using System.Text.Json;
using System.Text.Json.Serialization;
using Melodex.Contracts.Catalogue;

namespace Melodex.Contracts.Plugins.Retrieval;

// Retrieval plugins run out of process, so their contract (version 1.0) is HTTP, described in
// docs/contracts/retrieval-v1.openapi.yaml. These types are its wire format, shared by the core's client and
// any .NET implementation. Durations are seconds so plugins in any language can use them directly.

/// <summary>Whether a target or candidate is a single track or a whole album.</summary>
public enum RetrievalKind
{
    /// <summary>One track.</summary>
    Track,

    /// <summary>A whole album.</summary>
    Album,
}

/// <summary>What the core wants fetched, with every ID it knows.</summary>
/// <param name="Kind">Track or album.</param>
/// <param name="Title">Track or album title.</param>
/// <param name="Artist">Credited artist.</param>
/// <param name="Album">Album title, for a track target.</param>
/// <param name="DurationSeconds">Expected length of the track, or of the whole album.</param>
/// <param name="Tracks">For an album target, the expected track listing.</param>
/// <param name="Ids">Known IDs: MusicBrainz, ISRC, UPC and any other provider's.</param>
public sealed record RetrievalTarget(
    RetrievalKind Kind,
    string Title,
    string Artist,
    string? Album = null,
    double? DurationSeconds = null,
    IReadOnlyList<RetrievalTargetTrack>? Tracks = null,
    IReadOnlyList<ProviderId>? Ids = null);

/// <summary>One expected track of an album target.</summary>
/// <param name="Disc">1-based disc number.</param>
/// <param name="Position">1-based position on the disc.</param>
/// <param name="Title">Title.</param>
/// <param name="DurationSeconds">Expected length.</param>
/// <param name="Ids">Known IDs for the track.</param>
public sealed record RetrievalTargetTrack(
    int Disc,
    int Position,
    string Title,
    double? DurationSeconds = null,
    IReadOnlyList<ProviderId>? Ids = null);

/// <summary>Body of <c>POST /v1/health</c>.</summary>
/// <param name="Settings">The plugin instance's settings.</param>
public sealed record HealthRequest(JsonElement Settings);

/// <summary>Answer to <c>POST /v1/checks/{id}</c>, whose body is a <see cref="HealthRequest"/>.</summary>
/// <param name="Passed">Whether what's checked works.</param>
/// <param name="Message">What was found, and why not when it doesn't.</param>
public sealed record CheckResult(bool Passed, string Message);

/// <summary>Body of <c>POST /v1/search</c>.</summary>
/// <param name="Settings">The plugin instance's settings.</param>
/// <param name="Target">What to find.</param>
/// <param name="Limit">Maximum candidates to return.</param>
public sealed record SearchRequest(JsonElement Settings, RetrievalTarget Target, int Limit = 10);

/// <summary>Response of <c>POST /v1/search</c>.</summary>
/// <param name="Candidates">Candidates, best first.</param>
public sealed record SearchResponse(IReadOnlyList<Candidate> Candidates);

/// <summary>Something the plugin could fetch for a target.</summary>
/// <param name="CandidateId">
/// Opaque to the core and passed back unchanged to <c>POST /v1/fetch</c>. It must carry everything the plugin
/// needs to fetch, because plugins are stateless between calls.
/// </param>
/// <param name="Kind">Track or album.</param>
/// <param name="Title">Title as the source shows it.</param>
/// <param name="Artist">Artist as the source shows it.</param>
/// <param name="Album">Album as the source shows it.</param>
/// <param name="DurationSeconds">Length as the source reports it.</param>
/// <param name="Source">Human-readable origin, shown on the Activity page.</param>
/// <param name="Url">Link to the item at its source, if it has one.</param>
/// <param name="Format">Output format the fetched file will have, for example <c>opus</c> or <c>flac</c>.</param>
/// <param name="BitrateKbps">Expected bitrate.</param>
/// <param name="Score">The plugin's own confidence from 0 to 1 that this matches the target.</param>
/// <param name="BitDepth">For lossless, the bits per sample the file will have (16, 24), if known.</param>
/// <param name="SampleRateHz">The sample rate the file will have (44100, 96000), if known.</param>
/// <remarks>
/// The core ranks candidates by the user's quality profile from <see cref="Format"/>, <see cref="BitrateKbps"/>,
/// <see cref="BitDepth"/> and <see cref="SampleRateHz"/>. A source with several qualities offers a candidate for each.
/// </remarks>
public sealed record Candidate(
    string CandidateId,
    RetrievalKind Kind,
    string Title,
    string? Artist,
    string? Album,
    double? DurationSeconds,
    string Source,
    string? Url,
    string? Format,
    int? BitrateKbps,
    double? Score,
    int? BitDepth = null,
    int? SampleRateHz = null);

