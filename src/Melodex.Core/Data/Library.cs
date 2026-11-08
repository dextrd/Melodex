using Melodex.Client;
using Melodex.Contracts.Plugins;

namespace Melodex.Core.Data;

// Library and work entities, filled by the core. Owned and missing are never stored as truth: they are
// computed by comparing catalogue entities with library files through the crosswalk.

/// <summary>How a library file was matched to the catalogue, in the order matching tries them.</summary>
public enum MatchMethod
{
    Unmatched,
    MusicBrainzTags,
    Crosswalk,
    IsrcOrUpc,
    Fuzzy,
    Manual,
}

public class LibraryFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Path relative to the library root.</summary>
    public required string Path { get; set; }

    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string? Format { get; set; }
    public TimeSpan? Duration { get; set; }

    /// <summary>Tags as read from the file, as a JSON object of tag name to value.</summary>
    public string TagsJson { get; set; } = "{}";

    /// <summary>The artist the file is credited to, once resolved.</summary>
    public Guid? ArtistId { get; set; }

    public Guid? TrackId { get; set; }
    public Track? Track { get; set; }

    /// <summary>The edition the file belongs to; null for a track matched on its own, without an album.</summary>
    /// <summary>What lyrics the file has, beside it in an .lrc file or in its tags, as last read or written.</summary>
    public LyricsKind Lyrics { get; set; }

    /// <summary>When the lyrics plugins were last asked and had none, so they aren't asked again for a while.</summary>
    public DateTime? LyricsCheckedAt { get; set; }

    /// <summary>When the user excluded its song from lyrics matching: no scan looks for its lyrics.</summary>
    public DateTime? LyricsExcludedAt { get; set; }

    public Guid? EditionId { get; set; }

    public Edition? Edition { get; set; }
    public MatchMethod MatchMethod { get; set; }

    /// <summary>From 0 to 1; anything under the threshold goes to the review list.</summary>
    public double? MatchConfidence { get; set; }

    /// <summary>For files on the review list: the best candidate found, which the user can accept.</summary>
    public Guid? SuggestedTrackId { get; set; }

    public Guid? SuggestedEditionId { get; set; }

    /// <summary>Why the file is on the review list; null when it's matched.</summary>
    public string? ReviewReason { get; set; }

    /// <summary>When matching last ran for this file; null means it's waiting to be matched.</summary>
    public DateTime? MatchedAt { get; set; }

    public string? MediaServerTrackId { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the user excluded it from matching, for a file no catalogue knows. It's never matched and isn't on the review
    /// list, but stays in the library as its own tags say. It stays excluded across scans, even if it changes.
    /// </summary>
    public DateTime? ExcludedAt { get; set; }

    /// <summary>
    /// When the user put off deciding: it leaves the review list, and each scan tries it again, matching it only when it
    /// beats the threshold.
    /// </summary>
    public DateTime? DeferredAt { get; set; }

    /// <summary>When the user hid it: it's out of the library altogether, though it stays in the music folder.</summary>
    public DateTime? HiddenAt { get; set; }

    /// <summary>
    /// When the user put it back on the review list from those set aside. It's on the list at once, and the next scan
    /// tries to match it again.
    /// </summary>
    public DateTime? PutBackAt { get; set; }

    /// <summary>When a format last rewrote it; a paused format skips files it has already done.</summary>
    public DateTime? FormattedAt { get; set; }
}

public enum RequestMode
{
    Permanent,
    ListenNow,
}

public enum RequestStatus
{
    /// <summary>Waiting for the worker; artist and album requests are split up when it picks them.</summary>
    Queued,

    /// <summary>An artist or album request whose parts are being fetched.</summary>
    InProgress,

    Searching,
    Fetching,
    Verifying,
    Importing,
    Notifying,
    InLibrary,

    /// <summary>Every retrieval plugin was tried and none gave a file that passed verification.</summary>
    NeedsReview,

    Failed,
    Cancelled,

    /// <summary>A listen-now request whose file is ready to play; it isn't in the library unless added.</summary>
    Playable,
}

