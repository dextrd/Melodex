using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Artwork;
using Melodex.Core.Data;
using Melodex.Core.Plugins;

namespace Melodex.Core.Artwork;

/// <summary>A picture ready to serve.</summary>
public sealed record CachedArtwork(string Path, string ContentType, DateTime UpdatedAt);

/// <summary>
/// Pictures of artists, albums and editions, found through the artwork plugins the first time each is shown and kept
/// under <c>data/artwork</c>. Pages only ever load them from the core, so the browser never contacts the sources.
/// </summary>
public sealed class ArtworkCache(IServiceScopeFactory scopes, IOptions<MelodexOptions> options, ILogger<ArtworkCache> logger)
{
    /// <summary>How long "no plugin has a picture" is believed before asking again.</summary>
    public static readonly TimeSpan NoneFor = TimeSpan.FromDays(14);

    private readonly ConcurrentDictionary<(EntityKind, Guid), Task<CachedArtwork?>> _finding = new();

    private string Root => Path.Combine(options.Value.DataPath, "artwork");

    /// <summary>The pictures already cached for the subjects, without looking for any that aren't.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> CachedAsync(EntityKind kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var records = await db.Artwork.AsNoTracking().Where(a => a.EntityKind == kind && ids.Contains(a.EntityId) && a.File != null)
            .Select(a => new { a.EntityId, a.File }).ToListAsync(ct);
        return records.Select(r => (r.EntityId, Path: Path.Combine(Root, r.File!))).Where(r => File.Exists(r.Path)).ToDictionary(r => r.EntityId, r => r.Path);
    }

    public async Task<CachedArtwork?> GetAsync(EntityKind kind, Guid id, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            if (await db.Artwork.FindAsync([kind, id], ct) is { } record)
            {
                if (record.File is not null && File.Exists(Path.Combine(Root, record.File)))
                    return new CachedArtwork(Path.Combine(Root, record.File), record.ContentType ?? "image/jpeg", record.CheckedAt);
                if (record.File is null && DateTime.UtcNow - record.CheckedAt < NoneFor)
                    return null;
            }
        }

        // One search per picture, however many pages ask for it at once. It isn't cancelled with a request, so a
        // page that moves on doesn't waste what was found.
        return await _finding.GetOrAdd((kind, id), FindOnceAsync).WaitAsync(ct);
    }

    /// <summary>
    /// A picture of the subject from one artwork plugin, straight from it rather than the cache, for writing into files.
    /// Null when the plugin has none; failures throw.
    /// </summary>
    public async Task<ArtworkImage?> FetchFromAsync(Guid pluginInstanceId, EntityKind kind, Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        if (await QueryAsync(db, kind, id) is not { } query)
            return null;
        var plugins = await scope.ServiceProvider.GetRequiredService<ArtworkPlugins>().ForAsync(query.Subject, ct);
        if (plugins.FirstOrDefault(p => p.Instance.Id == pluginInstanceId) is not { Plugin: { } plugin })
            return null;
        foreach (var candidate in await plugin.FindAsync(query, ct))
            if (await plugin.DownloadAsync(candidate, ct) is { Data.Length: > 0 } image)
                return image;
        return null;
    }

    private async Task<CachedArtwork?> FindOnceAsync((EntityKind Kind, Guid Id) key)
    {
        try
        {
            return await FindAsync(key.Kind, key.Id);
        }
        finally
        {
            _finding.TryRemove(key, out _);
        }
    }

    private const string ChosenFolder = "chosen";

    /// <summary>
    /// Shows a picture someone chose for each of the subjects in place of what the plugins found, until another is
    /// chosen. Fetching the artwork afresh keeps it.
    /// </summary>
    public async Task<CachedArtwork> ChooseAsync(IReadOnlyCollection<(EntityKind Kind, Guid Id)> subjects, byte[] data, string contentType, CancellationToken ct)
    {
        var file = Path.Combine(ChosenFolder, Guid.NewGuid().ToString("N") + Extension(contentType));
        Directory.CreateDirectory(Path.Combine(Root, ChosenFolder));
        await File.WriteAllBytesAsync(Path.Combine(Root, file), data, ct);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var replaced = new List<string>();
        var now = DateTime.UtcNow;
        foreach (var (kind, id) in subjects.Distinct())
        {
            var record = await db.Artwork.FindAsync([kind, id], ct);
            if (record is null)
                db.Artwork.Add(record = new ArtworkRecord { EntityKind = kind, EntityId = id });
            else if (record is { Chosen: true, File: { } old })
                replaced.Add(old);
            (record.File, record.ContentType, record.PluginInstanceId, record.Chosen, record.CheckedAt) = (file, contentType, null, true, now);
        }
        await db.SaveChangesAsync(ct);

        // Pictures chosen before that nothing shows any more.
        var still = await db.Artwork.Where(a => a.Chosen && a.File != null && replaced.Contains(a.File)).Select(a => a.File!).ToListAsync(ct);
        foreach (var old in replaced.Except(still).Distinct())
            File.Delete(Path.Combine(Root, old));
        return new CachedArtwork(Path.Combine(Root, file), contentType, now);
    }

    /// <summary>Raised after <see cref="ResetAsync"/>, so pictures can be fetched again.</summary>
    public event Action? Reset;

    /// <summary>
    /// Forgets every picture the plugins found, so the next look uses them as they now are. Chosen ones stay.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        await db.Artwork.Where(a => !a.Chosen).ExecuteDeleteAsync(ct);
        if (Directory.Exists(Root))
        {
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(Root).Where(d => Path.GetFileName(d) != ChosenFolder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (IOException e)
            {
                logger.LogWarning("Couldn't clear the artwork folder: {Message}", e.Message);
            }
        }
        Reset?.Invoke();
    }

    private async Task<CachedArtwork?> FindAsync(EntityKind kind, Guid id)
    {
        await Task.Yield();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        if (await QueryAsync(db, kind, id) is not { } query)
            return null;

        var failed = false;
        foreach (var (instance, plugin) in await scope.ServiceProvider.GetRequiredService<ArtworkPlugins>().ForAsync(query.Subject, CancellationToken.None))
        {
            try
            {
                foreach (var candidate in await plugin.FindAsync(query, CancellationToken.None))
                {
                    if (await plugin.DownloadAsync(candidate, CancellationToken.None) is not { Data.Length: > 0 } image)
                        continue;
                    var file = Path.Combine(kind.ToString().ToLowerInvariant(), id.ToString("N") + Extension(image.ContentType));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, file))!);
                    await File.WriteAllBytesAsync(Path.Combine(Root, file), image.Data);
                    var found = await SaveAsync(db, kind, id, file, image.ContentType, instance.Id);
                    return new CachedArtwork(Path.Combine(Root, file), image.ContentType, found.CheckedAt);
                }
            }
            catch (Exception e)
            {
                // Try the next plugin, but don't remember there's nothing: this one may have it next time.
                failed = true;
                logger.LogWarning("{Plugin} couldn't find a picture of {Subject}: {Message}", instance.Name, query.Title, e.Message);
            }
        }

        if (!failed)
            await SaveAsync(db, kind, id, null, null, null);
        return null;
    }

    private static async Task<ArtworkRecord> SaveAsync(MelodexDbContext db, EntityKind kind, Guid id, string? file, string? contentType, Guid? plugin)
    {
        var record = await db.Artwork.FindAsync(kind, id);
        if (record is null)
            db.Artwork.Add(record = new ArtworkRecord { EntityKind = kind, EntityId = id });
        record.File = file;
        record.ContentType = contentType;
        record.PluginInstanceId = plugin;
        record.CheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return record;
    }

    /// <summary>What the plugins are told about the subject: its name and every ID the crosswalk has for it, its album and its artist.</summary>
    private static async Task<ArtworkQuery?> QueryAsync(MelodexDbContext db, EntityKind kind, Guid id)
    {
        async Task<IReadOnlyList<ProviderId>> IdsAsync(EntityKind of, Guid? entity) => entity is null
            ? []
            : await db.Crosswalk.Where(m => m.EntityKind == of && m.EntityId == entity).Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync();

        switch (kind)
        {
            case EntityKind.Artist when await db.Artists.FindAsync(id) is { } artist:
                return new ArtworkQuery(ArtworkSubject.Artist, artist.Name, null, await IdsAsync(EntityKind.Artist, id), [], [],
                    await db.Albums.Where(a => a.ArtistId == id).Select(a => a.Title).Distinct().Take(50).ToListAsync());
            case EntityKind.Album when await db.Albums.FindAsync(id) is { } album:
                return new ArtworkQuery(ArtworkSubject.Album, album.Title, album.ArtistCredit, await IdsAsync(EntityKind.Album, id), [],
                    await IdsAsync(EntityKind.Artist, album.ArtistId));
            case EntityKind.Edition when await db.Editions.Include(e => e.Album).FirstOrDefaultAsync(e => e.Id == id) is { } edition:
                return new ArtworkQuery(ArtworkSubject.Edition, edition.Title, edition.Album.ArtistCredit, await IdsAsync(EntityKind.Edition, id),
                    await IdsAsync(EntityKind.Album, edition.AlbumId), await IdsAsync(EntityKind.Artist, edition.Album.ArtistId));
            default:
                return null;
        }
    }

    private static string Extension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };
}