/// <summary>Body of <c>POST /v1/fetch</c>.</summary>
/// <param name="Settings">The plugin instance's settings.</param>
/// <param name="JobId">Core-assigned ID; the plugin uses it for <c>GET</c> and <c>DELETE /v1/fetch/{jobId}</c>.</param>
/// <param name="Target">What the candidate was chosen for.</param>
/// <param name="Candidate">The chosen candidate, as returned by search.</param>
/// <param name="StagingDirectory">
/// Directory relative to the staging volume root to write into. The core creates it; the plugin writes
/// only inside it.
/// </param>
public sealed record FetchRequest(
    JsonElement Settings,
    string JobId,
    RetrievalTarget Target,
    Candidate Candidate,
    string StagingDirectory);

/// <summary>States of a fetch.</summary>
public enum FetchState
{
    /// <summary>Accepted, not started.</summary>
    Queued,

    /// <summary>Downloading or converting.</summary>
    Running,

    /// <summary>Every file is in the staging directory.</summary>
    Completed,

    /// <summary>Gave up; see <see cref="FetchStatus.Error"/>.</summary>
    Failed,

    /// <summary>Cancelled by the core.</summary>
    Cancelled,
}

/// <summary>Response of <c>POST /v1/fetch</c> and <c>GET /v1/fetch/{jobId}</c>.</summary>
/// <param name="JobId">The core-assigned job ID.</param>
/// <param name="State">Current state.</param>
/// <param name="Progress">From 0 to 1, if known.</param>
/// <param name="Files">Files written, filled once <see cref="FetchState.Completed"/>.</param>
/// <param name="Error">Why it failed.</param>
public sealed record FetchStatus(
    string JobId,
    FetchState State,
    double? Progress = null,
    IReadOnlyList<FetchedFile>? Files = null,
    string? Error = null);

/// <summary>A file the plugin wrote to staging.</summary>
/// <param name="Path">Path relative to the request's staging directory.</param>
/// <param name="Codec">Audio codec, for example <c>opus</c>, <c>aac</c> or <c>flac</c>.</param>
/// <param name="Container">Container, for example <c>ogg</c>, <c>m4a</c> or <c>flac</c>.</param>
/// <param name="BitrateKbps">Bitrate.</param>
/// <param name="DurationSeconds">Length.</param>
/// <param name="Disc">For album fetches, the disc it belongs to, if the plugin knows.</param>
/// <param name="Position">For album fetches, its position on the disc, if the plugin knows.</param>
public sealed record FetchedFile(
    string Path,
    string? Codec,
    string? Container,
    int? BitrateKbps,
    double? DurationSeconds,
    int? Disc = null,
    int? Position = null);

/// <summary>Error body for any non-2xx response.</summary>
/// <param name="Error">Human-readable message.</param>
/// <param name="Code">Machine-readable code, for example <c>invalid-settings</c> or <c>unknown-job</c>.</param>
public sealed record RetrievalError(string Error, string? Code = null);

/// <summary>Capability names a retrieval plugin can declare in its manifest.</summary>
public static class RetrievalCapabilities
{
    /// <summary>Can fetch single tracks.</summary>
    public const string FetchTrack = "fetch-track";

    /// <summary>Can fetch whole albums in one job.</summary>
    public const string FetchAlbum = "fetch-album";

    /// <summary>Supports <c>DELETE /v1/fetch/{jobId}</c>.</summary>
    public const string Cancel = "cancel";
}

/// <summary>JSON settings for the retrieval HTTP contract: camelCase names and enums, nulls omitted.</summary>
public static class RetrievalJson
{
    /// <summary>Serializer options matching the wire format.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
