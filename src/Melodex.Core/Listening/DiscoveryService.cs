using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Discovery;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;

namespace Melodex.Core.Listening;

/// <summary>
/// Music related to what someone plays (#3): similar artists and songs, and recommendations, from the discovery plugins
/// in the user's order. Each plugin's scores are scaled to its best, weighted by its place in the order, and added up for
/// what several find, so what they agree on comes first. What they find is matched to the catalogue by its IDs, else by
/// name when that's clear; an artist with a MusicBrainz ID the catalogue hasn't seen yet is added to it, as a search would.
/// </summary>
public sealed class DiscoveryService(MelodexDbContext db, DiscoveryPlugins plugins, CatalogueService catalogue, ILogger<DiscoveryService> logger)
{
    /// <summary>How long a plugin's similar artists and songs are kept; they change slowly.</summary>
    public static readonly TimeSpan SimilarFor = TimeSpan.FromDays(1);

    /// <summary>How long a plugin's recommendations for someone are kept; services remake them about daily.</summary>
    public static readonly TimeSpan RecommendedFor = TimeSpan.FromHours(6);

    /// <summary>The most a list can be.</summary>
    public const int MaxTake = 100;

    /// <summary>How far back what someone has played counts toward what they're recommended.</summary>
    public static readonly TimeSpan Lately = TimeSpan.FromDays(90);

    private const int Seeds = 10;

    public async Task<DiscoveryStatus> StatusAsync(CancellationToken ct)
    {
        var manifests = (await plugins.GetAsync(ct)).Select(p => p.Manifest).ToList();
        bool Has(string capability) => manifests.Any(m => m.Capabilities.Contains(capability));
        return new DiscoveryStatus(Has(DiscoveryCapabilities.SimilarArtists), Has(DiscoveryCapabilities.SimilarRecordings),
            Has(DiscoveryCapabilities.Recommendations));
    }

    /// <summary>Artists like this one, closest first; null when there's no such artist.</summary>
    public async Task<IReadOnlyList<SimilarArtist>?> SimilarArtistsAsync(Guid artistId, int take, CancellationToken ct)
    {
        if (await db.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artistId, ct) is not { } artist)
            return null;
        take = Math.Clamp(take, 1, MaxTake);
        var query = new DiscoveryArtist(artist.Name, await catalogue.ProviderIdsAsync(EntityKind.Artist, artistId, ct));
        // A few more than asked for, as the artist themselves, or one they're matched to, is left out.
        var answers = await AskAsync(DiscoveryCapabilities.SimilarArtists, $"artist|{artistId}", SimilarFor,
            (plugin, ct2) => plugin.SimilarArtistsAsync(query, take + 5, ct2), ct);
        var combined = Combine(answers, a => a.Score, a => Keys(a.Ids, ArtistKey(a.Name)));

