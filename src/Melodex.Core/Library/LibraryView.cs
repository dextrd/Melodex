using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

/// <summary>
/// Owned and missing, computed by comparing catalogue albums with library files. Nothing here is stored as truth:
/// it's all derived from the catalogue, the files' matches and the settings. Music of your own that no catalogue knows
/// (<see cref="LocalMusic"/>) joins the library's lists as its tags say.
/// </summary>
public sealed partial class LibraryView(MelodexDbContext db, SettingsService settingsService, LocalMusic local)
{
    private sealed record Listing(Guid EditionId, int Disc, int Position, string Number, string Title, Guid TrackId, string ArtistCredit, TimeSpan? Duration);

    private sealed record OwnedFile(Guid Id, string Path, Guid TrackId, Guid? EditionId);

    /// <summary>What's in the library, loaded once for a set of artists.</summary>
    private sealed class Snapshot
    {
        public required LibrarySettings Settings { get; init; }
        public required List<Album> Albums { get; init; }
        public required ILookup<Guid, Edition> EditionsByAlbum { get; init; }
        public required ILookup<Guid, Listing> ListingsByEdition { get; init; }
        public required ILookup<Guid, OwnedFile> FilesByEdition { get; init; }
        public required ILookup<Guid, OwnedFile> FilesByTrack { get; init; }

        /// <summary>Tracks owned anywhere in the library, including recordings that share an ISRC with an owned one.</summary>
        public required HashSet<Guid> OwnedAnywhere { get; init; }

        /// <summary>Ignored tracks, including recordings that share an ISRC with an ignored one.</summary>
        public required HashSet<Guid> Ignored { get; init; }

        public required ILookup<Guid, ProviderId> AlbumIds { get; init; }

        /// <summary>How the missing view treats each album, by its kinds.</summary>
        public required Dictionary<Guid, ReleaseShowing> Showing { get; init; }

        /// <summary>
        /// The releases shown in full, or that you have, each song is on: by recording, and by artist and title. Only
        /// worked out when some kind is shown for new songs only.
        /// </summary>
        public required ILookup<Guid, Guid> FullReleasesByTrack { get; init; }

        public required ILookup<(Guid ArtistId, string Title), Guid> FullReleasesByTitle { get; init; }
    }

    /// <param name="onlyAlbums">
    /// For a page about a few albums, such as an album's or a song's: just these, and only the files that are theirs, so
    /// the page needn't wait on the whole library or every album the artist has. Albums shown for new songs only still
    /// need the songs of their artist's albums shown in full, which are read on their own.
    /// </param>
    private async Task<Snapshot> LoadAsync(IReadOnlyCollection<Guid> artistIds, CancellationToken ct, IReadOnlyCollection<Guid>? onlyAlbums = null,
        bool scoped = false)
    {
        var settings = await settingsService.GetLibraryAsync(ct);
        // Various Artists' albums are the compilations you have, not every one the catalogue has seen. They're found from
        // the files, as asking of each of its albums whether you have it takes a while.
        var various = onlyAlbums is null ? (await VariousArtists.IdsAsync(db, ct)).Where(artistIds.Contains).ToList() : [];
        var compilations = various.Count > 0
            ? await db.LibraryFiles.Where(f => f.EditionId != null && various.Contains(f.Edition!.Album.ArtistId)).Select(f => f.Edition!.AlbumId).Distinct().ToListAsync(ct)
            : [];
        var others = artistIds.Except(various).ToList();
        // Read only, so the many albums and editions of a whole library aren't tracked for changes.
        var albums = onlyAlbums is null
            ? [.. await db.Albums.AsNoTracking().Where(a => others.Contains(a.ArtistId)).ToListAsync(ct),
               .. await db.Albums.AsNoTracking().Where(a => compilations.Contains(a.Id)).ToListAsync(ct)]
            : await db.Albums.AsNoTracking().Where(a => onlyAlbums.Contains(a.Id)).ToListAsync(ct);
        if (!scoped && onlyAlbums is null)
        {
            // The whole library's lists count an album hidden from the missing view only when you have it, so the songs
            // of the others, often half the catalogue, aren't read.
            var have = await db.LibraryFiles.Where(f => f.EditionId != null).Select(f => f.Edition!.AlbumId).Distinct().ToHashSetAsync(ct);
            albums = albums.Where(a => have.Contains(a.Id) || settings.ShowingOf(a) != ReleaseShowing.Hide).ToList();
        }
        scoped |= onlyAlbums is not null;
        var albumIds = albums.Select(a => a.Id).ToList();
        var editions = await db.Editions.AsNoTracking().Where(e => albumIds.Contains(e.AlbumId)).ToListAsync(ct);
        var editionIds = editions.Select(e => e.Id).ToList();
        // A page shows each song's credit and length, read from the song where its listing has none. The whole library's
        // lists need only which songs each edition has, so they don't read every song too.
        var listings = scoped
            ? await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
                .Select(et => new Listing(et.EditionId, et.Disc, et.Position, et.Number, et.Title, et.TrackId, et.Track.ArtistCredit, et.Duration ?? et.Track.Duration))
                .ToListAsync(ct)
            : await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
                .Select(et => new Listing(et.EditionId, et.Disc, et.Position, "", et.Title, et.TrackId, "", null))
                .ToListAsync(ct);
        var listingTracks = listings.Select(l => l.TrackId).Distinct().ToList();

        // A page about an artist or a few albums needs only their songs' files and those filed under their editions.
        var files = scoped
            ? await db.LibraryFiles.Where(f => f.TrackId != null && (listingTracks.Contains(f.TrackId.Value) || f.EditionId != null && editionIds.Contains(f.EditionId.Value)))
                .Select(f => new OwnedFile(f.Id, f.Path, f.TrackId!.Value, f.EditionId)).ToListAsync(ct)
            : await db.LibraryFiles.Where(f => f.TrackId != null)
                .Select(f => new OwnedFile(f.Id, f.Path, f.TrackId!.Value, f.EditionId)).ToListAsync(ct);
        var owned = files.Select(f => f.TrackId).ToHashSet();

        // Different recordings sharing an ISRC are the same song as far as owning it goes: a song here is owned when
        // a recording with one of its ISRCs is in the library.
        // Worked out from the files, which are few, rather than from every song listed, which on a big library are many.
        var isrcs = db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.Isrc);
        var filedTracks = db.LibraryFiles.Where(f => f.TrackId != null).Select(f => f.TrackId!.Value);
        var ownedIsrcs = await isrcs.Where(m => filedTracks.Contains(m.EntityId)).Select(m => m.Value).Distinct().ToListAsync(ct);
        owned.UnionWith(await isrcs.Where(m => ownedIsrcs.Contains(m.Value)).Select(m => m.EntityId).ToListAsync(ct));

