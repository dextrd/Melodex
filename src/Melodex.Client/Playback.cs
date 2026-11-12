namespace Melodex.Client;

/// <summary>When the full-screen player's visualiser has its bars made: they're worked out from each song by the server.</summary>
public enum VisualiserBars
{
    /// <summary>For the song playing when the visualiser shows, and the next in the queue ahead of it; kept for a while, then forgotten.</summary>
    OnDemand,

    /// <summary>For every song in the library after each scan, kept in a .bars file beside it, and on demand for any without one yet.</summary>
    WhenMatching,

    /// <summary>Never: there's no visualiser.</summary>
    Off,
}

/// <summary>Which songs run into each other with no gap between them.</summary>
public enum GaplessPlayback
{
    /// <summary>None: each song starts once the one before has ended and the next has loaded.</summary>
    Off,

    /// <summary>One song after another from the same album, as the record plays them.</summary>
    SameAlbum,

    /// <summary>Every song after another in the queue.</summary>
    Always,
}

/// <summary>How music is played.</summary>
public sealed record PlaybackSettings
{
    public VisualiserBars Visualiser { get; init; } = VisualiserBars.OnDemand;

    public GaplessPlayback Gapless { get; init; } = GaplessPlayback.SameAlbum;
}

/// <summary>Playback's settings, with what bars beside every song would take.</summary>
/// <param name="Songs">Files in the library.</param>
/// <param name="EstimatedBytes">About how much room .bars files beside all of them would take.</param>
/// <param name="BarsFiles">The .bars files beside them now.</param>
/// <param name="BarsBytes">How much room those take.</param>
public sealed record PlaybackStatus(PlaybackSettings Settings, int Songs, long EstimatedBytes, int BarsFiles, long BarsBytes);

/// <summary>Playback's settings to save.</summary>
/// <param name="DeleteBarsFiles">Delete the .bars files beside the library's songs too.</param>
public sealed record SavePlayback(VisualiserBars Visualiser, bool DeleteBarsFiles = false, GaplessPlayback Gapless = GaplessPlayback.SameAlbum);
