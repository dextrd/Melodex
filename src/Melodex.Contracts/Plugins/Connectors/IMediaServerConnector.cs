namespace Melodex.Contracts.Plugins.Connectors;

/// <summary>
/// Media-server connector contract (version 1.3): tells a media server about new music and confirms it's
/// there. Optional features are separate interfaces, each matched by a capability name in the manifest;
/// the core and API only offer actions the active connector supports.
/// </summary>
public interface IMediaServerConnector : IPlugin
{
    /// <summary>
    /// Asks the server to pick up changes. <paramref name="paths"/> are library-relative paths that changed;
    /// servers that can't scan selectively may ignore them and scan everything new.
    /// </summary>
    Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);

    /// <summary>Whether a scan is running, and when the last one finished.</summary>
    Task<ScanStatus> GetScanStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Finds the server's track ID for a file or catalogue ID. Returns <c>null</c> when the server doesn't
    /// have the track (yet); the core polls until it does or gives up.
    /// </summary>
    Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken cancellationToken);
}

/// <summary>State of the server's scanner.</summary>
/// <param name="Scanning">Whether a scan is running.</param>
/// <param name="LastScanFinishedAt">When the last scan finished, if the server reports it.</param>
/// <param name="TrackCount">Tracks in the server's library, if reported.</param>
public sealed record ScanStatus(bool Scanning, DateTimeOffset? LastScanFinishedAt = null, long? TrackCount = null);

/// <summary>
/// What to look a track up by. Connectors should try the most specific fields first: the path, then the
/// MusicBrainz recording ID or ISRC, then title, artist, album and duration.
/// </summary>
/// <param name="Path">Library-relative path of the file.</param>
/// <param name="MusicBrainzRecordingId">MusicBrainz recording ID from the file's tags.</param>
/// <param name="MusicBrainzReleaseId">MusicBrainz release ID from the file's tags.</param>
/// <param name="Title">Track title.</param>
/// <param name="Artist">Track artist.</param>
/// <param name="Album">Album title.</param>
/// <param name="Duration">Length.</param>
/// <param name="Isrc">ISRC from the file's tags.</param>
public sealed record TrackLookup(
    string? Path = null,
    string? MusicBrainzRecordingId = null,
    string? MusicBrainzReleaseId = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    TimeSpan? Duration = null,
    string? Isrc = null);

/// <summary>Optional: full rescan. Capability <see cref="ConnectorCapabilities.FullScan"/>.</summary>
public interface IFullScan
{
    /// <summary>Rescans the whole library, including unchanged files.</summary>
    Task TriggerFullScanAsync(CancellationToken cancellationToken);
}

/// <summary>Optional: playlists. Capability <see cref="ConnectorCapabilities.Playlists"/>.</summary>
public interface IPlaylistManager
{
    /// <summary>Lists playlists.</summary>
    Task<IReadOnlyList<ServerPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken);

    /// <summary>Creates a playlist and returns its server ID.</summary>
    Task<string> CreatePlaylistAsync(string name, IReadOnlyList<string> trackIds, CancellationToken cancellationToken);

    /// <summary>Appends tracks to a playlist.</summary>
    Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken cancellationToken);
}

/// <summary>A playlist on the server.</summary>
/// <param name="Id">Server ID.</param>
/// <param name="Name">Name.</param>
/// <param name="TrackCount">Number of tracks.</param>
public sealed record ServerPlaylist(string Id, string Name, int TrackCount);

/// <summary>
/// Optional: each user's own playlists, kept the same in Melodex and on the server. Capability
/// <see cref="ConnectorCapabilities.UserPlaylists"/>; added in 1.1. Every call acts as the user whose login it's given,
/// so they only ever see and change their own playlists.
/// </summary>
public interface IPlaylistSync
{
    /// <summary>Whether the connector's settings say to keep playlists in step: its "sync playlists" box.</summary>
    bool SyncsPlaylists { get; }

    /// <summary>Checks the login works, for linking it to a Melodex user.</summary>
    Task<HealthReport> CheckLoginAsync(ServerLogin login, CancellationToken cancellationToken);

    /// <summary>The playlists the user owns, without their songs.</summary>
    Task<IReadOnlyList<UserPlaylist>> GetPlaylistsAsync(ServerLogin login, CancellationToken cancellationToken);

    /// <summary>A playlist with its songs in order, or null if it's gone.</summary>
    Task<UserPlaylistSongs?> GetPlaylistAsync(ServerLogin login, string playlistId, CancellationToken cancellationToken);

    /// <summary>Makes a playlist and returns its server ID.</summary>
    Task<string> CreatePlaylistAsync(ServerLogin login, PlaylistContent content, CancellationToken cancellationToken);

    /// <summary>Gives a playlist this name, comment and these songs, in this order, replacing what it had.</summary>
    Task UpdatePlaylistAsync(ServerLogin login, string playlistId, PlaylistContent content, CancellationToken cancellationToken);