        var found = new List<SimilarArtist>();
        foreach (var (best, score, from) in combined)
        {
            if (found.Count == take)
                break;
            var id = await MatchArtistAsync(best.Name, best.Ids, best.Disambiguation, ct);
            if (id == artistId || ArtistKey(best.Name) == ArtistKey(artist.Name) && id is null || id is not null && found.Any(f => f.Id == id))
                continue;
            found.Add(new SimilarArtist(id, best.Name, best.Disambiguation, false, id is { } known ? LibraryView.ArtistImageUrl(known) : null, score, from));
        }
        await db.SaveChangesAsync(ct);
        return await WithLibraryAsync(found, ct);
    }

    /// <summary>Songs like this one, closest first; null when there's no such song.</summary>
    public async Task<IReadOnlyList<DiscoveredSong>?> SimilarSongsAsync(Guid trackId, int take, CancellationToken ct)
    {
        if (await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct) is not { } track)
            return null;
        take = Math.Clamp(take, 1, MaxTake);
        var query = new DiscoveryRecording(track.Title, track.ArtistCredit, await catalogue.ProviderIdsAsync(EntityKind.Track, trackId, ct),
            [await ArtistOfAsync(track.ArtistId, ct)], track.Duration);
        var answers = await AskAsync(DiscoveryCapabilities.SimilarRecordings, $"track|{trackId}", SimilarFor,
            (plugin, ct2) => plugin.SimilarRecordingsAsync(query, Ask(take), ct2), ct);
        return await SongsAsync(answers, take, [trackId], ct);
    }

    /// <summary>Songs recommended for someone, best first, from what the plugins know of them and what they've played lately.</summary>
    public async Task<IReadOnlyList<DiscoveredSong>> RecommendAsync(Guid userId, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var accounts = (await db.ScrobbleAccounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct))
            .ToDictionary(a => a.Service == ScrobbleService.ListenBrainz ? DiscoveryAccounts.ListenBrainz : DiscoveryAccounts.LastFm, a => a.Username);
        var since = DateTime.UtcNow - Lately;
        var plays = await db.Plays.AsNoTracking().Where(p => p.UserId == userId && p.PlayedAt >= since).ToListAsync(ct);
        var topArtists = new List<DiscoveryArtist>();
        foreach (var group in plays.GroupBy(p => p.ArtistId is { } id ? id.ToString() : ArtistKey(p.ArtistCredit)).OrderByDescending(g => g.Count()).Take(Seeds))
        {
            var play = group.First();
            topArtists.Add(play.ArtistId is { } id ? await ArtistOfAsync(id, ct) : new DiscoveryArtist(play.ArtistCredit, []));
        }
        var topSongs = new List<DiscoveryRecording>();
        foreach (var group in plays.GroupBy(p => p.TrackId).OrderByDescending(g => g.Count()).Take(Seeds))
        {
            var play = group.First();
            var ids = (await catalogue.ProviderIdsAsync(EntityKind.Track, play.TrackId, ct)).ToList();
            if (play.RecordingMbid is { } mbid && !ids.Any(i => i.Provider == Providers.MusicBrainz))
                ids.Add(new ProviderId(Providers.MusicBrainz, mbid));
            topSongs.Add(new DiscoveryRecording(play.Title, play.ArtistCredit, ids,
                [play.ArtistId is { } artist ? await ArtistOfAsync(artist, ct) : new DiscoveryArtist(play.ArtistCredit, [])],
                play.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null));
        }
        var listener = new DiscoveryListener(accounts, topArtists, topSongs);
        var answers = await AskAsync(DiscoveryCapabilities.Recommendations, $"user|{userId}|{string.Join(',', accounts.OrderBy(a => a.Key))}", RecommendedFor,
            (plugin, ct2) => plugin.RecommendAsync(listener, Ask(take), ct2), ct);
        return await SongsAsync(answers, take, [], ct);
    }

    /// <summary>How many songs to ask each plugin for: more than are listed, as only those the catalogue has are.</summary>
    private static int Ask(int take) => Math.Min(take * 3, MaxTake);

    /// <summary>
    /// Asks every plugin that declares the capability, at once, keeping each one's answer a while. A plugin that fails is
    /// left out, and asked again next time.
    /// </summary>
    private async Task<IReadOnlyList<(string Source, IReadOnlyList<T> Items)>> AskAsync<T>(string capability, string subject, TimeSpan keep,
        Func<IDiscoveryPlugin, CancellationToken, Task<IReadOnlyList<T>>> ask, CancellationToken ct)
    {
        var asked = (await plugins.GetAsync(ct)).Where(p => p.Manifest.Capabilities.Contains(capability)).ToList();
        // Someone is waiting on this: it goes ahead of background work under the plugins' rate limits.
        PluginHttp.Interactive.Value = true;
        var answers = await Task.WhenAll(asked.Select(async p =>
        {
            try
            {
                // New settings, such as another API key, are asked afresh.
                var items = await DiscoveryCache.GetOrAddAsync($"{p.Instance.Id}|{p.Instance.SettingsJson.GetHashCode()}|{capability}|{subject}", keep,
                    () => ask(p.Plugin, ct));
                return (Source: p.Instance.Name, Items: items);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("{Plugin} couldn't find music like {Subject}: {Error}", p.Instance.Name, subject, e.Message);
                return (Source: p.Instance.Name, Items: (IReadOnlyList<T>)[]);
            }
        }));
        return answers;
    }

    /// <summary>
    /// The plugins' answers as one list, best first. Each one's scores are scaled to its best, and weighed by its place in
    /// the order: the first counts fully, each after it a quarter less, down to a quarter. What several find is the same
    /// when it shares an ID or a name, and its scores are added.
    /// </summary>
    public static IReadOnlyList<(T Best, double Score, IReadOnlyList<string> From)> Combine<T>(IReadOnlyList<(string Source, IReadOnlyList<T> Items)> answers,
        Func<T, double> score, Func<T, IReadOnlyList<string>> keys)
    {
        var entries = new List<(T Best, double Score, double BestScore, List<string> From)>();
        var byKey = new Dictionary<string, int>();
        var place = 0;
        foreach (var (source, items) in answers)
        {
            if (items.Count == 0)
                continue;
            var weight = Math.Max(0.25, 1 - 0.25 * place++);
            var top = items.Max(score);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                // Without scores to go by, their order says it.
                var scaled = top > 0 ? Math.Max(0, score(item)) / top : 1 - (double)i / items.Count;
                var itemKeys = keys(item);
                var at = itemKeys.Select(k => byKey.TryGetValue(k, out var found) ? found : -1).FirstOrDefault(f => f >= 0, -1);
                if (at < 0)
                {
                    entries.Add((item, scaled * weight, scaled * weight, [source]));
                    at = entries.Count - 1;
                }
                else
                {
                    var entry = entries[at];
                    var better = scaled * weight > entry.BestScore;
                    entries[at] = (better ? item : entry.Best, entry.Score + scaled * weight, Math.Max(entry.BestScore, scaled * weight),
                        entry.From.Contains(source) ? entry.From : [.. entry.From, source]);
                }
                foreach (var key in itemKeys)
                    byKey.TryAdd(key, at);
            }
        }
        var most = Math.Max(1, answers.Count(a => a.Items.Count > 0));
        // Scaled back to at most 1: what every plugin put first.
        var ceiling = Enumerable.Range(0, most).Sum(p => Math.Max(0.25, 1 - 0.25 * p));
        return entries.OrderByDescending(e => e.Score)
            .Select(e => (e.Best, Math.Round(e.Score / ceiling, 4), (IReadOnlyList<string>)e.From))
            .ToList();
    }

    private async Task<IReadOnlyList<DiscoveredSong>> SongsAsync(IReadOnlyList<(string Source, IReadOnlyList<DiscoveredRecording> Items)> answers, int take,
        IReadOnlyCollection<Guid> leaveOut, CancellationToken ct)
    {
        var combined = Combine(answers, r => r.Score, r => Keys(r.Ids, SongKey(r.Title, r.Artists.FirstOrDefault()?.Name ?? r.Artist)));
        var found = new List<DiscoveredSong>();
        foreach (var (best, score, from) in combined)
        {
            if (found.Count == take)
                break;
            var artist = best.Artists.FirstOrDefault() ?? new DiscoveryArtist(best.Artist, []);
            var artistId = await MatchArtistAsync(artist.Name, artist.Ids, null, ct);
            // Only songs the catalogue has are listed: they're the ones that can be played, fetched or opened.
            if (await MatchTrackAsync(best, artistId, ct) is not { } trackId || leaveOut.Contains(trackId) || found.Any(f => f.TrackId == trackId))
                continue;
            found.Add(new DiscoveredSong(trackId, best.Title, best.Artist, artistId, null, best.Album, null, best.Duration?.TotalSeconds, false, score, from));
        }
        await db.SaveChangesAsync(ct);
        return await WithAlbumsAsync(found, ct);
    }

    /// <summary>
    /// The artist in the catalogue: by an ID it already has; else, for a MusicBrainz ID when a catalogue uses them, added
    /// to it as a search would; else the only artist the catalogue holds with that name. Null when none of those.
    /// </summary>
    private async Task<Guid?> MatchArtistAsync(string name, IReadOnlyList<ProviderId> ids, string? disambiguation, CancellationToken ct)
    {
        foreach (var id in ids)
            if (await catalogue.FindAsync(EntityKind.Artist, id, ct) is { } known)
                return known;
        if (ids.FirstOrDefault(i => i.Provider == Providers.MusicBrainz) is { } mbid && await catalogue.UsesAsync(Providers.MusicBrainz, ct))
            return (await catalogue.ImportArtistAsync(new IndexArtist(mbid, name, Disambiguation: disambiguation), ct)).Id;
        var named = await catalogue.ArtistsNamedAsync(name, ct);
        return named.Count == 1 ? named[0].Id : named.Where(a => a.InLibrary).ToList() is [var only] ? only.Id : null;
    }

    /// <summary>The song in the catalogue: by an ID it already has, else the artist's song with that title, the one you have first.</summary>
    private async Task<Guid?> MatchTrackAsync(DiscoveredRecording recording, Guid? artistId, CancellationToken ct)
    {
        foreach (var id in recording.Ids)
            if (await catalogue.FindAsync(EntityKind.Track, id, ct) is { } known)
                return known;
        if (artistId is not { } artist || TextMatch.NormaliseTitle(recording.Title) is not { Length: > 0 } title)
            return null;
        var tracks = await db.Tracks.AsNoTracking().Where(t => t.ArtistId == artist).Select(t => new { t.Id, t.Title }).ToListAsync(ct);
        var same = tracks.Where(t => TextMatch.NormaliseTitle(t.Title) == title).Select(t => t.Id).ToList();
        if (same.Count <= 1)
            return same.FirstOrDefault() is var one && one != Guid.Empty ? one : null;
        return await db.LibraryFiles.Where(f => f.TrackId != null && same.Contains(f.TrackId.Value)).Select(f => f.TrackId).FirstOrDefaultAsync(ct) ?? same[0];
    }

    private async Task<IReadOnlyList<SimilarArtist>> WithLibraryAsync(List<SimilarArtist> found, CancellationToken ct)
    {
        var ids = found.Select(f => f.Id).OfType<Guid>().ToList();
        var inLibrary = (await db.Artists.Where(a => ids.Contains(a.Id) && a.InLibrary).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        return found.Select(f => f.Id is { } id && inLibrary.Contains(id) ? f with { InLibrary = true } : f).ToList();
    }

    /// <summary>
    /// Whether each song is in the library, and an album it's on for its cover: one you have; else the one the plugin
    /// named; else a studio album rather than a single, live album or compilation, the earliest first.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredSong>> WithAlbumsAsync(IReadOnlyList<DiscoveredSong> found, CancellationToken ct)
    {
        var ids = found.Select(f => f.TrackId).ToList();
        var owned = (await db.LibraryFiles.Where(f => f.TrackId != null && ids.Contains(f.TrackId.Value))
            .Select(f => new { TrackId = f.TrackId!.Value, f.EditionId }).ToListAsync(ct)).ToLookup(f => f.TrackId, f => f.EditionId);
        var appearances = (await db.EditionTracks.Where(et => ids.Contains(et.TrackId))
                .Select(et => new { et.TrackId, et.EditionId, et.Edition.AlbumId, et.Edition.Album.Title, et.Edition.Album.Type, et.Edition.Album.SecondaryTypes,
                    et.Edition.Album.FirstReleaseDate })
                .ToListAsync(ct))
            .ToLookup(et => et.TrackId);
        return found.Select(f =>
        {
            var id = f.TrackId;
            var mine = owned[id].ToList();
            var named = TextMatch.NormaliseTitle(f.AlbumTitle);
            var on = appearances[id].OrderByDescending(a => mine.Contains(a.EditionId))
                .ThenByDescending(a => named.Length > 0 && TextMatch.NormaliseTitle(a.Title) == named)
                .ThenByDescending(a => a.Type == AlbumType.Album && a.SecondaryTypes.Count == 0)
                .ThenBy(a => a.FirstReleaseDate ?? "9999")
                .FirstOrDefault();
            return f with
            {
                Owned = mine.Count > 0,
                AlbumId = on?.AlbumId,
                AlbumTitle = on?.Title ?? f.AlbumTitle,
                CoverUrl = on is null ? null : LibraryView.CoverUrl(on.AlbumId, mine.Contains(on.EditionId) ? on.EditionId : null),
            };
        }).ToList();
    }

    private async Task<DiscoveryArtist> ArtistOfAsync(Guid artistId, CancellationToken ct)
    {
        var name = await db.Artists.Where(a => a.Id == artistId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? "";
        return new DiscoveryArtist(name, await catalogue.ProviderIdsAsync(EntityKind.Artist, artistId, ct));
    }

    private static IReadOnlyList<string> Keys(IReadOnlyList<ProviderId> ids, string name) =>
        [.. ids.Where(i => i.Provider is Providers.MusicBrainz or Providers.Isrc).Select(i => $"{i.Provider}:{i.Value.ToLowerInvariant()}"), $"name:{name}"];

    private static string ArtistKey(string name) => TextMatch.NormaliseArtist(name);

    private static string SongKey(string title, string artist) => $"{TextMatch.NormaliseTitle(title)}|{TextMatch.NormaliseArtist(artist)}";
}

/// <summary>Each plugin's answers, kept a while so the same page shows at once next time and the services aren't asked again.</summary>
public static class DiscoveryCache
{
    private const int MaxEntries = 500;

    private static readonly ConcurrentDictionary<string, (DateTime Until, Task<object> Answer)> Entries = new();

    public static async Task<IReadOnlyList<T>> GetOrAddAsync<T>(string key, TimeSpan keep, Func<Task<IReadOnlyList<T>>> ask)
    {
        var now = DateTime.UtcNow;
        if (Entries.TryGetValue(key, out var entry) && now < entry.Until && !entry.Answer.IsFaulted && !entry.Answer.IsCanceled)
            return (IReadOnlyList<T>)await entry.Answer;
        var task = Box(ask());
        Entries[key] = (now + keep, task);
        if (Entries.Count > MaxEntries)
            foreach (var old in Entries.OrderBy(e => e.Value.Until).Take(Entries.Count - MaxEntries).ToList())
                Entries.TryRemove(old.Key, out _);
        try
        {
            return (IReadOnlyList<T>)await task;
        }
        catch
        {
            Entries.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>Forgets every answer, for tests.</summary>
    public static void Clear() => Entries.Clear();

    private static async Task<object> Box<T>(Task<IReadOnlyList<T>> task) => await task;
}
