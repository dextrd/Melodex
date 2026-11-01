namespace Melodex.Client;

/// <summary>A song to put in a playlist: a track in your library (as one of its files, or whichever plays), or a file of your own.</summary>
public sealed record PlaylistItem(Guid? TrackId = null, Guid? FileId = null);

/// <summary>How a playlist stands with the media server.</summary>
public enum PlaylistSyncState
{
    /// <summary>Playlists aren't synced: the connector doesn't, or you haven't linked your login.</summary>
    Off,

    /// <summary>The same in both.</summary>
    Synced,

    /// <summary>Changed since the last sync, or songs the server hasn't found yet.</summary>
    Waiting,
}

/// <summary>A playlist, as listed. Folders are part of its name, split by your folder delimiter.</summary>
/// <param name="Covers">Its own picture, or up to four covers of the albums in it, for a mosaic.</param>
/// <param name="Pinned">Pinned by you, to show first.</param>
/// <param name="OwnCover">It has a picture of its own, rather than its albums' covers.</param>
public sealed record PlaylistSummary(
    Guid Id,
    string Name,
    string? Comment,
    int Count,
    double DurationSeconds,
    IReadOnlyList<string> Covers,
    DateTime UpdatedAt,
    PlaylistSyncState Sync,
    bool Pinned = false,
    bool OwnCover = false);

/// <summary>Body of <c>PUT /playlists/{id}/cover</c>: a picture, base64, up to 10 MB: JPEG, PNG, WebP or GIF.</summary>
public sealed record SetPlaylistCover(string Upload);

/// <summary>Where a song in a playlist is.</summary>
public enum PlaylistSongState
{
    /// <summary>In your library, to play.</summary>
    Library,

    /// <summary>A file of yours no catalogue knows, to play.</summary>
    Local,

    /// <summary>Only the media server has it: it came with the server's copy of the playlist.</summary>
    ServerOnly,

    /// <summary>No longer in your library.</summary>
    Gone,
}

/// <summary>A song in a playlist.</summary>
/// <param name="EntryId">This place in the playlist: the same song can be in it more than once.</param>
public sealed record PlaylistSong(
    Guid EntryId,
    Guid? TrackId,
    Guid? FileId,
    string Title,
    string Artist,
    Guid? ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    string? CoverUrl,
    double? DurationSeconds,
    PlaylistSongState State);

public sealed record PlaylistDetail(PlaylistSummary Playlist, IReadOnlyList<PlaylistSong> Songs);

/// <summary>Body of <c>POST /playlists</c>.</summary>
public sealed record CreatePlaylist(string Name, string? Comment = null, IReadOnlyList<PlaylistItem>? Songs = null);

/// <summary>Body of <c>PUT /playlists/{id}</c>: a new name or comment. An empty comment removes it.</summary>
public sealed record UpdatePlaylist(string? Name = null, string? Comment = null);

/// <summary>Body of <c>POST /playlists/{id}/songs</c>.</summary>
/// <param name="Duplicates">Add songs already in it again; otherwise they're left out.</param>
public sealed record AddToPlaylist(IReadOnlyList<PlaylistItem> Songs, bool Duplicates = false);

/// <summary>What adding did.</summary>
/// <param name="AlreadyThere">Songs left out as they're in it already.</param>
/// <param name="NotFound">Songs that aren't in your library.</param>
public sealed record PlaylistAdded(int Added, int AlreadyThere, int NotFound);

/// <summary>Body of <c>PUT /playlists/{id}/songs</c>: the songs to keep, by entry, in their new order.</summary>
public sealed record ArrangePlaylist(IReadOnlyList<Guid> EntryIds);

/// <summary>Body of <c>PUT /playlists/folders</c>: renames a folder, moving everything in it.</summary>
/// <param name="From">The folder's whole name, its parents' too, as in a playlist's name.</param>
/// <param name="To">Its new whole name. Empty takes what's in it out of the folder.</param>
/// <param name="Delimiter">What separates folders in names.</param>
public sealed record RenamePlaylistFolder(string From, string To, string Delimiter);

/// <summary>Whether your playlists are synced with the media server, and how it went.</summary>
/// <param name="Available">The media server's connector keeps playlists in step: its "sync playlists" box is ticked.</param>
/// <param name="Server">The connector's name, if it can sync playlists at all.</param>
/// <param name="Username">Your login there, once linked.</param>
/// <param name="SyncedAt">When they were last synced.</param>
/// <param name="Error">Why the last sync failed, if it did.</param>
public sealed record PlaylistSyncStatus(bool Available, string? Server, string? Username, DateTime? SyncedAt, string? Error)
{
    public bool Linked => Username is not null;
}

/// <summary>Body of <c>PUT /auth/media-server</c> and <c>PUT /users/{id}/media-server</c>: a login on the media server. It's checked before it's kept.</summary>
public sealed record LinkMediaServer(string Username, string Password);

/// <summary>A folder of playlists with a picture of its own.</summary>
/// <param name="Path">The folder's path, its parents' names and its own joined by your separator.</param>
/// <param name="CoverUrl">Its picture; null when the folder icon stands for it.</param>
public sealed record PlaylistFolderInfo(Guid Id, string Path, string? CoverUrl);

/// <summary>Body of <c>PUT /playlists/folders/cover</c>.</summary>
/// <param name="Upload">The picture, base64.</param>
public sealed record SetPlaylistFolderCover(string Path, string Upload);
