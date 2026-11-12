using Microsoft.EntityFrameworkCore;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Data;
using Melodex.Core.Plugins;

namespace Melodex.Core.Catalogue;

/// <summary>
/// The catalogue cache. Reads go to the database; the index plugins are only called when something is unknown or
/// stale. Everything imported is keyed through the crosswalk, so provider IDs never leak into the rest of the core.
/// With several catalogues, each entity is fetched from the first catalogue that has an ID for it, so one that only a
/// gap-filling catalogue knows is kept up to date from that one.
/// </summary>
public sealed class CatalogueService(MelodexDbContext db, IndexPlugins indexPlugins, ILogger<CatalogueService> logger)
{
    public static readonly TimeSpan DiscographyTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan EditionsTtl = TimeSpan.FromDays(30);

    private IReadOnlyList<CatalogueIndex>? _indexes;

    /// <summary>The catalogues that are on, core catalogue first.</summary>
    public async Task<IReadOnlyList<CatalogueIndex>> IndexesAsync(CancellationToken ct) => _indexes ??= await indexPlugins.GetAllAsync(ct);

    /// <summary>Whether a catalogue is on. Without one, Melodex is a plain music library.</summary>
    public async Task<bool> HasCatalogueAsync(CancellationToken ct) => (await IndexesAsync(ct)).Count > 0;

    private async Task<CatalogueIndex> IndexAsync(CancellationToken ct) =>
        (await IndexesAsync(ct)).FirstOrDefault() ?? throw new NoCatalogueException();

    /// <summary>The core catalogue's plugin.</summary>
    public async Task<IIndexPlugin> PluginAsync(CancellationToken ct) => (await IndexAsync(ct)).Plugin;

    /// <summary>The catalogue whose IDs are in this scheme, if one is on.</summary>
    private async Task<CatalogueIndex?> IndexForAsync(string provider, CancellationToken ct) =>
        (await IndexesAsync(ct)).FirstOrDefault(i => i.Provider == provider);

