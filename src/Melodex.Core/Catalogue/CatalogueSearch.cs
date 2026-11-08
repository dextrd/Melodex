using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;

namespace Melodex.Core.Catalogue;

/// <summary>
/// Searching for artists, albums and tracks: through the catalogues, whose answers are kept a while so a search
/// made again comes back at once, or only in the catalogue Melodex already holds, which answers in a moment. The second
/// takes in music of your own, listed as its tags say, too.
/// </summary>
public sealed class CatalogueSearch(MelodexDbContext db, CatalogueService service, LibraryView view, LocalMusic local)
{
    private const int Limit = 10;

    private static readonly SemaphoreSlim Importing = new(1, 1);

    public async Task<SearchResults> SearchAsync(string text, IReadOnlySet<SearchKind>? kinds, bool local, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
            return new SearchResults([], [], []);
        // Without a catalogue, there's only the library to search.
        return local || !await service.HasCatalogueAsync(ct) ? await LocalAsync(text, kinds, ct) : await RemoteAsync(text, kinds, ct);
    }

    private static bool Wants(IReadOnlySet<SearchKind>? kinds, SearchKind kind) => kinds is null || kinds.Count == 0 || kinds.Contains(kind);

    private async Task<SearchResults> RemoteAsync(string text, IReadOnlySet<SearchKind>? kinds, CancellationToken ct)
    {
        var results = await SearchCache.GetOrAddAsync(text, kinds, async () =>
        {
            // Someone is waiting on this: it goes ahead of background catalogue updates under the rate limit.
            PluginHttp.Interactive.Value = true;
            var indexes = await service.IndexesAsync(ct);
            var query = new IndexSearchQuery(text, kinds, Limit);
            var answers = await Task.WhenAll(indexes.Select(async (index, i) =>
            {
                try
                {
                    return await index.Plugin.SearchAsync(query, ct);
                }
                // A gap-filling catalogue that isn't answering leaves the core catalogue's results to stand on their own.
                catch (Exception e) when (i > 0 && LibraryMatcher.IsUnavailable(e, ct))
                {
                    return IndexSearchResults.Empty;
                }
            }));
            return Merge(answers);
        });

        var artists = new List<Artist>();
        var albumIds = new List<Guid>();
        var tracks = new List<(Track Track, Album? Album)>();
        // The web app asks for artists, albums and tracks separately, at once. Their answers often share an artist or
        // album, so they're filed one at a time, or two would add the same one.
        await Importing.WaitAsync(ct);
        try
        {
            foreach (var hit in results.Artists)
                artists.Add(await service.ImportArtistAsync(hit, ct));
            foreach (var hit in results.Albums)
                albumIds.Add((await service.ImportAlbumAsync(hit, null, ct)).Id);
            foreach (var hit in results.Tracks)
                tracks.Add((await service.ImportTrackAsync(hit, ct), hit.Album is { } from ? await service.ImportAlbumUnlessKnownAsync(from, ct) : null));
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Importing.Release();
        }
        return await ResultsAsync(artists, albumIds, tracks, ct);
    }

    /// <summary>
    /// The core catalogue's answers, then up to half as many again from the catalogues after it, leaving out what the
    /// core catalogue already found: the same name, or the same title by the same artist.
    /// </summary>
    public static IndexSearchResults Merge(IReadOnlyList<IndexSearchResults> answers)
    {
        if (answers.Count == 1)
            return answers[0];
        static string Artist(string name) => TextMatch.NormaliseArtist(name);
        static string Titled(string title, string credit) => $"{TextMatch.NormaliseTitle(title)}|{TextMatch.NormaliseArtist(credit)}";
        List<T> Combine<T>(Func<IndexSearchResults, IReadOnlyList<T>> kind, Func<T, string> key)
        {
            var list = kind(answers[0]).ToList();
            var seen = list.Select(key).ToHashSet();
            foreach (var answer in answers.Skip(1))
                list.AddRange(kind(answer).Where(x => seen.Add(key(x))).Take(Limit / 2));
            return list;
        }
        return new IndexSearchResults(
            Combine(r => r.Artists, a => Artist(a.Name)),
            Combine(r => r.Albums, a => Titled(a.Title, a.ArtistCredit)),
            Combine(r => r.Tracks, t => Titled(t.Title, t.ArtistCredit)));
    }

