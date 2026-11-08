using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using ScanFileState = Melodex.Client.ScanFileState;
using Melodex.Core.Plugins;

namespace Melodex.Core.Library;

/// <summary>
/// Matches scanned files to catalogue tracks and editions, in the plan's order: MusicBrainz IDs in the tags, then
/// other crosswalk IDs, then ISRC, then a fuzzy comparison of title, artist and duration. Files that fall under the
/// confidence threshold go to the review list with the best candidate as a suggestion.
/// </summary>
public sealed class LibraryMatcher(
    MelodexDbContext db,
    CatalogueService catalogue,
    SettingsService settings,
    IOptions<PluginHttpOptions> httpOptions,
    ScanFeed feed,
    ILogger<LibraryMatcher> logger)
{
    private readonly Dictionary<string, Artist?> _artistsByName = new(StringComparer.OrdinalIgnoreCase);
    private double _threshold;

    /// <param name="Waiting">Files left pending because the index was unavailable; the next scan matches them.</param>
    /// <param name="StillDeferred">Deferred files tried again that still didn't beat the threshold, so stay deferred.</param>
    public sealed record Result(int Matched, int Review, int Waiting, int StillDeferred = 0);

    public async Task<Result> MatchPendingAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var library = await settings.GetLibraryAsync(ct);
        _threshold = library.MatchThreshold;
        var pending = await db.LibraryFiles.AsNoTracking().Where(f => f.MatchedAt == null && f.ExcludedAt == null && f.HiddenAt == null).Select(f => new { f.Id, f.Path, f.TagsJson }).ToListAsync(ct);
        var groups = pending
            .Select(f => (f.Id, Tags: FileTags.Parse(f.TagsJson), f.Path))
            .GroupBy(x => x.Tags.Album is null
                ? $"loose|{x.Path}"
                : $"{x.Tags.AlbumArtist ?? x.Tags.Artist}|{x.Tags.Album}|{Path.GetDirectoryName(x.Path)}")
            // Groups with MusicBrainz IDs first: they pin their artists by ID, so later groups by the same artist don't
            // need a name search. Then by path, as the scan read them, so the Activity page's list is matched from the top
            // down; the key breaks ties, so the order (and the requests made) never depends on the filesystem.
            .OrderBy(g => g.Any(x => x.Tags.MusicBrainzReleaseGroupId is not null || x.Tags.MusicBrainzReleaseId is not null) ? 0 : 1)
            .ThenBy(g => g.Select(x => x.Path).Min(StringComparer.Ordinal), StringComparer.Ordinal)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        // A group the source was too busy for goes to the back, so it gets time to recover before the next attempt.
        var queue = new Queue<(IGrouping<string, (Guid Id, FileTags Tags, string Path)> Group, int Attempt)>(groups.Select(g => (g, 1)));
        var backoff = new SourceBackoff(httpOptions.Value, logger);
        int done = 0, matched = 0, review = 0, deferred = 0, stillDeferred = 0;
        while (queue.TryDequeue(out var item))
        {
            ct.ThrowIfCancellationRequested();
            var (group, attempt) = item;
            if (backoff.GaveUp)
            {
                deferred += group.Count();
                foreach (var x in group)
                    feed.Set(x.Path, ScanFileState.Waiting, x.Tags.Title, x.Tags.Artist, "The catalogue source is busy; the next scan matches it");
                continue;
            }
            // Each group starts with a clear tracker, so catalogue imports don't pile up and slow every later query.
            db.ChangeTracker.Clear();
            var ids = group.Select(x => x.Id).ToList();
            var tracked = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
            var files = group.Where(x => tracked.ContainsKey(x.Id)).Select(x => new Candidate(tracked[x.Id], x.Tags)).ToList();
            foreach (var file in files)
                feed.Set(file.File.Path, ScanFileState.Matching, file.Tags.Title, file.Tags.Artist);
            try
            {
                await MatchGroupAsync(files, ct);
                // Only a group the source was asked about says it's answering again; one with no title tags never asks.
                if (files.Any(f => f.Tags.Title is not null || f.Tags.Isrc is not null))
                    backoff.Answered();
            }
            catch (Exception e) when (IsUnavailable(e, ct))
            {
                db.ChangeTracker.Clear();
                foreach (var file in files)
                    feed.Set(file.File.Path, ScanFileState.Waiting, file.Tags.Title, file.Tags.Artist,
                        attempt < library.AttemptsWhenBusy ? "The catalogue source is busy; it's tried again later in the scan" : "The catalogue source is busy; the next scan matches it");
                if (attempt < library.AttemptsWhenBusy)
                {
                    queue.Enqueue((group, attempt + 1));
                    logger.LogWarning("[{Done}/{Total}] The catalogue source is busy; {Where} goes to the back of the queue (attempt {Attempt} of {Attempts}): {Reason}",
                        done, pending.Count, Where(files), attempt, library.AttemptsWhenBusy, e.Message);
                }
                else
                {
                    // Out of attempts: leave the files pending so the next scan tries again.
                    done += files.Count;
                    deferred += files.Count;
                    logger.LogWarning("[{Done}/{Total}] Gave up on {Where} for this scan after {Attempts}; the next scan tries again: {Reason}",
                        done, pending.Count, Where(files), Plural(library.AttemptsWhenBusy, "attempt", "attempts"), e.Message);
                }
                await backoff.UnavailableAsync(progress, $"Matched {done} of {pending.Count} files", ct);
                continue;
            }

            foreach (var file in files.Select(c => c.File))
            {
                file.MatchedAt = DateTime.UtcNow;
                // A deferred file leaves the deferred list only by beating the threshold.
                if (file.TrackId is not null)
                {
                    file.DeferredAt = null;
                    matched++;
                }
                else if (file.DeferredAt is not null)
                    stillDeferred++;
                else
                    review++;
            }
            await db.SaveChangesAsync(ct);
            // Artists join the library as their files match, so the library and missing pages fill in during a long
            // scan. Artists who lost their last file leave at the end.
            var credited = files.Select(c => c.File.ArtistId).OfType<Guid>().Distinct().ToList();
            if (credited.Count > 0)
                await db.Artists.Where(a => !a.InLibrary && credited.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.InLibrary, true), ct);
            done += files.Count;
            LogGroup(files, done, pending.Count);
            await ReportAsync(files, ct);
            progress?.Report($"Matched {done} of {pending.Count} files");
        }
        if (backoff.GaveUp && deferred > 0)
            logger.LogWarning("{Files} left to match on the next scan", Plural(deferred, "file", "files"));
        db.ChangeTracker.Clear();

        await UpdateLibraryArtistsAsync(db, ct);
        return new Result(matched, review, deferred, stillDeferred);
    }

    private sealed record Candidate(LibraryFile File, FileTags Tags);

    /// <summary>Tells the scan feed what each file turned out to be, with the song as the library shows it.</summary>
    private async Task ReportAsync(List<Candidate> files, CancellationToken ct)
    {
        var trackIds = files.Select(f => f.File.TrackId).OfType<Guid>().Distinct().ToList();
        var editionIds = files.Select(f => f.File.EditionId).OfType<Guid>().Distinct().ToList();
        var tracks = await db.Tracks.Where(t => trackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ArtistCredit, t.ArtistId }).ToDictionaryAsync(t => t.Id, ct);
        var editions = await db.Editions.Where(e => editionIds.Contains(e.Id))
            .Select(e => new { e.Id, e.AlbumId, e.Album.Title }).ToDictionaryAsync(e => e.Id, ct);
        foreach (var (file, tags) in files)
        {
            if (file.TrackId is { } trackId && tracks.TryGetValue(trackId, out var track))
            {
                var edition = file.EditionId is { } e ? editions.GetValueOrDefault(e) : null;
                feed.Matched(file.Path, track.Title, track.ArtistCredit, track.ArtistId, edition?.Title, edition?.AlbumId,
                    edition is null ? null : LibraryView.CoverUrl(edition.AlbumId, file.EditionId));
            }
            else
                feed.Set(file.Path, ScanFileState.Review, tags.Title, tags.Artist,
                    file.DeferredAt is not null ? "Deferred: still no confident match" : file.ReviewReason ?? "No confident match");
        }
    }

    /// <summary>A provider failure worth retrying later, as opposed to a bug.</summary>
    public static bool IsUnavailable(Exception e, CancellationToken ct) =>
        e is IndexUnavailableException or HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested;

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Where(List<Candidate> files) =>
        files.Count == 1 ? files[0].File.Path : Path.GetDirectoryName(files[0].File.Path)?.Replace('\\', '/') + "/";

    // Each line starts with [files done/total], the same count as the progress in the top bar.
    private void LogGroup(List<Candidate> files, int done, int total)
    {
        var placed = files.Where(f => f.File.TrackId is not null).ToList();
        if (placed.Count > 0)
        {
            var how = string.Join(", ", placed.GroupBy(f => f.File.MatchMethod).Select(g => $"{g.Count()} by {Describe(g.Key)}"));
            logger.LogInformation("[{Done}/{Total}] Matched {Matched} of {Count} files in {Where} ({How})", done, total, placed.Count, files.Count, Where(files), how);
        }
        foreach (var file in files.Where(f => f.File.TrackId is null))
            logger.LogWarning("[{Done}/{Total}] To review: {Path}: {Reason}", done, total, file.File.Path, file.File.ReviewReason ?? "no confident match");
    }

    private static string Describe(MatchMethod method) => method switch
    {
        MatchMethod.MusicBrainzTags => "MusicBrainz tags",
        MatchMethod.IsrcOrUpc => "ISRC",
        MatchMethod.Fuzzy => "title, artist and length",
        MatchMethod.Crosswalk => "a known ID",
        _ => method.ToString(),
    };

    private async Task MatchGroupAsync(List<Candidate> files, CancellationToken ct)
    {
        var first = files[0].Tags;
        var artist = await ResolveArtistAsync(first, ct);
        foreach (var file in files)
            file.File.ArtistId = artist?.Id;

        var unplaced = files;
        if (first.Album is not null)
        {
            var albums = await CandidateAlbumsAsync(files, artist, ct);
            unplaced = await MatchToAlbumsAsync(files, albums, ct);
        }

        foreach (var file in unplaced)
            await MatchLooseAsync(file, artist, ct);

        // What the core catalogue doesn't have, the ones after it may.
        foreach (var index in (await catalogue.IndexesAsync(ct)).Skip(1))
        {
            var left = files.Where(f => f.File.TrackId is null && (f.Tags.Title is not null || f.Tags.Isrc is not null)).ToList();
            if (left.Count == 0)
                break;
            await FillGapsAsync(left, artist, index, ct);
        }
    }

    // ---- Gap-filling catalogues ----

    private readonly Dictionary<(Guid Index, string Name), Artist?> _gapArtists = [];

    /// <summary>
    /// Matches files the core catalogue had nothing confident for against a catalogue after it: the album first, then
    /// each song on its own, by ISRC or by title, artist and length. A suggestion only replaces one that was surer.
    /// </summary>
    private async Task FillGapsAsync(List<Candidate> files, Artist? artist, CatalogueIndex index, CancellationToken ct)
    {
        var tags = files[0].Tags;
        var name = tags.AlbumArtist ?? tags.Artist;
        if (name is not null && await GapArtistAsync(name, artist, index, ct) is { } found)
        {
            artist ??= found;
            foreach (var file in files)
                file.File.ArtistId ??= found.Id;
        }

        var left = files;
        if (tags.Album is not null)
        {
            var title = TextMatch.NormaliseAlbum(tags.Album);
            var results = await index.Plugin.SearchAsync(new IndexSearchQuery($"{tags.Album} {name}".Trim(), new HashSet<SearchKind> { SearchKind.Album }, 10), ct);
            var albums = new List<Album>();
            foreach (var hit in results.Albums
                         .Where(a => TextMatch.Similarity(TextMatch.NormaliseAlbum(a.Title), title) >= 0.9
                                     && (name is null || TextMatch.ArtistSimilarity(name, a.ArtistCredit) >= 0.8))
                         .Take(2))
                if (await catalogue.GetAlbumAsync(hit.Id, ct) is { } album)
                    albums.Add(album);
            left = await MatchToAlbumsAsync(files, albums, ct, index);
        }

        foreach (var file in left)
            await MatchLooseInAsync(file, artist, index, ct);
    }

    /// <summary>
    /// The artist as this catalogue has them, by exact name: the one the core catalogue knows when there is one, paired
    /// with this catalogue's ID so their albums from either catalogue go together, or otherwise new.
    /// </summary>
    private async Task<Artist?> GapArtistAsync(string name, Artist? artist, CatalogueIndex index, CancellationToken ct)
    {
        var key = (index.Instance.Id, TextMatch.NormaliseArtist(name));
        if (_gapArtists.TryGetValue(key, out var cached))
            return cached;

        Artist? result = null;
        if (artist is not null && (await catalogue.ProviderIdsAsync(EntityKind.Artist, artist.Id, ct)).Any(p => p.Provider == index.Provider))
            result = artist;
        else if (await CatalogueService.FindArtistAsync(index.Plugin, name, ct) is { } hit)
        {
            if (await catalogue.FindAsync(EntityKind.Artist, hit.Id, ct) is { } known)
                result = await db.Artists.FindAsync([known], ct);
            else if (artist is not null)
            {
                await catalogue.PairAsync(EntityKind.Artist, artist.Id, hit.Id, ct);
                result = artist;
            }
            else
            {
                result = await catalogue.ImportArtistAsync(hit, ct);
                await db.SaveChangesAsync(ct);
            }
        }
        return _gapArtists[key] = result;
    }

    /// <summary>One song against a gap-filling catalogue, filed with its album when that fits the file's, so it has a page.</summary>
    private async Task MatchLooseInAsync(Candidate file, Artist? artist, CatalogueIndex index, CancellationToken ct)
    {
        var tags = file.Tags;
        (IndexTrack Track, double Score, MatchMethod Method)? best = null;
        if (tags.Isrc is { } isrc && index.Plugin is IIsrcLookup lookup && (await lookup.LookupIsrcAsync(isrc, ct)).FirstOrDefault() is { } byIsrc)
            best = (byIsrc, 1, MatchMethod.IsrcOrUpc);
        else if (tags.Title is not null)
        {
            var artistName = tags.Artist ?? tags.AlbumArtist ?? artist?.Name;
            var results = await index.Plugin.SearchAsync(new IndexSearchQuery($"{tags.Title} {artistName}".Trim(), new HashSet<SearchKind> { SearchKind.Track }, 10), ct);
            foreach (var t in results.Tracks)
            {
                var sameArtist = artist is not null && await catalogue.FindAsync(EntityKind.Artist, t.Artist.Id, ct) == artist.Id;
                var score = Fuzzy(tags, file.File.Duration, t.Title, t.Title, t.ArtistCredit, t.Duration, sameArtist);
                if (best is null || score > best.Value.Score)
                    best = (t, score, MatchMethod.Fuzzy);
            }
        }
        if (best is not { } found)
            return;

        if (found.Score >= _threshold)
        {
            var album = found.Track.Album is { } from && (tags.Album is null || TextMatch.Similarity(TextMatch.NormaliseAlbum(from.Title), TextMatch.NormaliseAlbum(tags.Album)) >= 0.9)
                ? await catalogue.GetAlbumAsync(from.Id, ct)
                : null;
            var track = await catalogue.ImportTrackAsync(found.Track, ct);
            Match(file.File, track.Id, found.Method, found.Score);
            file.File.EditionId = album is null
                ? null
                : await db.EditionTracks.Where(et => et.TrackId == track.Id && et.Edition.AlbumId == album.Id).Select(et => (Guid?)et.EditionId).FirstOrDefaultAsync(ct);
            file.File.ArtistId ??= track.ArtistId;
        }
        else if (file.File.SuggestedTrackId is null || found.Score > (file.File.MatchConfidence ?? 0))
        {
            var track = await catalogue.ImportTrackAsync(found.Track, ct);
            file.File.SuggestedTrackId = track.Id;
            file.File.SuggestedEditionId = null;
            file.File.MatchConfidence = Math.Round(found.Score, 3);
            Review(file.File, $"Closest match, {found.Track.Title} by {found.Track.ArtistCredit} from {index.Instance.Name}, is only {found.Score:P0} confident.");
        }
    }

    // ---- Artist ----

    private async Task<Artist?> ResolveArtistAsync(FileTags tags, CancellationToken ct)
    {
        if ((tags.MusicBrainzAlbumArtistId ?? tags.MusicBrainzArtistId) is { } mbid)
        {
            var artist = await catalogue.GetArtistAsync(new ProviderId(Providers.MusicBrainz, mbid), ct);
            if (artist is not null)
                return artist;
        }

        var name = tags.AlbumArtist ?? tags.Artist;
        if (name is null)
            return null;
        if (!_artistsByName.TryGetValue(name, out var resolved))
            _artistsByName[name] = resolved = await catalogue.ResolveArtistByNameAsync(name, ct);
        return resolved;
    }

    // ---- Album matching ----

    private async Task<List<Album>> CandidateAlbumsAsync(List<Candidate> files, Artist? artist, CancellationToken ct)
    {
        // 1. A release group ID in the tags pins the album.
        var groupId = files.Select(f => f.Tags.MusicBrainzReleaseGroupId).FirstOrDefault(id => id is not null);
        if (groupId is not null && await catalogue.GetAlbumAsync(new ProviderId(Providers.MusicBrainz, groupId), ct) is { } pinned)
            return [pinned];

        var title = TextMatch.NormaliseAlbum(files[0].Tags.Album);
        var albums = new List<Album>();

        // 2. The artist's discography, by title.
        if (artist is not null && !await VariousArtists.IsAsync(db, artist.Id, ct))
        {
            await catalogue.RefreshDiscographyAsync(artist, false, ct);
            var discography = await db.Albums.Where(a => a.ArtistId == artist.Id).ToListAsync(ct);
            albums = discography.Where(a => TextMatch.NormaliseAlbum(a.Title) == title).ToList();
            if (albums.Count == 0)
                albums = discography
                    .Select(a => (Album: a, Score: TextMatch.Similarity(TextMatch.NormaliseAlbum(a.Title), title)))
                    .Where(x => x.Score >= 0.9).OrderByDescending(x => x.Score).Take(3).Select(x => x.Album).ToList();
        }

        // 3. A catalogue search, for compilations and artists that couldn't be resolved.
        if (albums.Count == 0)
        {
            var artistName = files[0].Tags.AlbumArtist ?? files[0].Tags.Artist ?? "";
            var plugin = await catalogue.PluginAsync(ct);
            var results = await plugin.SearchAsync(new IndexSearchQuery($"{files[0].Tags.Album} {artistName}".Trim(), new HashSet<SearchKind> { SearchKind.Album }, 10), ct);
            foreach (var hit in results.Albums
                         .Where(a => TextMatch.Similarity(TextMatch.NormaliseAlbum(a.Title), title) >= 0.9
                                     && (artistName.Length == 0 || TextMatch.Similarity(TextMatch.NormaliseArtist(a.ArtistCredit), TextMatch.NormaliseArtist(artistName)) >= 0.8))
                         .Take(2))
                if (await catalogue.GetAlbumAsync(hit.Id, ct) is { } album)
                    albums.Add(album);
        }

        foreach (var album in albums)
            await catalogue.RefreshEditionsAsync(album, false, ct);
        return albums;
    }

    private sealed record Listing(Guid EditionId, int Disc, int Position, string Title, TimeSpan? Duration, Guid TrackId,
        string TrackTitle, string ArtistCredit, Guid? TrackArtistId, TimeSpan? TrackDuration, HashSet<string> Recordings, HashSet<string> Isrcs);

    private sealed record Assignment(Candidate File, Listing Listing, double Score, MatchMethod Method);

    /// <summary>Picks the edition that best fits the files and assigns files to its tracks. Returns files left over.</summary>
    /// <param name="gapFiller">The catalogue the albums are from when it isn't the core one: then a suggestion only replaces a surer one.</param>
    private async Task<List<Candidate>> MatchToAlbumsAsync(List<Candidate> files, List<Album> albums, CancellationToken ct, CatalogueIndex? gapFiller = null)
    {
        if (albums.Count == 0)
            return files;

        var albumIds = albums.Select(a => a.Id).ToList();
        var editions = await db.Editions.Where(e => albumIds.Contains(e.AlbumId)).ToListAsync(ct);
        if (editions.Count == 0)
            return files;
        var listings = await LoadListingsAsync(editions.Select(e => e.Id).ToList(), ct);
        var editionMbids = await db.Crosswalk
            .Where(m => m.EntityKind == EntityKind.Edition && m.Provider == Providers.MusicBrainz && editions.Select(e => e.Id).Contains(m.EntityId))
            .ToDictionaryAsync(m => m.EntityId, m => m.Value, ct);

        var releaseTag = files.Select(f => f.Tags.MusicBrainzReleaseId).FirstOrDefault(id => id is not null);
        var yearTag = files.Select(f => f.Tags.Date?[..Math.Min(4, f.Tags.Date.Length)]).FirstOrDefault(d => d is not null);

        var artistId = files[0].File.ArtistId;
        (Edition Edition, List<Assignment> Assignments, double Score)? best = null;
        foreach (var edition in editions)
        {
            var album = albums.First(a => a.Id == edition.AlbumId);
            var live = album.SecondaryTypes.Contains(AlbumSecondaryType.Live) || TextMatch.Normalise(album.Title).Split(' ').Contains("live");
            var assignments = Assign(files, listings.GetValueOrDefault(edition.Id, []), artistId, live);
            var score = assignments.Sum(a => a.Score)
                        + (releaseTag is not null && editionMbids.GetValueOrDefault(edition.Id) == releaseTag ? 5 : 0)
                        + (yearTag is not null && edition.ReleaseDate?.StartsWith(yearTag) == true ? 0.1 : 0)
                        + (string.Equals(edition.Status, "Official", StringComparison.OrdinalIgnoreCase) ? 0.05 : 0)
                        - 0.01 * Math.Abs(edition.TrackCount - files.Count);
            if (best is null || score > best.Value.Score)
                best = (edition, assignments, score);
        }

        var leftovers = new List<Candidate>(files);
        foreach (var assignment in best!.Value.Assignments)
        {
            var file = assignment.File.File;
            if (assignment.Score >= _threshold)
            {
                file.TrackId = assignment.Listing.TrackId;
                file.EditionId = assignment.Listing.EditionId;
                file.MatchMethod = assignment.Method;
                file.MatchConfidence = Math.Round(Math.Min(1, assignment.Score), 3);
                file.ReviewReason = null;
                file.SuggestedTrackId = file.SuggestedEditionId = null;
                leftovers.Remove(assignment.File);
            }
            else if (gapFiller is null)
            {
                // Keep the album's best guess as the suggestion; matching on its own may still do better.
                file.SuggestedTrackId = assignment.Listing.TrackId;
                file.SuggestedEditionId = assignment.Listing.EditionId;
                file.MatchConfidence = Math.Round(assignment.Score, 3);
            }
            else if (file.SuggestedTrackId is null || assignment.Score > (file.MatchConfidence ?? 0))
            {
                file.SuggestedTrackId = assignment.Listing.TrackId;
                file.SuggestedEditionId = assignment.Listing.EditionId;
                file.MatchConfidence = Math.Round(assignment.Score, 3);
                Review(file, $"Closest match, {assignment.Listing.TrackTitle} by {assignment.Listing.ArtistCredit} from {gapFiller.Instance.Name}, is only {assignment.Score:P0} confident.");
            }
        }
        return leftovers;
    }

    private async Task<Dictionary<Guid, List<Listing>>> LoadListingsAsync(List<Guid> editionIds, CancellationToken ct)
    {
        var rows = await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
            .Select(et => new { et.EditionId, et.Disc, et.Position, et.Title, et.Duration, et.TrackId, TrackTitle = et.Track.Title, et.Track.ArtistCredit, TrackArtistId = (Guid?)et.Track.ArtistId, TrackDuration = et.Track.Duration })
            .ToListAsync(ct);
        var trackIds = rows.Select(r => r.TrackId).Distinct().ToList();
        var ids = (await db.Crosswalk
                .Where(m => m.EntityKind == EntityKind.Track && trackIds.Contains(m.EntityId) && (m.Provider == Providers.MusicBrainz || m.Provider == Providers.Isrc))
                .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct))
            .ToLookup(m => m.EntityId);

        return rows
            .Select(r => new Listing(r.EditionId, r.Disc, r.Position, r.Title, r.Duration, r.TrackId, r.TrackTitle, r.ArtistCredit, r.TrackArtistId, r.TrackDuration,
                ids[r.TrackId].Where(m => m.Provider == Providers.MusicBrainz).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
                ids[r.TrackId].Where(m => m.Provider == Providers.Isrc).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .GroupBy(l => l.EditionId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Greedy one-to-one assignment of files to listings, best pairs first.</summary>
    private static List<Assignment> Assign(List<Candidate> files, List<Listing> listings, Guid? artistId, bool liveAlbum)
    {
        var pairs = new List<Assignment>();
        foreach (var file in files)
        foreach (var listing in listings)
        {
            var (score, method) = Score(file, listing, artistId, liveAlbum);
            if (score > 0.3)
                pairs.Add(new Assignment(file, listing, score, method));
        }

        var usedFiles = new HashSet<Candidate>();
        var usedListings = new HashSet<Listing>();
        var result = new List<Assignment>();
        foreach (var pair in pairs.OrderByDescending(p => p.Score))
        {
            if (usedFiles.Contains(pair.File) || usedListings.Contains(pair.Listing))
                continue;
            usedFiles.Add(pair.File);
            usedListings.Add(pair.Listing);
            result.Add(pair);
        }
        return result;
    }

    private static (double Score, MatchMethod Method) Score(Candidate file, Listing listing, Guid? artistId, bool liveAlbum)
    {
        if (file.Tags.MusicBrainzRecordingId is { } recording && listing.Recordings.Contains(recording))
            return (PositionBonus(file, listing) + 1, MatchMethod.MusicBrainzTags);
        if (file.Tags.Isrc is { } isrc && listing.Isrcs.Contains(isrc))
            return (PositionBonus(file, listing) + 1, MatchMethod.IsrcOrUpc);
        // Where the catalogue doesn't know a track's length, its place on the album stands in for it.
        var expected = listing.Duration ?? listing.TrackDuration ?? (PositionBonus(file, listing) > 0 ? file.File.Duration : null);
        var fuzzy = Fuzzy(file.Tags, file.File.Duration, listing.Title, listing.TrackTitle, listing.ArtistCredit, expected,
            artistId is not null && listing.TrackArtistId == artistId, liveAlbum);
        return (Math.Min(1, fuzzy + PositionBonus(file, listing)), MatchMethod.Fuzzy);
    }

    // Breaks ties between identical recordings on one edition (for example a track repeated on a bonus disc).
    private static double PositionBonus(Candidate file, Listing listing) =>
        file.Tags.Track == listing.Position && (file.Tags.Disc ?? 1) == listing.Disc ? 0.05 : 0;

    /// <summary>
    /// Fuzzy confidence from 0 to 1: title counts most, then duration, then artist. A title that isn't close, a
    /// different artist, or a length more than 20 seconds out, caps the score below any sensible threshold.
    /// </summary>
    /// <param name="sameArtist">Whether the track is by the artist the file's tags were resolved to, whatever the credit's spelling or script.</param>
    /// <param name="liveAlbum">Whether the listing is on a live album, where a venue in the title doesn't count against it.</param>
    public static double Fuzzy(FileTags tags, TimeSpan? fileDuration, string listingTitle, string trackTitle, string artistCredit, TimeSpan? expected,
        bool sameArtist = false, bool liveAlbum = false)
    {
        var fileTitle = TextMatch.NormaliseTitle(tags.Title, liveAlbum);
        var title = Math.Max(TextMatch.Similarity(fileTitle, TextMatch.NormaliseTitle(listingTitle, liveAlbum)),
                             TextMatch.Similarity(fileTitle, TextMatch.NormaliseTitle(trackTitle, liveAlbum)));
        var fileArtist = tags.Artist ?? tags.AlbumArtist;
        var artist = sameArtist ? 1 : fileArtist is null ? 0.5 : TextMatch.ArtistSimilarity(fileArtist, artistCredit);

        // An unknown length is weak evidence against, not neutral: title and artist alone stay under the default
        // threshold, so they go to review rather than matching.
        double duration = 0.25;
        double? difference = null;
        if (fileDuration is { } actual && expected is { } wanted)
        {
            difference = Math.Abs((actual - wanted).TotalSeconds);
            duration = difference <= 3 ? 1 : difference >= 20 ? 0 : 1 - (difference.Value - 3) / 17;
        }

        var score = 0.55 * title + 0.15 * artist + 0.30 * duration;
        if (difference > 20)
            score = Math.Min(score, 0.6);
        if (title < 0.6)
            score = Math.Min(score, 0.5);
        // The same song by someone else is another recording, usually a cover.
        if (artist < 0.5)
            score = Math.Min(score, 0.5);
        return score;
    }

    // ---- Tracks on their own ----

    private async Task MatchLooseAsync(Candidate file, Artist? artist, CancellationToken ct)
    {
        var tags = file.Tags;
        file.File.EditionId = null;

        // IDs first: a recording or ISRC already in the crosswalk.
        foreach (var id in new[] { tags.MusicBrainzRecordingId is { } r ? new ProviderId(Providers.MusicBrainz, r) : null,
                                   tags.Isrc is { } i ? new ProviderId(Providers.Isrc, i) : null }.OfType<ProviderId>())
        {
            if (await catalogue.FindAsync(EntityKind.Track, id, ct) is { } trackId)
            {
                Match(file.File, trackId, id.Provider == Providers.Isrc ? MatchMethod.IsrcOrUpc : MatchMethod.MusicBrainzTags, 1);
                return;
            }
        }

        var plugin = await catalogue.PluginAsync(ct);
        if (tags.Isrc is { } isrc && plugin is IIsrcLookup isrcLookup && (await isrcLookup.LookupIsrcAsync(isrc, ct)).FirstOrDefault() is { } byIsrc)
        {
            var track = await catalogue.ImportTrackAsync(byIsrc, ct);
            Match(file.File, track.Id, MatchMethod.IsrcOrUpc, 1);
            return;
        }

        if (tags.Title is null)
        {
            Review(file.File, "The file has no title tag.");
            return;
        }

        var artistName = tags.Artist ?? tags.AlbumArtist ?? artist?.Name;
        var results = await plugin.SearchAsync(new IndexSearchQuery($"{tags.Title} {artistName}".Trim(), new HashSet<SearchKind> { SearchKind.Track }, 10), ct);
        var scored = new List<(IndexTrack Track, double Score)>();
        foreach (var t in results.Tracks)
        {
            // By the artist the tags resolved to, even when the credit is spelled or written differently (岩崎琢 for Taku Iwasaki).
            var sameArtist = artist is not null && await catalogue.FindAsync(EntityKind.Artist, t.Artist.Id, ct) == artist.Id;
            scored.Add((t, Fuzzy(tags, file.File.Duration, t.Title, t.Title, t.ArtistCredit, t.Duration, sameArtist)));
        }
        var best = scored.OrderByDescending(x => x.Score).FirstOrDefault();

        if (best.Track is null)
        {
            if (file.File.SuggestedTrackId is null)
                Review(file.File, "Nothing in the catalogue looks like this file.");
            else
                Review(file.File, $"Closest match is only {file.File.MatchConfidence:P0} confident.");
            return;
        }

        var imported = await catalogue.ImportTrackAsync(best.Track, ct);
        if (best.Score >= _threshold)
        {
            Match(file.File, imported.Id, MatchMethod.Fuzzy, best.Score);
        }
        else if (file.File.SuggestedTrackId is null || best.Score > (file.File.MatchConfidence ?? 0))
        {
            file.File.SuggestedTrackId = imported.Id;
            file.File.SuggestedEditionId = null;
            file.File.MatchConfidence = Math.Round(best.Score, 3);
            Review(file.File, $"Closest match, {best.Track.Title} by {best.Track.ArtistCredit}, is only {best.Score:P0} confident.");
        }
        else
        {
            Review(file.File, $"Closest match is only {file.File.MatchConfidence:P0} confident.");
        }
    }

    private static void Match(LibraryFile file, Guid trackId, MatchMethod method, double confidence)
    {
        file.TrackId = trackId;
        file.MatchMethod = method;
        file.MatchConfidence = Math.Round(confidence, 3);
        file.ReviewReason = null;
        file.SuggestedTrackId = file.SuggestedEditionId = null;
    }

    private static void Review(LibraryFile file, string reason)
    {
        file.TrackId = null;
        file.EditionId = null;
        file.MatchMethod = MatchMethod.Unmatched;
        file.ReviewReason = reason;
    }

    /// <summary>Marks the artists credited on a file as in the library, and the rest as not.</summary>
    public static async Task UpdateLibraryArtistsAsync(MelodexDbContext db, CancellationToken ct)
    {
        var credited = await db.LibraryFiles.Where(f => f.ArtistId != null).Select(f => f.ArtistId!.Value).Distinct().ToListAsync(ct);
        await db.Artists.Where(a => a.InLibrary && !credited.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.InLibrary, false), ct);
        await db.Artists.Where(a => !a.InLibrary && credited.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.InLibrary, true), ct);
    }
}
