using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Melodex.Core.Data;

namespace Melodex.Core.Playlists;

/// <summary>Playlists' own pictures, kept as files in the data folder.</summary>
public sealed class PlaylistCovers(IOptions<MelodexOptions> options)
{
    private string Root => Path.Combine(options.Value.DataPath, "playlist-covers");

    public string? PathOf(Playlist playlist) => playlist.CoverFile is { } file ? Path.Combine(Root, file) : null;

    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Gives the playlist this picture. Saving the playlist is left to the caller.</summary>
    public async Task SetAsync(Playlist playlist, byte[] data, string contentType, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var file = $"{playlist.Id:N}{Extension(contentType)}";
        if (playlist.CoverFile is { } old && old != file)
            File.Delete(Path.Combine(Root, old));
        await File.WriteAllBytesAsync(Path.Combine(Root, file), data, ct);
        playlist.CoverFile = file;
        playlist.CoverType = contentType;
        playlist.CoverHash = Hash(data);
    }

    /// <summary>Takes the playlist's picture away. Saving the playlist is left to the caller.</summary>
    public void Remove(Playlist playlist)
    {
        if (PathOf(playlist) is { } path)
            File.Delete(path);
        playlist.CoverFile = null;
        playlist.CoverType = null;
        playlist.CoverHash = null;
    }

    public string? PathOf(PlaylistFolder folder) => folder.CoverFile is { } file ? Path.Combine(Root, file) : null;

    /// <summary>Gives the folder this picture. Saving it is left to the caller.</summary>
    public async Task SetAsync(PlaylistFolder folder, byte[] data, string contentType, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var file = $"folder-{folder.Id:N}{Extension(contentType)}";
        if (folder.CoverFile is { } old && old != file)
            File.Delete(Path.Combine(Root, old));
        await File.WriteAllBytesAsync(Path.Combine(Root, file), data, ct);
        folder.CoverFile = file;
        folder.CoverType = contentType;
        folder.CoverHash = Hash(data);
    }

    public void Remove(PlaylistFolder folder)
    {
        if (PathOf(folder) is { } path)
            File.Delete(path);
    }

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };
}