    /// <summary>Matches in the catalogue Melodex already holds: artists, albums and tracks it has seen before.</summary>
    private async Task<SearchResults> LocalAsync(string text, IReadOnlySet<SearchKind>? kinds, CancellationToken ct)
    {
        var pattern = $"%{text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
        var artists = Wants(kinds, SearchKind.Artist)
            ? await db.Artists.Where(a => EF.Functions.Like(a.Name, pattern, @"\"))
                .OrderByDescending(a => a.InLibrary).ThenBy(a => a.Name.Length).ThenBy(a => a.Name).Take(Limit).ToListAsync(ct)
            : [];
        var albumIds = Wants(kinds, SearchKind.Album)
            ? await db.Albums.Where(a => EF.Functions.Like(a.Title, pattern, @"\"))
                .OrderByDescending(a => a.Artist.InLibrary).ThenBy(a => a.Title.Length).ThenBy(a => a.Title).Select(a => a.Id).Take(Limit).ToListAsync(ct)
            : [];
        var tracks = new List<(Track Track, Album? Album)>();
        if (Wants(kinds, SearchKind.Track))
        {
            var found = await db.Tracks.Where(t => EF.Functions.Like(t.Title, pattern, @"\"))
                .OrderByDescending(t => db.LibraryFiles.Any(f => f.TrackId == t.Id)).ThenBy(t => t.Title.Length).ThenBy(t => t.Title)
                .Take(Limit)
                .ToListAsync(ct);
            // The albums are looked up for these few on their own: asked inside the query above, SQLite works out every
            // track's first album before picking ten, which on a full catalogue takes many seconds.
            var foundIds = found.Select(t => t.Id).ToList();
            var albums = (await db.EditionTracks.Where(et => foundIds.Contains(et.TrackId))
                    .Select(et => new { et.TrackId, et.Edition.Album }).ToListAsync(ct))
                .DistinctBy(et => et.TrackId).ToDictionary(et => et.TrackId, et => et.Album);
            tracks.AddRange(found.Select(t => (t, albums.GetValueOrDefault(t.Id))));
        }
        var results = await ResultsAsync(artists, albumIds, tracks, ct);

        // Your own music, after the catalogue's.
        if (!Wants(kinds, SearchKind.Artist) && !Wants(kinds, SearchKind.Album))
            return results;
        var own = await local.LoadAsync(ct);
        bool Has(string name) => name.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        var ownArtists = Wants(kinds, SearchKind.Artist)
            ? own.Artists.Where(a => Has(a.Name)).OrderBy(a => a.Name.Length).ThenBy(a => a.Name)
                .Select(a => new SearchArtist(a.Id, a.Name, null, true, [], a.Albums[0].CoverUrl, Local: true)).Take(Limit).ToList()
            : [];
        var ownAlbums = Wants(kinds, SearchKind.Album)
            ? own.Albums.Where(a => a.Title != LocalMusic.NoAlbum && Has(a.Title)).OrderBy(a => a.Title.Length).ThenBy(a => a.Title)
                .Select(a => a.Summary()).Take(Limit).ToList()
            : [];
        return results with { Artists = [.. results.Artists, .. ownArtists], Albums = [.. results.Albums, .. ownAlbums] };
    }

    private async Task<SearchResults> ResultsAsync(List<Artist> artists, List<Guid> albumIds, List<(Track Track, Album? Album)> tracks, CancellationToken ct)
    {
        var artistIdList = artists.Select(a => a.Id).ToList();
        var artistProviderIds = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && artistIdList.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new Contracts.Catalogue.ProviderId(m.Provider, m.Value));
        var trackIds = tracks.Select(t => t.Track.Id).ToList();
        var trackProviderIds = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && trackIds.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new Contracts.Catalogue.ProviderId(m.Provider, m.Value));
        var owned = (await db.LibraryFiles.Where(f => f.TrackId != null && trackIds.Contains(f.TrackId.Value)).Select(f => f.TrackId!.Value).ToListAsync(ct)).ToHashSet();
        var summaries = (await view.GetAlbumSummariesAsync([.. albumIds.Union(tracks.Select(t => t.Album?.Id).OfType<Guid>())], ct)).ToDictionary(a => a.Id);

        return new SearchResults(
            artists.Select(a => new SearchArtist(a.Id, a.Name, a.Disambiguation, a.InLibrary, artistProviderIds[a.Id].ToList(), LibraryView.ArtistImageUrl(a.Id))).ToList(),
            [.. albumIds.Where(summaries.ContainsKey).Select(id => summaries[id])],
            tracks.Select(t => new SearchTrack(t.Track.Id, t.Track.Title, t.Track.ArtistCredit, t.Track.Duration?.TotalSeconds, owned.Contains(t.Track.Id),
                trackProviderIds[t.Track.Id].ToList(), t.Track.ArtistId, t.Album?.Id, t.Album?.Title,
                t.Album is { } album ? summaries.GetValueOrDefault(album.Id)?.CoverUrl ?? LibraryView.CoverUrl(album.Id, null) : null)).ToList());
    }
}

/// <summary>The index plugin's recent answers, so a search made again, or a page reloaded, needn't wait for it.</summary>
public static class SearchCache
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(15);
    private const int MaxEntries = 200;

    private static readonly ConcurrentDictionary<string, (DateTime At, Task<IndexSearchResults> Results)> Entries = new();

    public static async Task<IndexSearchResults> GetOrAddAsync(string text, IReadOnlySet<SearchKind>? kinds, Func<Task<IndexSearchResults>> search)
    {
        var key = $"{string.Join(",", (kinds ?? new HashSet<SearchKind>()).Order())}|{text.ToLowerInvariant()}";
        var now = DateTime.UtcNow;
        if (Entries.TryGetValue(key, out var entry) && now - entry.At < KeepFor && !entry.Results.IsFaulted && !entry.Results.IsCanceled)
            return await entry.Results;
        var task = search();
        Entries[key] = (now, task);
        if (Entries.Count > MaxEntries)
            foreach (var old in Entries.OrderBy(e => e.Value.At).Take(Entries.Count - MaxEntries).ToList())
                Entries.TryRemove(old.Key, out _);
        try
        {
            return await task;
        }
        catch
        {
            Entries.TryRemove(key, out _);
            throw;
        }
    }
}
