using System.Text.Json;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Contracts.Plugins.Retrieval;

namespace Melodex.Contracts.Plugins.Remote;

/// <summary>
/// The body of every call to a catalogue, artwork, lyrics or media-server plugin reached at an address
/// (docs/contracts/remote-plugins.md): the instance's settings, sent each time so the plugin keeps no setup of its
/// own, and the call's input. Plugins built into Melodex, or loaded from its plugins folder, are called in process
/// instead.
/// </summary>
/// <param name="Settings">The instance's settings, validated against the manifest's settings schema.</param>
/// <param name="Input">What the call is about. Left out of a health check.</param>
public sealed record RemoteCall<T>(JsonElement Settings, T? Input = default);

/// <summary>What a plugin answers when it can't do what was asked, with any status from 400 up.</summary>
/// <param name="Error">Why, in words for the person using Melodex.</param>
/// <param name="Code"><see cref="RemoteErrorCodes.Unavailable"/>, or anything else the plugin likes.</param>
public sealed record RemoteError(string Error, string? Code = null);

/// <summary>The codes in a <see cref="RemoteError"/> that Melodex acts on.</summary>
public static class RemoteErrorCodes
{
    /// <summary>
    /// The plugin's source can't answer right now: overloaded, rate-limiting, down or unreachable. Melodex tries again
    /// later rather than taking it as "not found". A 503 or 429 means the same.
    /// </summary>
    public const string Unavailable = "unavailable";
}

/// <summary>An ID to look up.</summary>
public sealed record IdInput(string Id);

/// <summary>An ISRC to look up.</summary>
public sealed record IsrcInput(string Isrc);

/// <summary>The library-relative paths a scan is for.</summary>
public sealed record ScanInput(IReadOnlyCollection<string> Paths);

/// <summary>A Melodex user's login to the media server.</summary>
public sealed record LoginInput(ServerLogin Login);

/// <summary>One of a user's playlists on the media server.</summary>
public sealed record PlaylistInput(ServerLogin Login, string PlaylistId);

/// <summary>A user's playlist to create (no ID) or replace (with one).</summary>
public sealed record PlaylistContentInput(ServerLogin Login, PlaylistContent Content, string? PlaylistId = null);

/// <summary>A playlist's new cover, or none to remove it.</summary>
public sealed record PlaylistCoverInput(ServerLogin Login, string PlaylistId, PlaylistCover? Cover);

/// <summary>The wire format, the same as the retrieval contract's: camelCase, enums as camelCase strings, nulls left out.</summary>
public static class RemoteJson
{
    /// <summary>Serializer options matching the wire format.</summary>
    public static JsonSerializerOptions Options => RetrievalJson.Options;
}
