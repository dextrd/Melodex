namespace Melodex.Core.Data;

/// <summary>
/// One user's playlist. Folders are part of the name, split by the user's folder delimiter, as media servers have no
/// folders. When the media server keeps playlists in step, it's linked to the server's copy and synced both ways.
/// </summary>
public class Playlist
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public required string Name { get; set; }
    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it was last changed in Melodex, by its owner or by a sync bringing the server's changes.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Deleted, but still on the server: kept until a sync deletes it there too.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>The connector it's synced with, and its ID there.</summary>
    public Guid? ServerInstanceId { get; set; }

    public string? ServerId { get; set; }

    /// <summary>When the server said it last changed, at the last sync: a different time means it's changed there since.</summary>
    public DateTime? ServerChangedAt { get; set; }

    /// <summary>When it was last synced; changed in Melodex since if <see cref="UpdatedAt"/> is later.</summary>
    public DateTime? SyncedAt { get; set; }

    /// <summary>
    /// The server's copy needs sending again though nothing changed here: songs it couldn't find yet, or files that
    /// moved and may have new server IDs.
    /// </summary>
    public bool NeedsPush { get; set; }

    /// <summary>Pinned by its owner, to show first.</summary>
    public DateTime? PinnedAt { get; set; }

    /// <summary>Its own picture, if it has one, under the data folder's <c>playlist-covers</c>; otherwise its albums' covers stand in.</summary>
    public string? CoverFile { get; set; }

    public string? CoverType { get; set; }

    /// <summary>The picture's SHA-256, to tell whether the server's is the same.</summary>
    public string? CoverHash { get; set; }

    /// <summary>When its picture was last set or taken away here.</summary>
    public DateTime? CoverChangedAt { get; set; }

    /// <summary>Its picture was changed here and the server's copy needs it.</summary>
    public bool CoverNeedsPush { get; set; }

    /// <summary>The server's picture at the last sync, by its hash: null for none. A different one means it's changed there.</summary>
    public string? ServerCoverHash { get; set; }

    /// <summary>When the server's picture was last looked at or sent: never, for playlists synced before pictures were.</summary>
    public DateTime? CoverSyncedAt { get; set; }

    public List<PlaylistEntry> Entries { get; set; } = [];
}

/// <summary>
/// A song in a playlist: a track in the library, a file of your own no catalogue knows, or a song only the media
/// server has. What it was called is kept, so it still shows if it leaves the library.
/// </summary>
public class PlaylistEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;
    public int Position { get; set; }

    /// <summary>The catalogue track, for a song in the library.</summary>
    public Guid? TrackId { get; set; }

    /// <summary>The file it was added as: which copy plays, and the song itself for files no catalogue knows.</summary>
    public Guid? FileId { get; set; }

    public required string Title { get; set; }
    public string Artist { get; set; } = "";
    public string? Album { get; set; }
    public double? DurationSeconds { get; set; }

    /// <summary>Its ID on the media server, once found there.</summary>
    public string? ServerTrackId { get; set; }
}

/// <summary>A folder of a user's playlists with a picture of its own. Folders are only parts of playlists' names, so one has a row only for its picture.</summary>
public class PlaylistFolder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>The folder's path, its parents' names and its own joined by the user's separator, as in playlists' names.</summary>
    public required string Path { get; set; }

    public string? CoverFile { get; set; }
    public string? CoverType { get; set; }
    public string? CoverHash { get; set; }
}