        var ignored = await db.Tracks.Where(t => t.Ignored).Select(t => t.Id).ToHashSetAsync(ct);
        if (ignored.Count > 0)
        {
            var ignoredIsrcs = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.Isrc && ignored.Contains(m.EntityId))
                .Select(m => m.Value).Distinct().ToListAsync(ct);
            ignored.UnionWith(await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.Isrc
                                                          && ignoredIsrcs.Contains(m.Value) && listingTracks.Contains(m.EntityId))
                .Select(m => m.EntityId).ToListAsync(ct));
        }

        var providerIds = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Album && albumIds.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct);

        var showing = albums.ToDictionary(a => a.Id, a => settings.ShowingOf(a));
        var full = new List<(Guid TrackId, string Title, Album Album)>();
        if (showing.ContainsValue(ReleaseShowing.NewSongsOnly) && onlyAlbums is null)
        {
            var albumsById = albums.ToDictionary(a => a.Id);
            var albumOfEdition = editions.ToDictionary(e => e.Id, e => albumsById[e.AlbumId]);
            var ownedAlbums = files.Where(f => f.EditionId is { } e && albumOfEdition.ContainsKey(e))
                .Select(f => albumOfEdition[f.EditionId!.Value].Id).ToHashSet();
            full = listings.Select(l => (l.TrackId, l.Title, Album: albumOfEdition[l.EditionId]))
                .Where(x => showing[x.Album.Id] == ReleaseShowing.Show || ownedAlbums.Contains(x.Album.Id)).ToList();
        }
        else if (showing.ContainsValue(ReleaseShowing.NewSongsOnly))
        {
            // Just a few albums: whether a song is new needs only the songs of their artists' albums shown in full or owned.
            var theirs = albums.Select(a => a.ArtistId).Distinct().ToList();
            var candidates = await db.Albums.Where(a => theirs.Contains(a.ArtistId)).ToListAsync(ct);
            var ownedAlbums = await db.LibraryFiles.Where(f => f.EditionId != null && theirs.Contains(f.Edition!.Album.ArtistId))
                .Select(f => f.Edition!.AlbumId).Distinct().ToListAsync(ct);
            var fully = candidates.Where(a => settings.ShowingOf(a) == ReleaseShowing.Show || ownedAlbums.Contains(a.Id)).ToDictionary(a => a.Id);
            var fullIds = fully.Keys.ToList();
            full = (await db.EditionTracks.Where(et => fullIds.Contains(et.Edition.AlbumId))
                    .Select(et => new { et.TrackId, et.Title, et.Edition.AlbumId }).ToListAsync(ct))
                .Select(x => (x.TrackId, x.Title, Album: fully[x.AlbumId])).ToList();
        }

        return new Snapshot
        {
            Settings = settings,
            Albums = albums,
            EditionsByAlbum = editions.ToLookup(e => e.AlbumId),
            ListingsByEdition = listings.ToLookup(l => l.EditionId),
            FilesByEdition = files.Where(f => f.EditionId != null).ToLookup(f => f.EditionId!.Value),
            FilesByTrack = files.ToLookup(f => f.TrackId),
            OwnedAnywhere = owned,
            Ignored = ignored,
            AlbumIds = providerIds.ToLookup(p => p.EntityId, p => new ProviderId(p.Provider, p.Value)),
            Showing = showing,
            FullReleasesByTrack = full.Select(x => (x.TrackId, x.Album.Id)).Distinct().ToLookup(x => x.TrackId, x => x.Id),
            FullReleasesByTitle = full.Select(x => (Key: (x.Album.ArtistId, SongKey(x.Title)), x.Album.Id)).Distinct().ToLookup(x => x.Key, x => x.Id),
        };
    }

    /// <summary>
    /// A song's title as a person would compare them: case, spacing, curly quotes and "&" for "and" aside, without notes on the
    /// version at the end, such as "(Live)", "[2011 remaster]" or " - Radio Edit". Empty for the catalogue's
    /// placeholders, such as "[untitled]", which aren't songs.
    /// </summary>
    public static string SongKey(string title)
    {
        var key = string.Join(' ', title.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('\u2019', '\'').Replace('\u2018', '\'').Replace("&", " and ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string before;
        do
        {
            before = key;
            key = VersionNote().Replace(key, "").Trim();
        }
        while (key != before && key.Length > 0);
        return key;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\s*\([^()]*\)|\s*\[[^\[\]]*\]|\s+[-\u2013\u2014]\s+[^-\u2013\u2014]*)$")]
    private static partial System.Text.RegularExpressions.Regex VersionNote();

    /// <summary>
    /// A song on a release shown for new songs only that you have, or that's on another release shown in full or one you
    /// have, by recording or by title. It doesn't count against this release.
    /// </summary>
    private static bool Elsewhere(Snapshot s, Album album, Listing listing) =>
        s.Showing[album.Id] == ReleaseShowing.NewSongsOnly
        && (s.OwnedAnywhere.Contains(listing.TrackId)
            || s.FullReleasesByTrack[listing.TrackId].Any(a => a != album.Id)
            || SongKey(listing.Title) is not { Length: > 0 } key
            || s.FullReleasesByTitle[(album.ArtistId, key)].Any(a => a != album.Id));

    /// <param name="AllIgnored">Every track not owned on the shown edition is ignored, so there's nothing to fetch.</param>
    /// <param name="NothingNew">Shown for new songs only, and every song on it is elsewhere, ignored or yours.</param>
    private sealed record Evaluation(Album Album, AlbumStatus Status, Edition? OwnedEdition, Edition? ShownEdition, int? OwnedTracks, int? TotalTracks,
        bool AllIgnored = false, bool NothingNew = false);

    private static Evaluation Evaluate(Snapshot s, Album album)
    {
        var editions = s.EditionsByAlbum[album.Id].ToList();
        var editionIds = editions.Select(e => e.Id).ToHashSet();
        var ownedEdition = editions
            .Select(e => (Edition: e, Files: s.FilesByEdition[e.Id].Count()))
            .Where(x => x.Files > 0)
            .OrderByDescending(x => x.Files).ThenByDescending(x => x.Edition.TrackCount)
            .Select(x => x.Edition).FirstOrDefault();

        if (ownedEdition is not null)
        {
            var listings = s.ListingsByEdition[ownedEdition.Id].ToList();
            var statuses = listings.Select(l => TrackStatusOf(s, album, l, editionIds)).ToList();
            var missing = statuses.Count(t => t == TrackStatus.Missing);
            var counted = statuses.Count(t => t is not (TrackStatus.Ignored or TrackStatus.Elsewhere));
            return new Evaluation(album, missing == 0 ? AlbumStatus.Complete : AlbumStatus.Partial, ownedEdition, ownedEdition,
                counted - missing, counted);
        }

        // Until its songs are known, a release shown for new songs only can't be said to have any.
        if (album.EditionsRefreshedAt is null || editions.Count == 0)
            return new Evaluation(album, AlbumStatus.Missing, null, null, null, null, NothingNew: s.Showing[album.Id] == ReleaseShowing.NewSongsOnly);

        // Not owned: find the edition most covered by recordings owned elsewhere, preferring official editions.
        // Ignored tracks don't count, owned or not, and nor do songs elsewhere on a release shown for new songs only.
        var counts = s.Settings.CountsOwnedAnywhere;
        var official = editions.Where(e => string.Equals(e.Status, "Official", StringComparison.OrdinalIgnoreCase)).ToList();
        var best = (official.Count > 0 ? official : editions)
            .Select(e =>
            {
                var tracks = s.ListingsByEdition[e.Id].ToList();
                var owned = counts ? tracks.Count(t => s.OwnedAnywhere.Contains(t.TrackId)) : 0;
                var skipped = tracks.Count(t => !(counts && s.OwnedAnywhere.Contains(t.TrackId)) && (s.Ignored.Contains(t.TrackId) || Elsewhere(s, album, t)));
                var fresh = tracks.Count(t => !s.Ignored.Contains(t.TrackId) && !Elsewhere(s, album, t));
                return (Edition: e, Listed: tracks.Count, Total: tracks.Count - skipped, Owned: owned, New: fresh);
            })
            .Where(x => x.Listed > 0)
            .OrderByDescending(x => x.Owned == x.Total).ThenByDescending(x => x.Owned).ThenByDescending(x => x.Total)
            .FirstOrDefault();
        if (best.Edition is null)
            return new Evaluation(album, AlbumStatus.Missing, null, null, null, null, NothingNew: s.Showing[album.Id] == ReleaseShowing.NewSongsOnly);

        var covered = counts && best.Owned > 0 && best.Owned == best.Total;
        var nothingNew = s.Showing[album.Id] == ReleaseShowing.NewSongsOnly && best.New == 0;
        return new Evaluation(album, covered ? AlbumStatus.Covered : AlbumStatus.Missing, null, best.Edition,
            best.Owned, best.Total, AllIgnored: best.Owned == best.Total && !covered && !nothingNew, NothingNew: nothingNew);
    }

    private static TrackStatus TrackStatusOf(Snapshot s, Album album, Listing listing, HashSet<Guid> albumEditions)
    {
        // Owned here if a file for this recording sits on any edition of this album.
        if (s.FilesByTrack[listing.TrackId].Any(f => f.EditionId is { } e && albumEditions.Contains(e)))
            return TrackStatus.Owned;
        if (s.Settings.CountsOwnedAnywhere && s.OwnedAnywhere.Contains(listing.TrackId))
            return TrackStatus.OwnedElsewhere;
        if (s.Ignored.Contains(listing.TrackId))
            return TrackStatus.Ignored;
        return Elsewhere(s, album, listing) ? TrackStatus.Elsewhere : TrackStatus.Missing;
    }

    private static AlbumSummary Summarise(Snapshot s, Evaluation e) => new(
        e.Album.Id, e.Album.ArtistId, e.Album.Title, e.Album.ArtistCredit, e.Album.Type, e.Album.SecondaryTypes, e.Album.FirstReleaseDate,
        e.Status, HiddenByDefault(s, e), e.Album.Ignored || e.AllIgnored, e.OwnedTracks, e.TotalTracks, s.AlbumIds[e.Album.Id].ToList(),
        AlbumCover(s, e));

    // The owned files' own cover when the settings prefer it (falling back to the catalogue's), else the catalogue's.
    private static string AlbumCover(Snapshot s, Evaluation e) =>
        s.Settings.PreferFileCovers && e.OwnedEdition is { } owned && s.FilesByEdition[owned.Id].OrderBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault() is { } file
            ? LocalMusic.CoverUrl(file.Id)
            : CoverUrl(e.Album.Id, e.OwnedEdition?.Id);

    /// <summary>
    /// The cover, served by the core from the artwork plugins: the edition you have, or else the album's usual one. Some
    /// have none (a 404), so the web app keeps a placeholder behind it.
    /// </summary>
    public static string CoverUrl(Guid albumId, Guid? editionId) =>
        editionId is { } edition ? $"api/v1/artwork/edition/{edition}" : $"api/v1/artwork/album/{albumId}";

    /// <summary>The artist's picture, served by the core like covers.</summary>
    public static string ArtistImageUrl(Guid artistId) => $"api/v1/artwork/artist/{artistId}";

    private static bool HiddenByDefault(Snapshot s, Evaluation e) => s.Showing[e.Album.Id] == ReleaseShowing.Hide || e.NothingNew;

    private static bool IsListedMissing(Snapshot s, Evaluation e) =>
        e.Status == AlbumStatus.Missing && !HiddenByDefault(s, e) && !e.Album.Ignored && !e.AllIgnored;

    private static IEnumerable<Evaluation> Ordered(IEnumerable<Evaluation> evaluations) =>
        evaluations.OrderBy(e => e.Album.FirstReleaseDate ?? "9999").ThenBy(e => e.Album.Title);

    // ---- Queries ----

    public async Task<IReadOnlyList<LibraryArtist>> GetLibraryArtistsAsync(CancellationToken ct)
    {
        var artists = await db.Artists.Where(a => a.InLibrary).ToListAsync(ct);
        var ids = artists.Select(a => a.Id).ToList();
        var s = await LoadAsync(ids, ct);
        var evaluations = s.Albums.Select(a => Evaluate(s, a)).ToLookup(e => e.Album.ArtistId);
        var files = await db.LibraryFiles.Where(f => f.ArtistId != null).GroupBy(f => f.ArtistId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var artistIds = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && ids.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct);
        var idLookup = artistIds.ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));
        var own = await local.LoadAsync(ct);
        var ownAlbums = own.Albums.ToLookup(a => a.ArtistId);

        return artists
            .Select(a =>
            {
                var albums = evaluations[a.Id].ToList();
                return new LibraryArtist(a.Id, a.Name, a.SortName, a.Disambiguation, idLookup[a.Id].ToList(),
                    albums.Count(e => e.Status is AlbumStatus.Complete or AlbumStatus.Partial) + ownAlbums[a.Id].Count(),
                    albums.Count(e => IsListedMissing(s, e)),
                    albums.Count(e => e.Status == AlbumStatus.Covered && !HiddenByDefault(s, e)),
                    files.GetValueOrDefault(a.Id) + ownAlbums[a.Id].Sum(x => x.Songs.Count),
                    ArtistImageUrl(a.Id),
                    PartialAlbums: albums.Count(e => e.Status == AlbumStatus.Partial && !e.Album.Ignored));
            })
            .Concat(own.Artists.Select(a => new LibraryArtist(a.Id, a.Name, null, null, [], a.Albums.Count, 0, 0, a.Albums.Sum(x => x.Songs.Count),
                a.Albums[0].CoverUrl, Local: true)))
            .OrderBy(a => a.SortName ?? a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<LibraryAlbum>> GetLibraryAlbumsAsync(CancellationToken ct)
    {
        var artists = await db.Artists.Where(a => a.InLibrary).ToDictionaryAsync(a => a.Id, ct);
        // Only albums you have are listed, so only theirs are read, not their artists' whole discographies.
        var have = await db.LibraryFiles.Where(f => f.TrackId != null && f.EditionId != null && f.Edition!.Album.Artist.InLibrary)
            .Select(f => f.Edition!.AlbumId).Distinct().ToListAsync(ct);
        var s = await LoadAsync([], ct, onlyAlbums: have);
        var own = await local.LoadAsync(ct);
        return s.Albums.Select(a => Evaluate(s, a)).Where(e => e.Status is AlbumStatus.Complete or AlbumStatus.Partial)
            .Select(e => new LibraryAlbum(artists[e.Album.ArtistId].Name, Summarise(s, e)))
            .Concat(own.Albums.Select(a => new LibraryAlbum(a.ArtistLocal ? a.ArtistName : artists[a.ArtistId].Name, a.Summary())))
            .OrderBy(a => a.Album.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => artists.GetValueOrDefault(a.Album.ArtistId)?.SortName ?? a.ArtistName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Album.FirstReleaseDate ?? "9999", StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<LibraryTrack>> GetLibraryTracksAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetLibraryAsync(ct);
        var rows = await db.LibraryFiles.Where(f => f.TrackId != null)
            .Select(f => new
            {
                f.Id, f.Path, f.Format, f.Duration, f.EditionId, TrackId = f.TrackId!.Value, f.TagsJson,
                f.Track!.Title, f.Track.ArtistCredit, f.Track.ArtistId, TrackDuration = f.Track.Duration,
                AlbumId = (Guid?)f.Edition!.AlbumId, AlbumTitle = f.Edition.Album.Title,
            })
            .ToListAsync(ct);
        // Where each file sits on its edition, read for just those editions: asked inside the query above, SQLite numbers
        // every listing in the catalogue to pick each file's first.
        var editionIds = rows.Select(r => r.EditionId).OfType<Guid>().Distinct().ToList();
        var listings = (await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
                .Select(et => new { et.EditionId, et.TrackId, et.Disc, et.Position, et.Number }).ToListAsync(ct))
            .OrderBy(et => et.Disc).ThenBy(et => et.Position)
            .DistinctBy(et => (et.EditionId, et.TrackId))
            .ToDictionary(et => (et.EditionId, et.TrackId));

        var own = await local.LoadAsync(ct);
        // Songs matched on their own show on an album they're on.
        var places = await LooseSongs.PlacesAsync(db, rows.Where(r => r.EditionId is null).GroupBy(r => r.TrackId)
            .ToDictionary(g => g.Key, g => g.Select(r => FileTags.Parse(r.TagsJson).Album).FirstOrDefault(a => a is not null)), ct);
        return rows
            .Select(r =>
            {
                var tags = settings.PreferFileTags ? FileTags.Parse(r.TagsJson) : null;
                var place = r.EditionId is null ? places.GetValueOrDefault(r.TrackId) : null;
                var albumId = r.AlbumId ?? place?.AlbumId;
                var listing = r.EditionId is { } edition ? listings.GetValueOrDefault((edition, r.TrackId)) : null;
                return new LibraryTrack(r.Id, r.TrackId, tags?.Title ?? r.Title, tags?.Artist ?? r.ArtistCredit, r.ArtistId, albumId, tags?.Album ?? r.AlbumTitle ?? place?.AlbumTitle ?? FileTags.Parse(r.TagsJson).Album,
                    listing?.Disc, listing?.Number, (r.Duration ?? r.TrackDuration)?.TotalSeconds, r.Path, r.Format,
                    settings.PreferFileCovers ? LocalMusic.CoverUrl(r.Id) : albumId is { } album ? CoverUrl(album, r.EditionId ?? place?.EditionId) : null);
            })
            .Concat(own.Albums.SelectMany(a => a.Songs.Select(song => new LibraryTrack(song.File.Id, song.File.Id, song.Title, song.Artist,
                a.ArtistLocal ? null : a.ArtistId, a.Id, a.Title, song.Tags.Disc, song.Tags.Track?.ToString(), song.File.Duration?.TotalSeconds,
                song.File.Path, song.File.Format, a.CoverUrl, Local: true))))
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.ArtistCredit, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.AlbumTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<ArtistDetail?> GetArtistAsync(Guid id, CancellationToken ct)
    {
        var artist = await db.Artists.FindAsync([id], ct);
        if (artist is null)
            return null;
        var s = await LoadAsync([id], ct, scoped: true);
        var albums = Ordered(s.Albums.Select(a => Evaluate(s, a))).Select(e => Summarise(s, e)).ToList();
        if (artist.InLibrary)
            albums.AddRange((await local.LoadAsync(ct)).Albums.Where(a => a.ArtistId == id).Select(a => a.Summary()));
        var ids = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && m.EntityId == id)
            .Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync(ct);
        return new ArtistDetail(artist.Id, artist.Name, artist.SortName, artist.Disambiguation, artist.Country, ids,
            artist.InLibrary, artist.AlbumsRefreshedAt, albums, ArtistImageUrl(artist.Id)) { OwnShowing = s.Settings.ArtistShowing.ContainsKey(id) };
    }

    public async Task<AlbumDetail?> GetAlbumAsync(Guid id, Guid? editionId, CancellationToken ct)
    {
        var album = await db.Albums.Include(a => a.Artist).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album is null)
            return null;
        var s = await LoadAsync([album.ArtistId], ct, onlyAlbums: [id]);
        var evaluation = Evaluate(s, s.Albums.First(a => a.Id == id));
        var editions = s.EditionsByAlbum[id].OrderBy(e => e.ReleaseDate ?? "9999").ThenBy(e => e.Country).ToList();
        var editionIds = editions.Select(e => e.Id).ToHashSet();
        var shown = editions.FirstOrDefault(e => e.Id == editionId) ?? evaluation.ShownEdition ?? editions.FirstOrDefault();

        var editionProviderIds = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Edition && editionIds.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));
        var editionSummaries = editions.Select(e => new EditionSummary(e.Id, e.Title, e.ReleaseDate, e.Country, e.Status, e.Disambiguation,
            e.Formats, e.TrackCount, s.FilesByEdition[e.Id].Count(), editionProviderIds[e.Id].ToList())).ToList();

        var tracks = new List<TrackRow>();
        var tagged = s.Settings.PreferFileTags && shown is not null
            ? (await db.LibraryFiles.Where(f => f.EditionId != null && editionIds.Contains(f.EditionId.Value)).Select(f => new { f.Id, f.TagsJson }).ToListAsync(ct))
                .ToDictionary(f => f.Id, f => FileTags.Parse(f.TagsJson).Title)
            : null;
        if (shown is not null)
        {
            var listings = s.ListingsByEdition[shown.Id].OrderBy(l => l.Disc).ThenBy(l => l.Position).ToList();
            var trackIds = listings.Select(l => l.TrackId).ToList();
            var trackProviderIds = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && trackIds.Contains(m.EntityId))
                .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));
            foreach (var listing in listings)
            {
                var status = TrackStatusOf(s, evaluation.Album, listing, editionIds);
                var file = s.FilesByTrack[listing.TrackId].FirstOrDefault(f => f.EditionId is { } e && editionIds.Contains(e));
                var title = file is not null && tagged?.GetValueOrDefault(file.Id) is { Length: > 0 } own ? own : listing.Title;
                tracks.Add(new TrackRow(listing.TrackId, listing.Disc, listing.Position, listing.Number, title, listing.ArtistCredit,
                    listing.Duration?.TotalSeconds, status, file?.Path, trackProviderIds[listing.TrackId].ToList(), s.Ignored.Contains(listing.TrackId)));
            }
        }

        return new AlbumDetail(Summarise(s, evaluation), album.Artist.Name, evaluation.OwnedEdition?.Id, shown?.Id, editionSummaries, tracks);
    }

    public async Task<TrackDetail?> GetTrackAsync(Guid id, CancellationToken ct)
    {
        var track = await db.Tracks.Include(t => t.Artist).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (track is null)
            return null;
        var ids = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.EntityId == id)
            .Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync(ct);
        var files = await db.LibraryFiles.Include(f => f.Edition).Where(f => f.TrackId == id).OrderBy(f => f.Path).ToListAsync(ct);
        var filed = files.Select(f => f.EditionId).OfType<Guid>().ToHashSet();
        var on = await db.EditionTracks.Include(et => et.Edition).ThenInclude(e => e.Album).Where(et => et.TrackId == id).ToListAsync(ct);
        var statuses = (await GetAlbumSummariesAsync(on.Select(et => et.Edition.AlbumId).Distinct().ToList(), ct)).ToDictionary(a => a.Id, a => a.Status);
        var appearances = on
            .OrderBy(et => et.Edition.Album.FirstReleaseDate ?? "9999").ThenBy(et => et.Edition.Album.Title).ThenBy(et => et.Edition.ReleaseDate ?? "9999")
            .Select(et => new TrackAppearance(et.Edition.AlbumId, et.Edition.Album.Title, et.Edition.Album.Type, et.EditionId, et.Edition.Title,
                et.Edition.ReleaseDate, et.Edition.Country, et.Disc, et.Number, et.Title, filed.Contains(et.EditionId),
                statuses.GetValueOrDefault(et.Edition.AlbumId, AlbumStatus.Missing)))
            .ToList();
        // A cover chosen for the song itself, else its edition's.
        var cover = await db.Artwork.AnyAsync(a => a.EntityKind == EntityKind.Track && a.EntityId == id && a.Chosen, ct)
            ? $"api/v1/artwork/track/{id}"
            : files.FirstOrDefault(f => f.Edition is not null) is { Edition: { } edition }
            ? CoverUrl(edition.AlbumId, edition.Id)
            : appearances.FirstOrDefault() is { } first ? CoverUrl(first.AlbumId, null) : null;
        return new TrackDetail(track.Id, track.Title, track.ArtistCredit, track.ArtistId, track.Artist.Name, track.Duration?.TotalSeconds, track.Ignored,
            ids, cover, appearances,
            files.Select(f => new TrackFile(f.Id, f.Path, f.Format, f.Duration?.TotalSeconds, f.Size, f.Edition?.AlbumId, f.EditionId,
                TagsOf(FileTags.Parse(f.TagsJson)), f.FormattedAt, f.Lyrics)).ToList());
    }

    // The tags worth showing, in the order a tag editor would.
    private static List<FileTag> TagsOf(FileTags tags) => new (string Name, string? Value)[]
        {
            ("Title", tags.Title), ("Artist", tags.Artist), ("Album", tags.Album), ("Album artist", tags.AlbumArtist),
            ("Track", tags.Track?.ToString()), ("Disc", tags.Disc?.ToString()), ("Date", tags.Date), ("ISRC", tags.Isrc),
            ("MusicBrainz recording", tags.MusicBrainzRecordingId), ("MusicBrainz release", tags.MusicBrainzReleaseId),
            ("MusicBrainz release group", tags.MusicBrainzReleaseGroupId), ("MusicBrainz artist", tags.MusicBrainzArtistId),
            ("MusicBrainz album artist", tags.MusicBrainzAlbumArtistId),
        }
        .Where(t => !string.IsNullOrEmpty(t.Value)).Select(t => new FileTag(t.Name, t.Value!)).ToList();

    public async Task<IReadOnlyList<AlbumSummary>?> GetArtistAlbumsAsync(Guid id, CancellationToken ct) =>
        (await GetArtistAsync(id, ct))?.Albums;

    /// <summary>Summaries for any albums, for example search results, in the order given.</summary>
    public async Task<IReadOnlyList<AlbumSummary>> GetAlbumSummariesAsync(IReadOnlyList<Guid> albumIds, CancellationToken ct)
    {
        var s = await LoadAsync([], ct, onlyAlbums: albumIds);
        var byId = s.Albums.ToDictionary(a => a.Id);
        return albumIds.Where(byId.ContainsKey).Select(id => Summarise(s, Evaluate(s, byId[id]))).ToList();
    }

    public async Task<IReadOnlyList<MissingAlbum>> GetMissingAsync(CancellationToken ct)
    {
        var artists = await db.Artists.Where(a => a.InLibrary).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var s = await LoadAsync(artists.Keys, ct);
        return Ordered(s.Albums.Select(a => Evaluate(s, a)).Where(e => IsListedMissing(s, e)))
            .Select(e => new MissingAlbum(artists[e.Album.ArtistId], Summarise(s, e)))
            .OrderBy(m => m.ArtistName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every excluded file, whether or not its tags are enough to show it.</summary>
    public Task<IReadOnlyList<ReviewItem>> GetExcludedAsync(CancellationToken ct) => ItemsAsync(db.LibraryFiles.Where(f => f.ExcludedAt != null), null, ct);

    public Task<IReadOnlyList<ReviewItem>> GetReviewAsync(ReviewList list, CancellationToken ct) => list switch
    {
        ReviewList.Excluded => ItemsAsync(db.LibraryFiles.Where(f => f.ExcludedAt != null), SetAsides.Usable, ct),
        ReviewList.Incomplete => ItemsAsync(db.LibraryFiles.Where(f => f.ExcludedAt != null), t => !SetAsides.Usable(t), ct),
        // A deferred file a scan is trying again is pending, and one it matched is in the library.
        ReviewList.Deferred => ItemsAsync(db.LibraryFiles.Where(f => f.DeferredAt != null && f.TrackId == null && f.MatchedAt != null), null, ct),
        ReviewList.Hidden => ItemsAsync(db.LibraryFiles.Where(f => f.HiddenAt != null), null, ct),
        _ => ItemsAsync(db.LibraryFiles.Where(f => f.TrackId == null && f.MatchedAt != null && f.ExcludedAt == null && f.DeferredAt == null && f.HiddenAt == null), null, ct),
    };

    private async Task<IReadOnlyList<ReviewItem>> ItemsAsync(IQueryable<LibraryFile> query, Func<FileTags, bool>? tagged, CancellationToken ct)
    {
        var files = await query.OrderBy(f => f.Path).ToListAsync(ct);
        if (tagged is not null)
            files = files.Where(f => tagged(FileTags.Parse(f.TagsJson))).ToList();
        var suggestedIds = files.Select(f => f.SuggestedTrackId).OfType<Guid>().ToList();
        var tracks = await db.Tracks.Where(t => suggestedIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var editionIds = files.Select(f => f.SuggestedEditionId).OfType<Guid>().ToList();
        var editions = await db.Editions.Include(e => e.Album).Where(e => editionIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

        return files.Select(f =>
        {
            var tags = FileTags.Parse(f.TagsJson);
            SuggestedTrack? suggestion = null;
            if (f.SuggestedTrackId is { } trackId && tracks.TryGetValue(trackId, out var track))
            {
                var edition = f.SuggestedEditionId is { } e ? editions.GetValueOrDefault(e) : null;
                suggestion = new SuggestedTrack(track.Id, track.Title, track.ArtistCredit, track.Duration?.TotalSeconds, edition?.Id, edition?.AlbumId, edition?.Album.Title,
                    edition is null ? null : CoverUrl(edition.AlbumId, edition.Id), track.ArtistId);
            }
            return new ReviewItem(f.Id, f.Path, f.ReviewReason, f.MatchConfidence, tags.Title, tags.Artist ?? tags.AlbumArtist, tags.Album,
                f.Duration?.TotalSeconds, suggestion, f.ExcludedAt is not null, SetAsides.Of(f), LocalMusic.CoverUrl(f.Id), f.ModifiedAt);
        }).ToList();
    }
}