    /// <summary>The entity's ID in the first catalogue, in the user's order, that has one for it.</summary>
    private async Task<(CatalogueIndex Index, ProviderId Id)?> SourceAsync(EntityKind kind, Guid entityId, CancellationToken ct)
    {
        var ids = await ProviderIdsAsync(kind, entityId, ct);
        foreach (var index in await IndexesAsync(ct))
            if (ids.FirstOrDefault(p => p.Provider == index.Provider) is { } id)
                return (index, id);
        return null;
    }

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
                SourcePluginInstanceId = (_indexes?.FirstOrDefault(i => i.Provider == id.Provider) ?? _indexes?.FirstOrDefault())?.Instance.Id,
            });
    }

    /// <summary>
    /// Records that an entity Melodex already has is the one with this ID in another catalogue, so importing from that
    /// catalogue adds to it instead of making a second one.
    /// </summary>
    public async Task PairAsync(EntityKind kind, Guid entityId, ProviderId id, CancellationToken ct)
    {
        if (await FindTrackedAsync(kind, id, ct) is not null)
            return;
        await MapAsync(kind, entityId, id, ct);
        await db.SaveChangesAsync(ct);
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

    /// <summary>
    /// Whether an entity is also in a catalogue earlier in the user's order than this provider's. Its details then stand:
    /// a gap-filling catalogue only adds its ID, so it can find the entity again.
    /// </summary>
    private async Task<bool> OutrankedAsync(EntityKind kind, Guid entityId, string provider, CancellationToken ct)
    {
        var indexes = await IndexesAsync(ct);
        var rank = indexes.Select(i => i.Provider).ToList().IndexOf(provider);
        if (rank <= 0)
            return false;
        var earlier = indexes.Take(rank).Select(i => i.Provider).ToList();
        return Tracked(db.Crosswalk).Any(m => m.EntityKind == kind && m.EntityId == entityId && earlier.Contains(m.Provider))
               || await db.Crosswalk.AnyAsync(m => m.EntityKind == kind && m.EntityId == entityId && earlier.Contains(m.Provider), ct);
    }

    private async Task<Guid?> FindTrackedAsync(EntityKind kind, ProviderId id, CancellationToken ct) =>
        Tracked(db.Crosswalk).FirstOrDefault(m => m.EntityKind == kind && m.Provider == id.Provider && m.Value == id.Value)?.EntityId
        ?? await FindAsync(kind, id, ct);

    // ---- Artists ----

    public async Task<Artist> ImportArtistAsync(IndexArtist source, CancellationToken ct) => await ImportArtistAsync(source, [], ct);

    /// <param name="albumTitles">Titles of albums of theirs, which tell apart artists with the same name.</param>
    private async Task<Artist> ImportArtistAsync(IndexArtist source, IReadOnlyCollection<string> albumTitles, CancellationToken ct)
    {
        var id = await FindTrackedAsync(EntityKind.Artist, source.Id, ct);
        var artist = id is { } existing ? await db.Artists.FindAsync([existing], ct) : null;
        if (artist is null && await SameArtistAsync(source.Name, source.Id.Provider, albumTitles, ct) is { } same)
        {
            await MapAsync(EntityKind.Artist, same.Id, source.Id, ct);
            return same;
        }
        if (artist is null)
        {
            artist = new Artist { Name = source.Name };
            db.Artists.Add(artist);
        }
        else if (await OutrankedAsync(EntityKind.Artist, artist.Id, source.Id.Provider, ct))
        {
            await MapAsync(EntityKind.Artist, artist.Id, source.Id, ct);
            return artist;
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
        if (await IndexForAsync(id.Provider, ct) is not { } index)
            return null;
        var source = await index.Plugin.GetArtistAsync(id.Value, ct);
        if (source is null)
            return null;
        var artist = await ImportArtistAsync(source, ct);
        await db.SaveChangesAsync(ct);
        return artist;
    }

    /// <summary>Gets the artist for a credit inside another entity, creating a stub if the index plugin isn't asked.</summary>
    /// <param name="albumTitle">The album the credit is on, if any, to tell apart artists with the same name.</param>
    private async Task<Artist> ArtistForRefAsync(ArtistRef reference, string? albumTitle, CancellationToken ct)
    {
        if (await FindTrackedAsync(EntityKind.Artist, reference.Id, ct) is { } known)
            return Tracked(db.Artists).FirstOrDefault(a => a.Id == known) ?? (await db.Artists.FindAsync([known], ct))!;
        return await ImportArtistAsync(new IndexArtist(reference.Id, reference.Name), albumTitle is null ? [] : [albumTitle], ct);
    }

    /// <summary>
    /// For an artist a gap-filling catalogue names, unknown by its ID: the core catalogue's artist with the same name,
    /// so there aren't two. Only when it's clear which: the one with an album of one of these titles, else the only one
    /// of that name, else the only one of that name in the library. Null for the core catalogue's own artists.
    /// </summary>
    private async Task<Artist?> SameArtistAsync(string name, string provider, IReadOnlyCollection<string> albumTitles, CancellationToken ct)
    {
        var indexes = await IndexesAsync(ct);
        if (indexes.Count == 0 || indexes[0].Provider == provider || TextMatch.NormaliseArtist(name) is not { Length: > 0 } key)
            return null;
        var core = indexes[0].Provider;
        var candidates = new List<Artist>();
        foreach (var artist in await NamedAsync(name, key, ct))
            if (await HasIdAsync(EntityKind.Artist, artist.Id, core, ct) && !await HasIdAsync(EntityKind.Artist, artist.Id, provider, ct))
                candidates.Add(artist);
        return await PickAsync(candidates, albumTitles, ct);
    }

    /// <summary>
    /// Artists only a gap-filling catalogue knows, made before imports looked for the core catalogue's artist of the same
    /// name, that are that artist: their IDs, albums, tracks, files and requests move to it, and they're removed. The
    /// number merged.
    /// </summary>
    public async Task<int> MergeDuplicateArtistsAsync(CancellationToken ct)
    {
        var indexes = await IndexesAsync(ct);
        if (indexes.Count == 0)
            return 0;
        var core = indexes[0].Provider;
        var stubs = await db.Artists
            .Where(a => db.Crosswalk.Any(m => m.EntityKind == EntityKind.Artist && m.EntityId == a.Id)
                        && !db.Crosswalk.Any(m => m.EntityKind == EntityKind.Artist && m.EntityId == a.Id && m.Provider == core))
            .ToListAsync(ct);
        var merged = 0;
        foreach (var stub in stubs)
        {
            if (TextMatch.NormaliseArtist(stub.Name) is not { Length: > 0 } key)
                continue;
            var providers = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && m.EntityId == stub.Id).Select(m => m.Provider).Distinct().ToListAsync(ct);
            var candidates = new List<Artist>();
            foreach (var artist in await NamedAsync(stub.Name, key, ct))
                if (artist.Id != stub.Id && await HasIdAsync(EntityKind.Artist, artist.Id, core, ct)
                    && !await db.Crosswalk.AnyAsync(m => m.EntityKind == EntityKind.Artist && m.EntityId == artist.Id && providers.Contains(m.Provider), ct))
                    candidates.Add(artist);
            var titles = await db.Albums.Where(a => a.ArtistId == stub.Id).Select(a => a.Title).ToListAsync(ct);
            if (await PickAsync(candidates, titles, ct) is not { } into)
                continue;
            await MergeArtistAsync(stub, into, ct);
            merged++;
        }
        if (merged > 0)
            logger.LogInformation("Merged {Count} artists a gap-filling catalogue had added twice", merged);
        return merged;
    }

    private async Task MergeArtistAsync(Artist from, Artist into, CancellationToken ct)
    {
        await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && m.EntityId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.EntityId, into.Id), ct);
        await db.Albums.Where(a => a.ArtistId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ArtistId, into.Id), ct);
        await db.Tracks.Where(t => t.ArtistId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ArtistId, into.Id), ct);
        await db.LibraryFiles.Where(f => f.ArtistId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.ArtistId, (Guid?)into.Id), ct);
        await db.Requests.Where(r => r.TargetKind == EntityKind.Artist && r.TargetId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.TargetId, into.Id), ct);
        await db.Artwork.Where(a => a.EntityKind == EntityKind.Artist && a.EntityId == from.Id).ExecuteDeleteAsync(ct);
        // What the context already holds is moved too, and the artist deleted directly: removed through the context, it
        // would take with it the albums the context still has as theirs.
        foreach (var album in Tracked(db.Albums).Where(a => a.ArtistId == from.Id))
            album.ArtistId = into.Id;
        foreach (var track in Tracked(db.Tracks).Where(t => t.ArtistId == from.Id))
            track.ArtistId = into.Id;
        foreach (var mapping in Tracked(db.Crosswalk).Where(m => m.EntityKind == EntityKind.Artist && m.EntityId == from.Id))
            mapping.EntityId = into.Id;
        db.Entry(from).State = EntityState.Detached;
        await db.Artists.Where(a => a.Id == from.Id).ExecuteDeleteAsync(ct);
        into.InLibrary |= from.InLibrary;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// For an album a gap-filling catalogue has, unknown by its ID: the core catalogue's album by the same artist with the
    /// same title, so there aren't two. Only when there's one. Null for the core catalogue's own albums.
    /// </summary>
    private async Task<Album?> SameAlbumAsync(Guid artistId, string title, string provider, CancellationToken ct)
    {
        var indexes = await IndexesAsync(ct);
        if (indexes.Count == 0 || indexes[0].Provider == provider || TextMatch.NormaliseAlbum(title) is not { Length: > 0 } key)
            return null;
        var core = indexes[0].Provider;
        var candidates = new List<Album>();
        foreach (var album in await AlbumsTitledAsync(artistId, key, ct))
            if (await HasIdAsync(EntityKind.Album, album.Id, core, ct) && !await HasIdAsync(EntityKind.Album, album.Id, provider, ct))
                candidates.Add(album);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>The artist's albums, saved or about to be, with this normalised title.</summary>
    private async Task<List<Album>> AlbumsTitledAsync(Guid artistId, string key, CancellationToken ct)
    {
        var saved = await db.Albums.Where(a => a.ArtistId == artistId).ToListAsync(ct);
        return saved.Concat(Tracked(db.Albums).Where(a => a.ArtistId == artistId)).DistinctBy(a => a.Id)
            .Where(a => TextMatch.NormaliseAlbum(a.Title) == key).ToList();
    }

    /// <summary>
    /// Albums only a gap-filling catalogue knows that are the core catalogue's album by the same artist with the same
    /// title, made before imports looked for it: their IDs, editions and requests move to it, and they're removed. The
    /// files on their editions stay on those editions, now the album's. The number merged.
    /// </summary>
    public async Task<int> MergeDuplicateAlbumsAsync(CancellationToken ct)
    {
        var indexes = await IndexesAsync(ct);
        if (indexes.Count == 0)
            return 0;
        var core = indexes[0].Provider;
        var copies = await db.Albums
            .Where(a => db.Crosswalk.Any(m => m.EntityKind == EntityKind.Album && m.EntityId == a.Id)
                        && !db.Crosswalk.Any(m => m.EntityKind == EntityKind.Album && m.EntityId == a.Id && m.Provider == core))
            .ToListAsync(ct);
        var merged = 0;
        foreach (var copy in copies)
        {
            if (TextMatch.NormaliseAlbum(copy.Title) is not { Length: > 0 } key)
                continue;
            var providers = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Album && m.EntityId == copy.Id).Select(m => m.Provider).Distinct().ToListAsync(ct);
            var candidates = new List<Album>();
            foreach (var album in await AlbumsTitledAsync(copy.ArtistId, key, ct))
                if (album.Id != copy.Id && await HasIdAsync(EntityKind.Album, album.Id, core, ct)
                    && !await db.Crosswalk.AnyAsync(m => m.EntityKind == EntityKind.Album && m.EntityId == album.Id && providers.Contains(m.Provider), ct))
                    candidates.Add(album);
            if (candidates.Count != 1)
                continue;
            await MergeAlbumAsync(copy, candidates[0], ct);
            merged++;
        }
        if (merged > 0)
            logger.LogInformation("Merged {Count} albums a gap-filling catalogue had added twice", merged);
        return merged;
    }

    private async Task MergeAlbumAsync(Album from, Album into, CancellationToken ct)
    {
        await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Album && m.EntityId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.EntityId, into.Id), ct);
        await db.Editions.Where(e => e.AlbumId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.AlbumId, into.Id), ct);
        await db.Requests.Where(r => r.TargetKind == EntityKind.Album && r.TargetId == from.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.TargetId, into.Id), ct);
        await db.Artwork.Where(a => a.EntityKind == EntityKind.Album && a.EntityId == from.Id).ExecuteDeleteAsync(ct);
        // As with artists: what the context holds moves too, and the album is deleted directly, keeping its editions.
        foreach (var edition in Tracked(db.Editions).Where(e => e.AlbumId == from.Id))
            edition.AlbumId = into.Id;
        foreach (var mapping in Tracked(db.Crosswalk).Where(m => m.EntityKind == EntityKind.Album && m.EntityId == from.Id))
            mapping.EntityId = into.Id;
        db.Entry(from).State = EntityState.Detached;
        await db.Albums.Where(a => a.Id == from.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Of artists with the same name, the one it's clear an artist with albums of these titles is.</summary>
    private async Task<Artist?> PickAsync(IReadOnlyList<Artist> candidates, IReadOnlyCollection<string> albumTitles, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return null;
        var titles = albumTitles.Select(t => TextMatch.NormaliseAlbum(t)).Where(t => t.Length > 0).ToHashSet();
        if (titles.Count > 0)
        {
            var withAlbum = new List<Artist>();
            foreach (var artist in candidates)
                if ((await db.Albums.Where(a => a.ArtistId == artist.Id).Select(a => a.Title).ToListAsync(ct)).Any(t => titles.Contains(TextMatch.NormaliseAlbum(t))))
                    withAlbum.Add(artist);
            if (withAlbum.Count == 1)
                return withAlbum[0];
        }
        if (candidates.Count == 1)
            return candidates[0];
        var inLibrary = candidates.Where(a => a.InLibrary).ToList();
        return inLibrary.Count == 1 ? inLibrary[0] : null;
    }

    /// <summary>Artists, saved or about to be, with this name, ignoring case, accents and punctuation.</summary>
    private async Task<List<Artist>> NamedAsync(string name, string key, CancellationToken ct)
    {
        var pattern = name.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        var saved = await db.Artists.Where(a => EF.Functions.Like(a.Name, pattern, @"\")).ToListAsync(ct);
        return saved.Concat(Tracked(db.Artists)).DistinctBy(a => a.Id).Where(a => TextMatch.NormaliseArtist(a.Name) == key).ToList();
    }

    private async Task<bool> HasIdAsync(EntityKind kind, Guid entityId, string provider, CancellationToken ct) =>
        Tracked(db.Crosswalk).Any(m => m.EntityKind == kind && m.EntityId == entityId && m.Provider == provider)
        || await db.Crosswalk.AnyAsync(m => m.EntityKind == kind && m.EntityId == entityId && m.Provider == provider, ct);

    /// <summary>
    /// Finds an artist by name: first among cached artists, then through the index plugin's search, accepting only
    /// an exact match on the normalised name or sort name. Returns null rather than guessing.
    /// </summary>
    public async Task<Artist?> ResolveArtistByNameAsync(string name, CancellationToken ct)
    {
        if (TextMatch.NormaliseArtist(name).Length == 0)
            return null;

        var cached = await db.Artists.Where(a => a.Name == name).OrderByDescending(a => a.InLibrary).FirstOrDefaultAsync(ct);
        if (cached is not null)
            return cached;

        var match = await FindArtistAsync((await IndexAsync(ct)).Plugin, name, ct);
        if (match is null)
        {
            logger.LogInformation("No artist named {Name} in the catalogue", name);
            return null;
        }
        var artist = await ImportArtistAsync(match, ct);
        await db.SaveChangesAsync(ct);
        return artist;
    }

    /// <summary>The artist a catalogue has with exactly this name, ignoring case, accents and punctuation, or its sort name.</summary>
    public static async Task<IndexArtist?> FindArtistAsync(IIndexPlugin plugin, string name, CancellationToken ct)
    {
        var key = TextMatch.NormaliseArtist(name);
        var results = await plugin.SearchAsync(new IndexSearchQuery(name, new HashSet<SearchKind> { SearchKind.Artist }, 10), ct);
        // A name in another script matches by its sort name: "Iwasaki, Taku" for 岩崎琢.
        return results.Artists.FirstOrDefault(a => TextMatch.NormaliseArtist(a.Name) == key)
               ?? results.Artists.FirstOrDefault(a => TextMatch.SortNameAsName(a.SortName) == key);
    }

    // ---- Albums ----

    /// <summary>Fetches the artist's discography if it was never fetched or is older than the TTL.</summary>
    public async Task RefreshDiscographyAsync(Artist artist, bool force, CancellationToken ct)
    {
        if (!force && artist.AlbumsRefreshedAt > DateTime.UtcNow - DiscographyTtl || await VariousArtists.IsAsync(db, artist.Id, ct))
            return;
        if (await SourceAsync(EntityKind.Artist, artist.Id, ct) is not var (index, providerId))
            return;

        var imported = 0;
        foreach (var source in await index.Plugin.GetArtistAlbumsAsync(providerId.Value, ct))
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
            artist ??= await ArtistForRefAsync(source.Artist, source.Title, ct);
            if (await SameAlbumAsync(artist.Id, source.Title, source.Id.Provider, ct) is { } same)
            {
                await MapAsync(EntityKind.Album, same.Id, source.Id, ct);
                return same;
            }
            album = new Album { ArtistId = artist.Id, Title = source.Title, ArtistCredit = source.ArtistCredit };
            db.Albums.Add(album);
        }
        else if (await OutrankedAsync(EntityKind.Album, album.Id, source.Id.Provider, ct))
        {
            await MapAsync(EntityKind.Album, album.Id, source.Id, ct);
            return album;
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

        if (await IndexForAsync(id.Provider, ct) is not { } index)
            return null;
        var detail = await index.Plugin.GetAlbumAsync(id.Value, ct);
        if (detail is null)
            return null;

        // An edition Melodex already has under this barcode is the same album from another catalogue: the album is
        // paired with it, and keeps the editions it has.
        foreach (var upc in detail.Editions.Select(e => e.Upc).OfType<string>())
        {
            if (await FindTrackedAsync(EntityKind.Edition, new ProviderId(Providers.Upc, upc), ct) is not { } editionId
                || await db.Editions.FindAsync([editionId], ct) is not { } edition)
                continue;
            var paired = (await db.Albums.FindAsync([edition.AlbumId], ct))!;
            await MapAsync(EntityKind.Album, paired.Id, detail.Album.Id, ct);
            await db.SaveChangesAsync(ct);
            await RefreshEditionsAsync(paired, false, ct);
            return paired;
        }

        var imported = await ImportAlbumAsync(detail.Album, null, ct);
        // The same album as the core catalogue's: it keeps the editions it has from there.
        if (await OutrankedAsync(EntityKind.Album, imported.Id, id.Provider, ct))
        {
            await db.SaveChangesAsync(ct);
            await RefreshEditionsAsync(imported, false, ct);
            return imported;
        }
        await ImportEditionsAsync(imported, detail.Editions, ct);
        await db.SaveChangesAsync(ct);
        return imported;
    }

    /// <summary>Fetches an album's editions and track listings if never fetched or older than the TTL.</summary>
    public async Task RefreshEditionsAsync(Album album, bool force, CancellationToken ct)
    {
        if (!force && album.EditionsRefreshedAt > DateTime.UtcNow - EditionsTtl)
            return;
        if (await SourceAsync(EntityKind.Album, album.Id, ct) is not var (index, providerId))
            return;

        var detail = await index.Plugin.GetAlbumAsync(providerId.Value, ct);
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
        // The same recording from another catalogue, by its ISRC: unless that track already has an ID of this
        // catalogue's own, which makes it a different recording that shares the code.
        foreach (var isrc in id is null ? source.Isrcs : [])
            if (await FindTrackedAsync(EntityKind.Track, new ProviderId(Providers.Isrc, isrc), ct) is { } byIsrc
                && !Tracked(db.Crosswalk).Any(m => m.EntityKind == EntityKind.Track && m.EntityId == byIsrc && m.Provider == source.Id.Provider)
                && !await db.Crosswalk.AnyAsync(m => m.EntityKind == EntityKind.Track && m.EntityId == byIsrc && m.Provider == source.Id.Provider, ct))
            {
                id = byIsrc;
                break;
            }
        var track = id is { } existing
            ? Tracked(db.Tracks).FirstOrDefault(t => t.Id == existing) ?? await db.Tracks.FindAsync([existing], ct)
            : null;
        if (track is null)
        {
            var artist = await ArtistForRefAsync(source.Artist, source.Album?.Title, ct);
            track = new Track { ArtistId = artist.Id, Title = source.Title, ArtistCredit = source.ArtistCredit };
            db.Tracks.Add(track);
        }
        else if (await OutrankedAsync(EntityKind.Track, track.Id, source.Id.Provider, ct))
        {
            track.Duration ??= source.Duration;
            await MapAsync(EntityKind.Track, track.Id, source.Id, ct);
            return track;
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

/// <summary>Something needs a catalogue, and none is on.</summary>
public sealed class NoCatalogueException() : InvalidOperationException(Message)
{
    public new const string Message = "This needs a catalogue, and none is set up. Add one, such as MusicBrainz, under Settings → Plugins.";
}
