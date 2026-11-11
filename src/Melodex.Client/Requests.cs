namespace Melodex.Client;

// Requests: fetching an artist, album or track into the library. An artist request is split into album requests,
// and an album request into track requests unless a retrieval plugin can fetch the whole album, so every request
// can be followed down to the jobs that fetched its files.

/// <summary>What a request asks for.</summary>
public enum RequestKind
{
    Artist,
    Album,
    Track,
}

/// <summary>Where a request is. Artist and album requests are <see cref="InProgress"/> while their parts run.</summary>
public enum RequestState
{
    Queued,
    InProgress,
    Searching,
    Fetching,
    Verifying,
    Importing,
    Notifying,
    InLibrary,
    NeedsReview,
    Failed,
    Cancelled,

    /// <summary>A listen-now request whose file is ready to play.</summary>
    Playable,
}

/// <summary>Body of <c>POST /requests</c>.</summary>
/// <param name="Kind">Artist, album or track.</param>
/// <param name="Id">The catalogue ID of the artist, album or track.</param>
/// <param name="EditionId">For an album or track, the edition to fetch; otherwise the one you own, or the planner's pick.</param>
public sealed record CreateRequest(RequestKind Kind, Guid Id, Guid? EditionId = null);

/// <summary>Body of <c>POST /requests/{id}/move</c> and <c>POST /tasks/{id}/move</c>.</summary>
/// <param name="Before">Put it just before this one in the queue; null for the end.</param>
public sealed record MoveInQueue(Guid? Before);

/// <summary>Body of <c>POST /requests/{id}/choose</c>: a candidate from one of the request's jobs.</summary>
public sealed record ChooseCandidate(Guid JobId, string CandidateId);

/// <summary>Body of keeping a copy an attempt fetched but didn't keep, for its quality.</summary>
public sealed record KeepCopy(Guid JobId);

/// <summary>A request and where it stands.</summary>
/// <param name="Title">What it's for, for example <c>Get Lucky · Daft Punk</c>.</param>
/// <param name="Detail">What's happening now, or why it stopped.</param>
/// <param name="Parts">For artist and album requests: how many parts there are and where they stand.</param>
/// <param name="Children">The parts themselves, on <c>GET /requests/{id}</c> only.</param>
/// <param name="Jobs">Attempts through retrieval plugins, oldest first, on <c>GET /requests/{id}</c> only.</param>
/// <param name="ListenNow">A track fetched to listen to, not for the library; see <c>/listen</c>.</param>
/// <param name="Paused">Held back: it won't start until resumed. One paused mid-fetch starts that fetch again.</param>
/// <param name="QueueOrder">Its place in the queue: lower starts first.</param>
public sealed record RequestInfo(
    Guid Id,
    RequestKind Kind,
    Guid TargetId,
    Guid? EditionId,
    string Title,
    RequestState State,
    string? Detail,
    Guid? ParentId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    RequestParts? Parts = null,
    IReadOnlyList<RequestInfo>? Children = null,
    IReadOnlyList<JobInfo>? Jobs = null,
    bool ListenNow = false,
    bool Paused = false,
    double QueueOrder = 0,
    bool Upgrade = false)
{
    /// <summary>Whether it can still change: not in the library, failed, needing review or cancelled.</summary>
    public bool Active => State is not (RequestState.InLibrary or RequestState.NeedsReview or RequestState.Failed or RequestState.Cancelled or RequestState.Playable);
}

/// <summary>How the parts of an artist or album request stand.</summary>
public sealed record RequestParts(int Total, int InLibrary, int Active, int NeedsReview, int Failed, int Cancelled);

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Whether the fetched audio passed the checks against the catalogue.</summary>
public enum Verification
{
    NotRun,
    Passed,
    DurationMismatch,
    FingerprintMismatch,

    /// <summary>The files are a quality the profile turns off.</summary>
    QualityRefused,

    /// <summary>Lossless whose highs stop as a lossy file's do: it seems to be lossy music passed off as lossless.</summary>
    SuspectLossless,

    /// <summary>An upgrade's copy that's no better than the file it would replace.</summary>
    NotBetter,
}

/// <summary>One attempt to fetch a request through one retrieval plugin.</summary>
/// <param name="Attempt">1 for the first plugin in the chain, 2 for the first fallback, and so on.</param>
/// <param name="Plugin">The plugin instance's name.</param>
/// <param name="Candidate">What was fetched.</param>
/// <param name="Candidates">Everything the plugin offered, best first, so another can be picked.</param>
/// <param name="Log">What happened, one line per step.</param>
public sealed record JobInfo(
    Guid Id,
    int Attempt,
    string? Plugin,
    JobState State,
    JobCandidate? Candidate,
    IReadOnlyList<JobCandidate> Candidates,
    Verification Verification,
    string? VerificationDetail,
    string Log,
    DateTime CreatedAt,
    DateTime? FinishedAt);

/// <summary>A retrieval candidate, as the Activity page shows it.</summary>
/// <param name="Vouched">Lossless with reason to think it really is: from a store, or a rip with its log.</param>
public sealed record JobCandidate(string CandidateId, string Title, string? Artist, string? Album, double? DurationSeconds,
    string Source, string? Url, string? Format, int? BitrateKbps, double? Score, bool? Vouched = null);

/// <summary>How requests are fetched, checked and filed.</summary>
public sealed record FetchSettings
{
    /// <summary>The beets default layout.</summary>
    public const string DefaultPathTemplate = "$albumartist/$album%aunique{}/$track $title";

    /// <summary>
    /// Where beets files a new album, in beets' path format. A track for an album you already have goes next to
    /// that album's files instead, wherever they are.
    /// </summary>
    public string PathTemplate { get; init; } = DefaultPathTemplate;

    /// <summary>How many tracks or albums are fetched at once, from 1 to 8.</summary>
    public int ParallelFetches { get; init; } = 2;

    /// <summary>
    /// A fetched track passes when its length is within this many seconds of the catalogue's, or
    /// <see cref="DurationTolerancePercent"/> of it, whichever is more.
    /// </summary>
    public int DurationToleranceSeconds { get; init; } = 5;

    public int DurationTolerancePercent { get; init; } = 3;

    /// <summary>How many of a plugin's candidates to try before moving on to the next plugin, from 1 to 5.</summary>
    public int CandidatesPerPlugin { get; init; } = 2;

    /// <summary>How long a listen-now track that isn't added is kept after it was last played, from 5 minutes to a week.</summary>
    public int ListenNowExpiryMinutes { get; init; } = 60;
}
