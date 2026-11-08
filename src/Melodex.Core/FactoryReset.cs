namespace Melodex.Core;

/// <summary>
/// Puts Melodex back as it was first installed: the database (users, settings, plugins, the library's matches,
/// playlists, requests), the library's cache, pictures, logs and Listen Now files all go. The music folder is never
/// touched, nor plugins' own data, nor the keys that sign sessions.
/// </summary>
/// <remarks>
/// Asking for it leaves a mark in the data folder and stops Melodex; the wiping happens as it starts again, before the
/// database is opened, so nothing still running holds on to what's being wiped, or keeps any of it in memory. The
/// compose file restarts Melodex when it stops.
/// </remarks>
public static class FactoryReset
{
    /// <summary>What the admin types to show they mean it.</summary>
    public const string Confirmation = "reset";

    private const string Mark = "factory-reset";

    private static readonly string[] Folders = ["cache", "logs", "artwork", "playlist-covers"];

    public static void Request(MelodexOptions options) =>
        File.WriteAllText(Path.Combine(options.DataPath, Mark), DateTime.UtcNow.ToString("O"));

    /// <summary>Wipes everything, if a reset was asked for. Returns whether it did.</summary>
    public static bool ApplyIfRequested(MelodexOptions options, string database)
    {
        var mark = Path.Combine(options.DataPath, Mark);
        if (!File.Exists(mark))
            return false;
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
            File.Delete(file);
        foreach (var folder in Folders.Select(f => Path.Combine(options.DataPath, f)))
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        // The Listen Now store may be a mount of its own, so it's emptied rather than removed.
        // Never anything that holds music, or is inside the music folder, however it's set up.
        var listenNow = Path.GetFullPath(options.ListenNowPath);
        if (Directory.Exists(listenNow) && !Overlaps(listenNow, Path.GetFullPath(options.LibraryPath)))
        {
            foreach (var file in Directory.EnumerateFiles(listenNow))
                File.Delete(file);
            foreach (var folder in Directory.EnumerateDirectories(listenNow))
                Directory.Delete(folder, recursive: true);
        }
        File.Delete(mark);
        return true;
    }

    private static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(a) + Path.DirectorySeparatorChar;
        b = Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }
}
