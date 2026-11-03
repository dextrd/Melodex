using Microsoft.EntityFrameworkCore;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Data;
using Melodex.Core.Plugins;

namespace Melodex.Core.Catalogue;

/// <summary>
/// The catalogue cache. Reads go to the database; the index plugin is only called when something is unknown or
/// stale. Everything imported is keyed through the crosswalk, so provider IDs never leak into the rest of the core.
/// </summary>
public sealed class CatalogueService(MelodexDbContext db, IndexPlugins indexPlugins, ILogger<CatalogueService> logger)
{
    public static readonly TimeSpan DiscographyTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan EditionsTtl = TimeSpan.FromDays(30);

    private (Guid InstanceId, IIndexPlugin Plugin)? _index;

    private async Task<(Guid InstanceId, IIndexPlugin Plugin)> IndexAsync(CancellationToken ct)
    {
        if (_index is null)
        {
            var (instance, plugin) = await indexPlugins.GetPrimaryAsync(ct);
            _index = (instance.Id, plugin);
        }
        return _index.Value;
    }

    public async Task<IIndexPlugin> PluginAsync(CancellationToken ct) => (await IndexAsync(ct)).Plugin;

    // ---- Crosswalk ----

    public Task<Guid?> FindAsync(EntityKind kind, ProviderId id, CancellationToken ct) =>
        db.Crosswalk.Where(m => m.EntityKind == kind && m.Provider == id.Provider && m.Value == id.Value)
            .Select(m => (Guid?)m.EntityId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ProviderId>> ProviderIdsAsync(EntityKind kind, Guid entityId, CancellationToken ct) =>
        await db.Crosswalk.Where(m => m.EntityKind == kind && m.EntityId == entityId)
            .OrderBy(m => m.Provider).Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync(ct);

    private async Task MapAsync(EntityKind kind, Guid entityId, ProviderId id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id.Value))
            return;
        var exists = Tracked(db.Crosswalk).Any(m => m.EntityKind == kind && m.EntityId == entityId && m.Provider == id.Provider && m.Value == id.Value)
                     || await db.Crosswalk.AnyAsync(m => m.EntityKind == kind && m.EntityId == entityId && m.Provider == id.Provider && m.Value == id.Value, ct);
        if (!exists)
            db.Crosswalk.Add(new ProviderIdMapping
            {
                EntityKind = kind, EntityId = entityId, Provider = id.Provider, Value = id.Value,
                SourcePluginInstanceId = _index?.InstanceId,
            });
    }

    /// <summary>
    /// Entities the context already holds, without <c>Local</c>'s scan of every one for changes: an import of a big
    /// album looks things up thousands of times, and those scans made it quadratic.
    /// </summary>
    private IEnumerable<T> Tracked<T>(DbSet<T> set) where T : class
    {
        var detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            return set.Local;
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }
    }

    private async Task<Guid?> FindTrackedAsync(EntityKind kind, ProviderId id, CancellationToken ct) =>
        Tracked(db.Crosswalk).FirstOrDefault(m => m.EntityKind == kind && m.Provider == id.Provider && m.Value == id.Value)?.EntityId
        ?? await FindAsync(kind, id, ct);

    // ---- Artists ----

    public async Task<Artist> ImportArtistAsync(IndexArtist source, CancellationToken ct)
    {
        var id = await FindTrackedAsync(EntityKind.Artist, source.Id, ct);
        var artist = id is { } existing ? await db.Artists.FindAsync([existing], ct) : null;
        if (artist is null)
        {
            artist = new Artist { Name = source.Name };
            db.Artists.Add(artist);
        }
        artist.Name = source.Name;
        artist.SortName = source.SortName ?? artist.SortName;
        artist.Disambiguation = source.Disambiguation ?? artist.Disambiguation;
        artist.Country = source.Country ?? artist.Country;
        artist.RefreshedAt = DateTime.UtcNow;
        await MapAsync(EntityKind.Artist, artist.Id, source.Id, ct);
        foreach (var external in source.ExternalIds ?? [])
            await MapAsync(EntityKind.Artist, artist.Id, external, ct);
        return artist;
    }

    /// <summary>The artist with this provider ID, fetched from the index plugin if it isn't cached yet.</summary>
    public async Task<Artist?> GetArtistAsync(ProviderId id, CancellationToken ct)
    {
        if (await FindTrackedAsync(EntityKind.Artist, id, ct) is { } known)
            return await db.Artists.FindAsync([known], ct);
        var (_, plugin) = await IndexAsync(ct);
        var source = await plugin.GetArtistAsync(id.Value, ct);
        if (source is null)
            return null;
        var artist = await ImportArtistAsync(source, ct);
        await db.SaveChangesAsync(ct);
        return artist;
    }

    /// <summary>Gets the artist for a credit inside another entity, creating a stub if the index plugin isn't asked.</summary>
    private async Task<Artist> ArtistForRefAsync(ArtistRef reference, CancellationToken ct)
    {
        if (await FindTrackedAsync(EntityKind.Artist, reference.Id, ct) is { } known)
            return Tracked(db.Artists).FirstOrDefault(a => a.Id == known) ?? (await db.Artists.FindAsync([known], ct))!;
        return await ImportArtistAsync(new IndexArtist(reference.Id, reference.Name), ct);
    }

    /// <summary>
    /// Finds an artist by name: first among cached artists, then through the index plugin's search, accepting only
    /// an exact match on the normalised name or sort name. Returns null rather than guessing.
    /// </summary>
    public async Task<Artist?> ResolveArtistByNameAsync(string name, CancellationToken ct)
    {
        var key = TextMatch.NormaliseArtist(name);
        if (key.Length == 0)
            return null;

        var cached = await db.Artists.Where(a => a.Name == name).OrderByDescending(a => a.InLibrary).FirstOrDefaultAsync(ct);
        if (cached is not null)
            return cached;

        var (_, plugin) = await IndexAsync(ct);
        var results = await plugin.SearchAsync(new IndexSearchQuery(name, new HashSet<SearchKind> { SearchKind.Artist }, 10), ct);
        // A name in another script matches by its sort name: "Iwasaki, Taku" for 岩崎琢.
        var match = results.Artists.FirstOrDefault(a => TextMatch.NormaliseArtist(a.Name) == key)
                    ?? results.Artists.FirstOrDefault(a => TextMatch.SortNameAsName(a.SortName) == key);
        if (match is null)
        {
            logger.LogInformation("No artist named {Name} in the catalogue", name);
            return null;
        }
        var artist = await ImportArtistAsync(match, ct);
        await db.SaveChangesAsync(ct);
        return artist;
    }

    // ---- Albums ----

    /// <summary>Fetches the artist's discography if it was never fetched or is older than the TTL.</summary>
    public async Task RefreshDiscographyAsync(Artist artist, bool force, CancellationToken ct)
    {
        if (!force && artist.AlbumsRefreshedAt > DateTime.UtcNow - DiscographyTtl)
            return;
        var providerId = (await ProviderIdsAsync(EntityKind.Artist, artist.Id, ct)).FirstOrDefault(p => p.Provider == Providers.MusicBrainz);
        if (providerId is null)
            return;

        var (_, plugin) = await IndexAsync(ct);
        var imported = 0;
        foreach (var source in await plugin.GetArtistAlbumsAsync(providerId.Value, ct))
        {
            await ImportAlbumAsync(source, artist, ct);
            // Saved as it goes, so a page showing the artist meanwhile fills in.
            if (++imported % 50 == 0)
                await db.SaveChangesAsync(ct);
        }
        artist.AlbumsRefreshedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Album> ImportAlbumAsync(IndexAlbum source, Artist? artist, CancellationToken ct)
    {
        var id = await FindTrackedAsync(EntityKind.Album, source.Id, ct);
        var album = id is { } existing
            ? Tracked(db.Albums).FirstOrDefault(a => a.Id == existing) ?? await db.Albums.FindAsync([existing], ct)
            : null;
        if (album is null)
        {
            artist ??= await ArtistForRefAsync(source.Artist, ct);
            album = new Album { ArtistId = artist.Id, Title = source.Title, ArtistCredit = source.ArtistCredit };
            db.Albums.Add(album);
        }
        album.Title = source.Title;
        album.ArtistCredit = source.ArtistCredit;
        album.Type = source.Type;
        album.SecondaryTypes = [.. source.SecondaryTypes];
        album.FirstReleaseDate = source.FirstReleaseDate;
        album.HasOfficialRelease = source.HasOfficialRelease ?? album.HasOfficialRelease;
        album.RefreshedAt = DateTime.UtcNow;
        await MapAsync(EntityKind.Album, album.Id, source.Id, ct);
        foreach (var external in source.ExternalIds ?? [])
            await MapAsync(EntityKind.Album, album.Id, external, ct);
        return album;
    }

    /// <summary>
    /// The album as the core knows it, or imported from partial details, such as the album a search result's track is
    /// from: what's already known isn't overwritten with them.
    /// </summary>
    public async Task<Album> ImportAlbumUnlessKnownAsync(IndexAlbum source, CancellationToken ct) =>
        await FindTrackedAsync(EntityKind.Album, source.Id, ct) is { } known
            ? Tracked(db.Albums).FirstOrDefault(a => a.Id == known) ?? (await db.Albums.FindAsync([known], ct))!
            : await ImportAlbumAsync(source, null, ct);

    /// <summary>The album with this provider ID, with its editions loaded, fetching whatever isn't cached.</summary>
    public async Task<Album?> GetAlbumAsync(ProviderId id, CancellationToken ct)
    {
        if (await FindTrackedAsync(EntityKind.Album, id, ct) is { } known)
        {
            var album = (await db.Albums.FindAsync([known], ct))!;
            await RefreshEditionsAsync(album, false, ct);
            return album;
        }

        var (_, plugin) = await IndexAsync(ct);
        var detail = await plugin.GetAlbumAsync(id.Value, ct);
        if (detail is null)
            return null;
        var imported = await ImportAlbumAsync(detail.Album, null, ct);
        await ImportEditionsAsync(imported, detail.Editions, ct);
        await db.SaveChangesAsync(ct);
        return imported;
    }

    /// <summary>Fetches an album's editions and track listings if never fetched or older than the TTL.</summary>
    public async Task RefreshEditionsAsync(Album album, bool force, CancellationToken ct)
    {
        if (!force && album.EditionsRefreshedAt > DateTime.UtcNow - EditionsTtl)
            return;
        var providerId = (await ProviderIdsAsync(EntityKind.Album, album.Id, ct)).FirstOrDefault(p => p.Provider == Providers.MusicBrainz);
        if (providerId is null)
            return;

        var (_, plugin) = await IndexAsync(ct);
        var detail = await plugin.GetAlbumAsync(providerId.Value, ct);
        if (detail is null)
            return;
        await ImportAlbumAsync(detail.Album, null, ct);
        await ImportEditionsAsync(album, detail.Editions, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task ImportEditionsAsync(Album album, IReadOnlyList<IndexEdition> editions, CancellationToken ct)
    {
        foreach (var source in editions)
        {
            var id = await FindTrackedAsync(EntityKind.Edition, source.Id, ct);
            var edition = id is { } existing
                ? await db.Editions.Include(e => e.Tracks).FirstOrDefaultAsync(e => e.Id == existing, ct)
                : null;
            if (edition is null)
            {
                edition = new Edition { AlbumId = album.Id, Title = source.Title };
                db.Editions.Add(edition);
            }
            edition.AlbumId = album.Id;
            edition.Title = source.Title;
            edition.ReleaseDate = source.ReleaseDate;
            edition.Country = source.Country;
            edition.Status = source.Status;
            edition.Disambiguation = source.Disambiguation;
            edition.Formats = string.Join(", ", source.Media.Select(m => m.Format ?? "Unknown"));
            edition.TrackCount = source.Media.Sum(m => m.Tracks.Count);
            await MapAsync(EntityKind.Edition, edition.Id, source.Id, ct);
            if (source.Upc is { } upc)
                await MapAsync(EntityKind.Edition, edition.Id, new ProviderId(Providers.Upc, upc), ct);

            // Update listings in place: removing and re-adding the same (disc, position) key in one save isn't allowed.
            var existingListings = edition.Tracks.ToDictionary(t => (t.Disc, t.Position));
            var seen = new HashSet<(int, int)>();
            foreach (var medium in source.Media)
            foreach (var listing in medium.Tracks)
            {
                if (!seen.Add((medium.Position, listing.Position)))
                    continue;
                var track = await ImportTrackAsync(listing.Track, ct);
                if (!existingListings.TryGetValue((medium.Position, listing.Position), out var row))
                {
                    row = new EditionTrack { EditionId = edition.Id, Disc = medium.Position, Position = listing.Position, Number = listing.Number, Title = listing.Title };
                    edition.Tracks.Add(row);
                }
                row.TrackId = track.Id;
                row.Number = listing.Number;
                row.Title = listing.Title;
                row.Duration = listing.Duration;
            }
            foreach (var stale in edition.Tracks.Where(t => !seen.Contains((t.Disc, t.Position))).ToList())
            {
                edition.Tracks.Remove(stale);
                db.EditionTracks.Remove(stale);
            }
        }
        album.EditionsRefreshedAt = DateTime.UtcNow;
    }

    public async Task<Track> ImportTrackAsync(IndexTrack source, CancellationToken ct)
    {
        var id = await FindTrackedAsync(EntityKind.Track, source.Id, ct);
        var track = id is { } existing
            ? Tracked(db.Tracks).FirstOrDefault(t => t.Id == existing) ?? await db.Tracks.FindAsync([existing], ct)
            : null;
        if (track is null)
        {
            var artist = await ArtistForRefAsync(source.Artist, ct);
            track = new Track { ArtistId = artist.Id, Title = source.Title, ArtistCredit = source.ArtistCredit };
            db.Tracks.Add(track);
        }
        track.Title = source.Title;
        track.ArtistCredit = source.ArtistCredit;
        track.Duration = source.Duration ?? track.Duration;
        await MapAsync(EntityKind.Track, track.Id, source.Id, ct);
        foreach (var isrc in source.Isrcs)
            await MapAsync(EntityKind.Track, track.Id, new ProviderId(Providers.Isrc, isrc), ct);
        foreach (var external in source.ExternalIds ?? [])
            await MapAsync(EntityKind.Track, track.Id, external, ct);
        return track;
    }
}