    /// <summary>Deletes a playlist. One that's already gone isn't an error.</summary>
    Task DeletePlaylistAsync(ServerLogin login, string playlistId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether playlists' own pictures are synced too: <see cref="GetPlaylistCoverAsync"/> and
    /// <see cref="SetPlaylistCoverAsync"/> work. Since 1.2; connectors built for 1.1 don't.
    /// </summary>
    bool SyncsPlaylistCovers => false;

    /// <summary>
    /// The picture someone gave the playlist, or null when it has none of its own (a picture the server makes from its
    /// songs doesn't count). Since 1.2.
    /// </summary>
    Task<PlaylistCover?> GetPlaylistCoverAsync(ServerLogin login, string playlistId, CancellationToken cancellationToken) =>
        Task.FromResult<PlaylistCover?>(null);

    /// <summary>Gives the playlist this picture, or with null, takes its picture away. Since 1.2.</summary>
    Task SetPlaylistCoverAsync(ServerLogin login, string playlistId, PlaylistCover? cover, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>A playlist's own picture.</summary>
/// <param name="Data">The image file.</param>
/// <param name="ContentType">Its type, such as <c>image/jpeg</c>.</param>
public sealed record PlaylistCover(byte[] Data, string ContentType);

/// <summary>A user's login on the media server.</summary>
public sealed record ServerLogin(string Username, string Password)
{
    /// <summary>Keeps the password out of logs.</summary>
    public override string ToString() => $"ServerLogin {{ Username = {Username} }}";
}

/// <summary>A user's playlist on the server.</summary>
/// <param name="Id">Server ID.</param>
/// <param name="Name">Name, folders and all: the server has no folders, so they're part of it.</param>
/// <param name="Comment">Its description, if any.</param>
/// <param name="SongCount">Number of songs.</param>
/// <param name="Changed">When it last changed on the server, to tell whether it has since Melodex last looked.</param>
public sealed record UserPlaylist(string Id, string Name, string? Comment, int SongCount, DateTimeOffset? Changed)
{
    /// <summary>
    /// Whether it has a picture of its own, if the connector can tell without fetching it: false spares asking for one.
    /// Since 1.2.
    /// </summary>
    public bool? HasCover { get; init; }
}

/// <summary>A playlist with its songs.</summary>
public sealed record UserPlaylistSongs(UserPlaylist Playlist, IReadOnlyList<ServerTrack> Songs);

/// <summary>What a playlist should be: its name, comment, and server track IDs in order.</summary>
public sealed record PlaylistContent(string Name, string? Comment, IReadOnlyList<string> TrackIds);

/// <summary>Optional: favourites. Capability <see cref="ConnectorCapabilities.Favourites"/>.</summary>
public interface IFavouritesManager
{
    /// <summary>Stars or unstars a track.</summary>
    Task SetFavouriteAsync(string trackId, bool favourite, CancellationToken cancellationToken);
}

/// <summary>Optional: reading the server's library. Capability <see cref="ConnectorCapabilities.LibraryRead"/>.</summary>
public interface ILibraryReader
{
    /// <summary>Streams every track the server knows, for cross-checking with the library scan.</summary>
    IAsyncEnumerable<ServerTrack> GetTracksAsync(CancellationToken cancellationToken);
}

/// <summary>A track as the server sees it.</summary>
/// <param name="Id">Server track ID.</param>
/// <param name="Path">Library-relative path, if the server exposes it.</param>
/// <param name="Title">Title.</param>
/// <param name="Artist">Artist.</param>
/// <param name="Album">Album.</param>
/// <param name="Duration">Length.</param>
/// <param name="MusicBrainzRecordingId">MusicBrainz recording ID, if the server exposes it.</param>
public sealed record ServerTrack(
    string Id,
    string? Path,
    string Title,
    string? Artist,
    string? Album,
    TimeSpan? Duration,
    string? MusicBrainzRecordingId);

/// <summary>Optional: which server it is, and its version (1.3). Capability <see cref="ConnectorCapabilities.ServerIdentity"/>.</summary>
public interface IServerIdentity
{
    /// <summary>The server's kind and version, or null when it can't be reached.</summary>
    Task<ServerIdentity?> GetServerAsync(CancellationToken cancellationToken);
}

/// <summary>A media server, as it names itself.</summary>
/// <param name="Kind">What it is, lower case: <c>navidrome</c>.</param>
/// <param name="Version">Its version, as it reports it: <c>0.64.2 (4d1e8c42)</c>.</param>
public sealed record ServerIdentity(string Kind, string? Version);

/// <summary>Capability names a connector can declare in its manifest.</summary>
public static class ConnectorCapabilities
{
    /// <summary>Implements <see cref="IFullScan"/>.</summary>
    public const string FullScan = "full-scan";

    /// <summary>Implements <see cref="IPlaylistManager"/>.</summary>
    public const string Playlists = "playlists";

    /// <summary>Implements <see cref="IFavouritesManager"/>.</summary>
    public const string Favourites = "favourites";

    /// <summary>Implements <see cref="ILibraryReader"/>.</summary>
    public const string LibraryRead = "library-read";

    /// <summary>Implements <see cref="IPlaylistSync"/>.</summary>
    public const string UserPlaylists = "user-playlists";

    /// <summary><see cref="IMediaServerConnector.TriggerScanAsync"/> honours its paths.</summary>
    public const string SelectiveScan = "selective-scan";

    /// <summary>Implements <see cref="IServerIdentity"/> (1.3).</summary>
    public const string ServerIdentity = "server-identity";
}
