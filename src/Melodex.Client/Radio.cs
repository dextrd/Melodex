namespace Melodex.Client;

/// <summary>What a radio was started from (#4).</summary>
public enum RadioSeedKind { Song, Album, Artist, Playlist }

/// <summary>
/// Body of <c>POST /radio</c>: the next songs for a radio, started from a song, album, artist or playlist (#4). A radio
/// keeps nothing on the server: each time, say what it was started from and what it's played so far.
/// </summary>
/// <param name="SeedId">The song (track ID), album, artist or playlist it was started from.</param>
/// <param name="Recent">The songs it's queued so far, oldest first, as track IDs: none come again, and the last few steer what's next.</param>
/// <param name="Take">How many songs (at most 50).</param>
/// <param name="OwnedOnly">Only songs you have, whatever your radio settings say.</param>
public sealed record RadioNext(Guid SeedId, IReadOnlyList<Guid>? Recent = null, int Take = 5, bool OwnedOnly = false);

/// <summary>The next songs for a radio.</summary>
/// <param name="Name">What it was started from: the song's, album's, artist's or playlist's name.</param>
/// <param name="Kind">What that is.</param>
/// <param name="Songs">
/// The songs, in the order to play them: those you have, and, as the server's share of them says, those you don't, to fetch to
/// listen to. Started from a song, the first batch starts with it.
/// </param>
public sealed record RadioBatch(string Name, RadioSeedKind Kind, IReadOnlyList<DiscoveredSong> Songs);

/// <summary>Someone's radio settings (#4).</summary>
/// <param name="Autoplay">When the queue runs out, carry on with a radio from the last song.</param>
public sealed record RadioSettings(bool Autoplay = true);

/// <summary>Body of <c>POST /radio/skips</c>: a song a radio played that was skipped within its first half minute.</summary>
public sealed record RadioSkipped(Guid TrackId);
