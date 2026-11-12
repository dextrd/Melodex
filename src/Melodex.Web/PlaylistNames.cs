using Melodex.Client;

namespace Melodex.Web;

/// <summary>Folders in playlist names, split by the delimiter set for the media server in use.</summary>
public static class PlaylistNames
{
    public static string Delimiter(AuthState auth) => auth.Status?.PlaylistFolders ?? "/";

    /// <summary>The folders a name is in, outermost first.</summary>
    public static IReadOnlyList<string> Folders(string name, string delimiter) =>
        delimiter.Length == 0 ? [] : name.Split(delimiter).SkipLast(1).Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

    /// <summary>The playlist's own name, without its folders.</summary>
    public static string Leaf(string name, string delimiter)
    {
        if (delimiter.Length == 0)
            return name;
        var leaf = name.Split(delimiter)[^1].Trim();
        return leaf.Length > 0 ? leaf : name;
    }

    /// <summary>"12 songs · 48:10".</summary>
    public static string Length(int count, double seconds) =>
        $"{count} {(count == 1 ? "song" : "songs")}{(seconds > 0 ? $" · {Format.Duration(seconds)}" : "")}";

    /// <summary>The song as something to add to a playlist, or null for one fetched only to listen to.</summary>
    public static PlaylistItem? ItemOf(PlayerEntry entry) =>
        entry.FileId is { } file ? new PlaylistItem(FileId: file)
        : entry.ListenId is null || entry.ListenState == ListenNowState.InLibrary ? new PlaylistItem(entry.TrackId)
        : null;

    /// <summary>The songs that can be played, ready for the player.</summary>
    public static IReadOnlyList<PlayerEntry> Playable(IEnumerable<PlaylistSong> songs) => songs
        .Where(s => s.State is PlaylistSongState.Library or PlaylistSongState.Local)
        .Select(s => new PlayerEntry
        {
            TrackId = s.State == PlaylistSongState.Local ? s.FileId!.Value : s.TrackId!.Value,
            FileId = s.State == PlaylistSongState.Local ? s.FileId : null,
            Title = s.Title, ArtistCredit = s.Artist, ArtistId = s.ArtistId, AlbumId = s.AlbumId, AlbumTitle = s.AlbumTitle,
            CoverUrl = s.CoverUrl, DurationSeconds = s.DurationSeconds,
        })
        .ToList();
}
