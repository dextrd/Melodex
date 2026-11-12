namespace Melodex.Client;

/// <summary>Where a listen-now track is in its life. Library actions (playlists, stars) unlock only at <see cref="InLibrary"/>.</summary>
public enum ListenNowState
{
    /// <summary>Waiting for the request worker.</summary>
    Requested,

    /// <summary>Being searched for, fetched and checked.</summary>
    Fetching,

    /// <summary>Ready to stream; not in the library.</summary>
    Playable,

    /// <summary>Being added: filed by beets and picked up by the media server.</summary>
    Importing,

    /// <summary>In the library; play it from the media server from now on.</summary>
    InLibrary,

    /// <summary>No plugin found a file that passed the checks.</summary>
    Failed,

    /// <summary>Discarded, or not played for longer than the expiry setting; the file is gone.</summary>
    Expired,
}

/// <summary>Body of <c>POST /listen</c>.</summary>
/// <param name="Kind">A track, or an album to listen to its missing tracks in order.</param>
/// <param name="Id">The track or album.</param>
/// <param name="EditionId">The edition to fetch as; by default the one you have part of, or the usual one.</param>
/// <param name="FromTrackId">
/// For an album played from one of its tracks, that track: the missing tracks from it on are fetched first, then the
/// ones before it.
/// </param>
public sealed record StartListen(RequestKind Kind, Guid Id, Guid? EditionId = null, Guid? FromTrackId = null);

/// <summary>A track fetched to listen to without adding it to the library.</summary>
/// <param name="Detail">What's happening, or why it failed or couldn't be added.</param>
/// <param name="MediaType">What to check with the player before playing, for example <c>audio/ogg; codecs=opus</c>.</param>
/// <param name="StreamUrl">A signed URL that plays the file, with seeking; it works for 12 hours. Only while playable.</param>
/// <param name="ExpiresAt">When the file goes unless it's added or played again. Each play pushes it back.</param>
/// <param name="RequestId">The request behind it, which the Activity page shows with its fetch attempts.</param>
/// <param name="MediaServerTrackId">Once in the library, the media server's ID for it, to play it from there instead.</param>
/// <param name="Paused">Its request is held back; resume it with <c>POST /requests/{requestId}/resume</c>.</param>
/// <param name="QueueOrder">Its place among listen-now tracks waiting to be fetched: lower goes first.</param>
public sealed record ListenNowInfo(
    Guid Id,
    Guid TrackId,
    string Title,
    string ArtistCredit,
    Guid? ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    Guid? EditionId,
    string? CoverUrl,
    ListenNowState State,
    string? Detail,
    double? DurationSeconds,
    string? Codec,
    string? Container,
    int? BitrateKbps,
    string? MediaType,
    string? StreamUrl,
    DateTime? ExpiresAt,
    Guid RequestId,
    string? MediaServerTrackId,
    DateTime CreatedAt,
    bool Paused = false,
    double QueueOrder = 0)
{
    /// <summary>Still on its way to being playable.</summary>
    public bool Pending => State is ListenNowState.Requested or ListenNowState.Fetching;
}

/// <summary>How to play a track you have, from <c>GET /tracks/{id}/play</c>.</summary>
/// <param name="StreamUrl">A signed URL that plays the library file, with seeking; it works for 12 hours.</param>
/// <param name="MediaType">What to check with the player before playing.</param>
public sealed record PlayInfo(
    Guid TrackId,
    Guid FileId,
    string Title,
    string ArtistCredit,
    Guid? ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    string? CoverUrl,
    double? DurationSeconds,
    string? Codec,
    string MediaType,
    string StreamUrl);

/// <summary>A server-sent event from <c>GET /events</c>: what changed, so a client can reload what it shows.</summary>
/// <param name="Type"><c>requests</c>, <c>listen</c>, <c>library</c> or <c>tasks</c>.</param>
public sealed record ServerEvent(string Type, DateTime At);