/// <summary>Something a user or app asked for.</summary>
public class Request
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Artist, album or track; editions are never requested directly.</summary>
    public EntityKind TargetKind { get; set; }

    public Guid TargetId { get; set; }

    /// <summary>
    /// For album and track requests, the edition to fetch: the one asked for, the one already owned, or the one the
    /// planner chose. Fixed once planned, so every track of an album lands on the same edition.
    /// </summary>
    public Guid? EditionId { get; set; }

    /// <summary>What it's for, for example <c>Get Lucky · Daft Punk</c>; kept so the Activity page needs no lookups.</summary>
    public string Title { get; set; } = "";

    /// <summary>What's happening now, or why it stopped, in words.</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// A candidate the user picked on the Activity page, tried next instead of searching: a <see cref="Requests.Choice"/>
    /// as JSON. Cleared once tried.
    /// </summary>
    public string? ChoiceJson { get; set; }

    public RequestMode Mode { get; set; }

    /// <summary>The admin or the API key's name.</summary>
    public required string Requester { get; set; }

    public RequestStatus Status { get; set; }

    /// <summary>
    /// The last status announced to webhooks and the event stream. When it differs from <see cref="Status"/>, the
    /// change hasn't been announced yet; null for requests from before announcements, which are never announced.
    /// </summary>
    public RequestStatus? AnnouncedStatus { get; set; } = RequestStatus.Queued;

    /// <summary>For artist and album requests, the request that fanned out to this one.</summary>
    public Guid? ParentRequestId { get; set; }

    /// <summary>
    /// Where it is in the queue: lower goes first. Starts as when it was made, in seconds, so the queue is oldest first
    /// until someone moves things; parts share their request's place.
    /// </summary>
    public double QueueOrder { get; set; } = QueueOrders.Now();

    /// <summary>Held back by the user: it isn't started until resumed. A request paused mid-fetch starts that fetch again.</summary>
    public bool Paused { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Job> Jobs { get; set; } = [];
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum VerificationResult
{
    NotRun,
    Passed,
    DurationMismatch,
    FingerprintMismatch,
}

/// <summary>One attempt to fulfil a request through one retrieval plugin.</summary>
public class Job
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RequestId { get; set; }
    public Request Request { get; set; } = null!;
    public Guid? PluginInstanceId { get; set; }
    public PluginInstance? PluginInstance { get; set; }

    /// <summary>1 for the first plugin in the chain, 2 for the first fallback, and so on.</summary>
    public int Attempt { get; set; } = 1;

    public JobStatus Status { get; set; }

    /// <summary>The chosen retrieval candidate, as JSON.</summary>
    public string? CandidateJson { get; set; }

    /// <summary>Every candidate the plugin offered, best first, as JSON; the Activity page lets the user pick another.</summary>
    public string? CandidatesJson { get; set; }

    /// <summary>Where the plugin wrote the files, relative to the staging volume; kept for review until resolved.</summary>
    public string? StagingDirectory { get; set; }

    public VerificationResult Verification { get; set; }
    public string? VerificationDetail { get; set; }
    public string Log { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>The listen-now lifecycle. Library actions unlock only at <see cref="InLibrary"/>.</summary>
public enum ListenNowState
{
    Requested,
    Fetching,
    Playable,
    Importing,
    InLibrary,
    Failed,
    Expired,
}

/// <summary>A temporary file held by the service, outside every media-server library.</summary>
public class ListenNowItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RequestId { get; set; }
    public Request Request { get; set; } = null!;
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;

    /// <summary>The edition the track is fetched as, and filed under if it's added to the library.</summary>
    public Guid? EditionId { get; set; }

    public ListenNowState State { get; set; }

    /// <summary>Path relative to the listen-now store.</summary>
    public string? FilePath { get; set; }

    public string? Codec { get; set; }
    public string? Container { get; set; }
    public int? BitrateKbps { get; set; }
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>
    /// When the file is deleted unless it's added: the expiry setting (an hour by default) after it became playable or
    /// was last played, whichever is later.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A configured plugin.</summary>
public class PluginInstance
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The manifest ID, for example <c>melodex.index.musicbrainz</c>.</summary>
    public required string PluginId { get; set; }

    public PluginType Type { get; set; }

    /// <summary>User-facing name, for example <c>YouTube (opus)</c>.</summary>
    public required string Name { get; set; }

    /// <summary>The contract version the plugin declared when it was set up.</summary>
    public required string ContractVersion { get; set; }

    /// <summary>Settings as JSON, validated against the manifest's schema. Includes the output format for retrieval plugins.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>For retrieval plugins, the base URL of the plugin's container.</summary>
    public string? Endpoint { get; set; }

    /// <summary>For retrieval plugins, the manifest as the container last served it, so capabilities are known offline.</summary>
    public string? ManifestJson { get; set; }

    /// <summary>Lower runs first: fallback order for retrieval plugins, field priority for index plugins.</summary>
    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;
    public HealthStatus? Health { get; set; }
    public string? HealthMessage { get; set; }
    public DateTime? HealthCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user setting, stored as JSON under a key.</summary>
/// <summary>
/// The picture shown for an artist, album or edition, kept under <c>data/artwork</c>, or that no artwork plugin had
/// one when last asked.
/// </summary>
public class ArtworkRecord
{
    public EntityKind EntityKind { get; set; }
    public Guid EntityId { get; set; }

    /// <summary>The image file, relative to the artwork folder; null when there's none.</summary>
    public string? File { get; set; }

    public string? ContentType { get; set; }

    /// <summary>The artwork plugin it came from.</summary>
    public Guid? PluginInstanceId { get; set; }

    /// <summary>Chosen by someone rather than found by the plugins, so it's kept when the artwork is fetched afresh.</summary>
    public bool Chosen { get; set; }

    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public class Setting
{
    public required string Key { get; set; }
    public required string ValueJson { get; set; }
}

public enum BackgroundTaskStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Database-backed queue of internal work, such as library scans, so it survives restarts.</summary>
public class BackgroundTask
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What to do, for example <c>library-scan</c>.</summary>
    public required string Kind { get; set; }

    public string? PayloadJson { get; set; }
    public BackgroundTaskStatus Status { get; set; }

    /// <summary>Human-readable progress, for example <c>Matching 40 of 85 files</c>.</summary>
    public string? Progress { get; set; }

    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Whether its finishing has been announced to webhooks and the event stream.</summary>
    public bool Announced { get; set; }

    /// <summary>Where it is in the queue: lower goes first. See <see cref="Request.QueueOrder"/>.</summary>
    public double QueueOrder { get; set; } = QueueOrders.Now();

    /// <summary>
    /// Held back by the user. A paused scan picks up where it stopped when resumed, as it skips files it has already
    /// read and matched; shorter tasks simply run again.
    /// </summary>
    public bool Paused { get; set; }
}

public static class QueueOrders
{
    /// <summary>Now, in seconds: fine enough to keep requests made a moment apart in order.</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// The place to put something so it comes just before <paramref name="next"/>, after <paramref name="previous"/>,
    /// or at an end of the queue when either is missing.
    /// </summary>
    public static double Between(double? previous, double? next) => (previous, next) switch
    {
        ({ } p, { } n) => (p + n) / 2,
        (null, { } n) => n - 1,
        ({ } p, null) => p + 1,
        _ => Now(),
    };
}
