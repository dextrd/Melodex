using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Listening;

/// <summary>
/// Radio (#4): play that never runs out, started from a song, album, artist or playlist. Each batch asks the discovery
/// plugins for songs like a few of what it was started from, and like what it played lately that you starred or didn't
/// skip, so it drifts as it goes; when they know too little, it falls back on the library's songs by the same and similar
/// artists. Songs you don't have make up the server's share of it, to fetch to listen to. Nothing it's queued comes again, nor
/// anything you played in the last two days while there's enough else, nor a song you skipped within half a minute in
/// the last 90 days; skips also count against the artist, and the same artist doesn't come within three songs
/// while there's another, nor twice running unless there's nothing else.
/// </summary>
public sealed partial class RadioService(MelodexDbContext db, DiscoveryService discovery, SettingsService settings)
{
    /// <summary>How long a quick skip steers radios.</summary>
    public static readonly TimeSpan SkipsKept = TimeSpan.FromDays(90);

    /// <summary>Songs played this recently come only when there's nothing else.</summary>
    public static readonly TimeSpan PlayedLately = TimeSpan.FromDays(2);

    public const int MaxTake = 50;

    // How many songs like each seed to ask for, how many seeds a batch uses (fewer for the first, so it starts quickly),
    // and how many songs apart the same artist comes.
    private const int Asked = 40;
    private const int FirstSeeds = 2, Seeds = 4;
    private const int ArtistGap = 3;
    private const string Library = "Your library";

    private sealed record Seed(string Name, RadioSeedKind Kind, IReadOnlyList<Guid> Tracks, IReadOnlyList<Guid> Artists);

    private sealed class Candidate(DiscoveredSong song)
    {
        public DiscoveredSong Song { get; set; } = song;
        public double Score { get; set; }
    }

    /// <summary>The next songs for a radio; null when what it was started from isn't there, or isn't theirs.</summary>
    public async Task<RadioBatch?> NextAsync(Guid userId, RadioNext body, CancellationToken ct)
    {
        if (await SeedAsync(userId, body.SeedId, ct) is not { } seed)
            return null;
        var take = Math.Clamp(body.Take, 1, MaxTake);
        var recent = body.Recent ?? [];
        var queued = recent.ToHashSet();
        var missingShare = body.OwnedOnly ? 0 : Math.Clamp((await settings.GetFetchAsync(ct)).RadioMissingPercent, 0, 100) / 100.0;

        var since = DateTime.UtcNow - SkipsKept;
        var skips = await db.RadioSkips.AsNoTracking().Where(s => s.UserId == userId && s.SkippedAt >= since).ToListAsync(ct);
        var skipped = skips.Select(s => s.TrackId).ToHashSet();
        var skippedArtists = skips.Where(s => s.ArtistId is not null).GroupBy(s => s.ArtistId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var starred = (await db.Marks.AsNoTracking().Where(m => m.UserId == userId && m.Kind == MarkKind.Song && m.StarredAt != null)
            .Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();
        var lately = DateTime.UtcNow - PlayedLately;
        var playedLately = (await db.Plays.AsNoTracking().Where(p => p.UserId == userId && p.PlayedAt >= lately).Select(p => p.TrackId).ToListAsync(ct))
            .ToHashSet();

        // A few of what it was started from, then what it played that you starred, and the last song it played that
        // wasn't skipped, a little less: so it drifts with what you're enjoying.
        var seeds = new Dictionary<Guid, double>();
        foreach (var track in seed.Tracks.OrderBy(_ => Random.Shared.Next()).Take(recent.Count == 0 ? FirstSeeds : Seeds))
            seeds[track] = 1;
        foreach (var track in recent.Reverse().Where(starred.Contains).Take(2))
            seeds.TryAdd(track, 1);
        foreach (var track in recent.Reverse().Where(t => !skipped.Contains(t) && !starred.Contains(t)).Take(1))
            seeds.TryAdd(track, 0.6);

        var candidates = new Dictionary<Guid, Candidate>();
        void Add(DiscoveredSong song, double score)
        {
            if (!candidates.TryGetValue(song.TrackId, out var known))
                candidates[song.TrackId] = known = new Candidate(song);
            known.Score += score;
        }
        foreach (var (track, weight) in seeds)
            foreach (var song in await discovery.SimilarSongsAsync(track, Asked, ct) ?? [])
                Add(song, weight * song.Score);

        // An album's, artist's or playlist's own songs you have belong in it, a little less than what's like them.
        if (seed.Kind != RadioSeedKind.Song)
            foreach (var song in await OwnedAmongAsync(seed.Tracks, 30, ct))
                Add(song, 0.5);
        // Too little found, as for an artist the plugins hardly know: songs you have by the same artists and those like them.
        // Counting only those it could still play.
        int Playable() => candidates.Values.Count(c => c.Song.Owned && !queued.Contains(c.Song.TrackId) && !playedLately.Contains(c.Song.TrackId));
        if (Playable() < take * 3)
        {
            foreach (var song in await OwnedByAsync(seed.Artists, 30, ct))
                Add(song, 0.3);
            foreach (var artist in seed.Artists.Take(2))
                foreach (var like in (await discovery.SimilarArtistsAsync(artist, 10, ct) ?? []).Where(a => a.Id is not null && a.InLibrary))
                    foreach (var song in await OwnedByAsync([like.Id!.Value], 5, ct))
                        Add(song, 0.4 * like.Score);
        }
        // And so it never runs out, nor runs short of songs you have for your share of them, anything you have.
        if (Playable() < take * 2)
            foreach (var song in await OwnedByAsync([], take * 4, ct))
                Add(song, 0.05);

        foreach (var c in candidates.Values)
        {
            if (c.Song.ArtistId is { } artist && skippedArtists.TryGetValue(artist, out var times))
                c.Score *= Math.Pow(0.6, times);
            if (starred.Contains(c.Song.TrackId))
                c.Score *= 1.3;
        }
        var ignored = (await db.Tracks.AsNoTracking().Where(t => candidates.Keys.Contains(t.Id) && t.Ignored).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        // A song it was started from plays first, and not again; an album's, artist's or playlist's own songs are part of it.
        if (seed.Kind == RadioSeedKind.Song)
            queued.Add(seed.Tracks[0]);
        var allowed = candidates.Values
            .Where(c => !queued.Contains(c.Song.TrackId) && !skipped.Contains(c.Song.TrackId)
                && !ignored.Contains(c.Song.TrackId) && (c.Song.Owned || missingShare > 0))
            .ToList();
        // Played lately only when there's not enough else.
        var fresh = allowed.Where(c => !playedLately.Contains(c.Song.TrackId)).ToList();
        if (fresh.Count >= take)
            allowed = fresh;

        // The same song twice, live or remixed, counts as once; and the same artist, credited however, as one.
        var heard = await TracksOfAsync(recent.TakeLast(100).Append(seed.Tracks[0]).ToList(), ct);
        var sung = heard.Select(t => SongKey(t.Title, t.ArtistCredit)).ToHashSet();
        allowed = allowed.Where(c => !sung.Contains(SongKey(c.Song.Title, c.Song.ArtistCredit)))
            .GroupBy(c => SongKey(c.Song.Title, c.Song.ArtistCredit)).Select(g => g.MaxBy(c => c.Score)!).ToList();
        var recentArtists = recent.TakeLast(ArtistGap).Select(id => heard.FirstOrDefault(t => t.Id == id)?.ArtistCredit).OfType<string>()
            .Select(TextMatch.NormaliseArtist).ToList();
        var picked = new List<DiscoveredSong>();
        // Started from a song, it plays first.
        if (recent.Count == 0 && seed.Kind == RadioSeedKind.Song && await SongAsync(seed.Tracks[0], ct) is { } first)
        {
            picked.Add(first);
            recentArtists.Add(TextMatch.NormaliseArtist(first.ArtistCredit));
        }
        // The share is a target over the whole radio: a batch that came short of songs you don't have, or long, is made up for in this one.
        var played = recent.TakeLast(100).Where(id => id != seed.Tracks[0] || seed.Kind != RadioSeedKind.Song).ToList();
        var playedMissing = played.Count - heard.Count(t => played.Contains(t.Id) && t.Owned);
        var missingWanted = Math.Clamp(Share(played.Count + take - picked.Count, missingShare) - playedMissing, 0, take - picked.Count);
        var slots = Enumerable.Range(0, take - picked.Count).Select(i => i < missingWanted).OrderBy(_ => Random.Shared.Next()).ToList();
        foreach (var missing in slots)
        {
            // One of the kind this place wants, by an artist not heard in the last few songs; else not the last one's; else
            // of either kind, spaced; else any, so your share holds and an artist doesn't come twice running when it can help it.
            var kind = allowed.Where(c => c.Song.Owned != missing).ToList();
            if ((Pick(kind, recentArtists, ArtistGap) ?? Pick(kind, recentArtists, 1) ?? Pick(allowed, recentArtists, ArtistGap)
                    ?? Pick(allowed, recentArtists, 1) ?? Pick(allowed, recentArtists, 0)) is not { } chosen)
                break;
            allowed.Remove(chosen);
            picked.Add(chosen.Song);
            recentArtists.Add(TextMatch.NormaliseArtist(chosen.Song.ArtistCredit));
        }
        return new RadioBatch(seed.Name, seed.Kind, await discovery.WithAlbumsAsync(picked, ct));
    }

    /// <summary>One of the best few, more likely the better it is, by an artist not among the last so many.</summary>
    private static Candidate? Pick(List<Candidate> pool, List<string> recentArtists, int gap)
    {
        var last = recentArtists.TakeLast(gap).ToHashSet();
        var best = pool.Where(c => !last.Contains(TextMatch.NormaliseArtist(c.Song.ArtistCredit)))
            .OrderByDescending(c => c.Score).Take(12).ToList();
        if (best.Count == 0)
            return null;
        var total = best.Sum(c => c.Score);
        var at = Random.Shared.NextDouble() * total;
        foreach (var c in best)
            if ((at -= c.Score) <= 0)
                return c;
        return best[^1];
    }

    /// <summary>How many of so many songs are ones you don't have: the share, rounded up or down at random so it comes out right over time.</summary>
    public static int Share(int count, double share)
    {
        var exact = count * share;
        var whole = (int)Math.Floor(exact);
        return whole + (Random.Shared.NextDouble() < exact - whole ? 1 : 0);
    }

    /// <summary>A quick skip of a song a radio played: radios leave it out, and play its artist less, for <see cref="SkipsKept"/>.</summary>
    public async Task SkipAsync(Guid userId, Guid trackId, CancellationToken ct)
    {
        var artist = await db.Tracks.Where(t => t.Id == trackId).Select(t => (Guid?)t.ArtistId).FirstOrDefaultAsync(ct);
        db.RadioSkips.Add(new RadioSkip { UserId = userId, TrackId = trackId, ArtistId = artist });
        var old = DateTime.UtcNow - SkipsKept;
        await db.RadioSkips.Where(s => s.UserId == userId && s.SkippedAt < old).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<RadioSettings> SettingsAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => new RadioSettings(u.Autoplay)).FirstAsync(ct);

    public async Task SaveSettingsAsync(Guid userId, RadioSettings value, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Autoplay, value.Autoplay), ct);

    /// <summary>What a radio was started from: a song, album, artist, or one of their own playlists.</summary>
    private async Task<Seed?> SeedAsync(Guid userId, Guid id, CancellationToken ct)
    {
        if (await db.Tracks.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.Title, t.ArtistId }).FirstOrDefaultAsync(ct) is { } track)
            return new Seed(track.Title, RadioSeedKind.Song, [id], [track.ArtistId]);
        if (await db.Albums.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Title, a.ArtistId }).FirstOrDefaultAsync(ct) is { } album)
        {
            var tracks = await db.EditionTracks.Where(et => et.Edition.AlbumId == id).Select(et => et.TrackId).Distinct().ToListAsync(ct);
            return new Seed(album.Title, RadioSeedKind.Album, await OwnedFirstAsync(tracks, ct), [album.ArtistId]);
        }
        if (await db.Artists.AsNoTracking().Where(a => a.Id == id).Select(a => a.Name).FirstOrDefaultAsync(ct) is { } artist)
        {
            var tracks = await db.Tracks.Where(t => t.ArtistId == id && !t.Ignored).Select(t => t.Id).ToListAsync(ct);
            return new Seed(artist, RadioSeedKind.Artist, await OwnedFirstAsync(tracks, ct), [id]);
        }
        if (await db.Playlists.AsNoTracking().Where(p => p.Id == id && p.UserId == userId && p.DeletedAt == null).Select(p => p.Name)
                .FirstOrDefaultAsync(ct) is { } playlist)
        {
            var entries = await db.PlaylistEntries.Where(e => e.PlaylistId == id).Select(e => new { e.TrackId, e.FileId }).ToListAsync(ct);
            var files = entries.Where(e => e.TrackId is null && e.FileId is not null).Select(e => e.FileId!.Value).ToList();
            var tracks = entries.Select(e => e.TrackId).OfType<Guid>()
                .Concat(await db.LibraryFiles.Where(f => files.Contains(f.Id) && f.TrackId != null).Select(f => f.TrackId!.Value).ToListAsync(ct))
                .Distinct().ToList();
            var artists = await db.Tracks.Where(t => tracks.Contains(t.Id)).GroupBy(t => t.ArtistId).OrderByDescending(g => g.Count())
                .Select(g => g.Key).Take(3).ToListAsync(ct);
            return tracks.Count == 0 ? null : new Seed(playlist, RadioSeedKind.Playlist, tracks, artists);
        }
        return null;
    }

    /// <summary>The songs, those you have first when there are any: a radio's seeds should be ones you know.</summary>
    private async Task<IReadOnlyList<Guid>> OwnedFirstAsync(List<Guid> tracks, CancellationToken ct)
    {
        var owned = await db.LibraryFiles.Where(f => f.TrackId != null && tracks.Contains(f.TrackId.Value)).Select(f => f.TrackId!.Value).Distinct()
            .ToListAsync(ct);
        return owned.Count > 0 ? owned : tracks;
    }

    /// <summary>Songs you have, at random: by these artists, or by anyone when there are none.</summary>
    private async Task<IReadOnlyList<DiscoveredSong>> OwnedByAsync(IReadOnlyList<Guid> artists, int take, CancellationToken ct)
    {
        var owned = db.LibraryFiles.Where(f => f.TrackId != null).Select(f => f.TrackId!.Value).Distinct();
        var tracks = db.Tracks.AsNoTracking().Where(t => owned.Contains(t.Id) && !t.Ignored);
        if (artists.Count > 0)
            tracks = tracks.Where(t => artists.Contains(t.ArtistId));
        var found = await tracks.OrderBy(_ => EF.Functions.Random()).Take(take)
            .Select(t => new { t.Id, t.Title, t.ArtistCredit, t.ArtistId, t.Duration }).ToListAsync(ct);
        return found.Select(t => new DiscoveredSong(t.Id, t.Title, t.ArtistCredit, t.ArtistId, null, null, null, t.Duration?.TotalSeconds, true, 0, [Library]))
            .ToList();
    }

    /// <summary>Those of these songs you have, at random.</summary>
    private async Task<IReadOnlyList<DiscoveredSong>> OwnedAmongAsync(IReadOnlyList<Guid> tracks, int take, CancellationToken ct)
    {
        var owned = db.LibraryFiles.Where(f => f.TrackId != null && tracks.Contains(f.TrackId.Value)).Select(f => f.TrackId!.Value).Distinct();
        var found = await db.Tracks.AsNoTracking().Where(t => owned.Contains(t.Id) && !t.Ignored).OrderBy(_ => EF.Functions.Random()).Take(take)
            .Select(t => new { t.Id, t.Title, t.ArtistCredit, t.ArtistId, t.Duration }).ToListAsync(ct);
        return found.Select(t => new DiscoveredSong(t.Id, t.Title, t.ArtistCredit, t.ArtistId, null, null, null, t.Duration?.TotalSeconds, true, 0, [Library]))
            .ToList();
    }

    private async Task<DiscoveredSong?> SongAsync(Guid trackId, CancellationToken ct) =>
        await db.Tracks.AsNoTracking().Where(t => t.Id == trackId).Select(t => new { t.Title, t.ArtistCredit, t.ArtistId, t.Duration }).FirstOrDefaultAsync(ct)
            is { } t
            ? new DiscoveredSong(trackId, t.Title, t.ArtistCredit, t.ArtistId, null, null, null, t.Duration?.TotalSeconds, false, 1, [])
            : null;

    private sealed record Heard(Guid Id, string Title, string ArtistCredit, bool Owned);

    private async Task<List<Heard>> TracksOfAsync(List<Guid> tracks, CancellationToken ct) =>
        await db.Tracks.AsNoTracking().Where(t => tracks.Contains(t.Id))
            .Select(t => new Heard(t.Id, t.Title, t.ArtistCredit, db.LibraryFiles.Any(f => f.TrackId == t.Id))).ToListAsync(ct);

    /// <summary>A song whatever version it is: its title without anything in brackets after it, and its artist.</summary>
    private static string SongKey(string title, string artist) =>
        $"{TextMatch.Normalise(Bracketed().Replace(title, " "))}|{TextMatch.NormaliseArtist(artist)}";

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*([\(\[][^\)\]]*[\)\]]|\s-\s.*$)")]
    private static partial System.Text.RegularExpressions.Regex Bracketed();
}
