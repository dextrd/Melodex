using System.Text.Json;

namespace Melodex.Plugins.Jellyfin;

/// <summary>
/// When each playlist was first seen as it is now, by its ETag, which Jellyfin changes with every change to it. Kept
/// in the plugin's data folder, so a restart doesn't make every playlist look changed.
/// </summary>
internal sealed class PlaylistClock(string file)
{
    private sealed record Seen(string Etag, DateTimeOffset At);

    private readonly Lock _gate = new();
    private Dictionary<string, Seen>? _seen;

    /// <summary>When the playlist changed: when it was first seen with this ETag. Null without one.</summary>
    public DateTimeOffset? Changed(string playlistId, string? etag)
    {
        if (etag is null)
            return null;
        lock (_gate)
        {
            var seen = Load();
            if (seen.TryGetValue(playlistId, out var known) && known.Etag == etag)
                return known.At;
            // To the second, as Melodex compares change times to the second.
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            seen[playlistId] = new Seen(etag, now);
            Save(seen);
            return now;
        }
    }

    public void Forget(string playlistId)
    {
        lock (_gate)
        {
            if (Load().Remove(playlistId))
                Save(_seen!);
        }
    }

    private Dictionary<string, Seen> Load()
    {
        if (_seen is not null)
            return _seen;
        try
        {
            _seen = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Seen>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _seen = [];
        }
        return _seen;
    }

    private void Save(Dictionary<string, Seen> seen)
    {
        try
        {
            var temporary = file + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(seen));
            File.Move(temporary, file, overwrite: true);
        }
        catch (IOException)
        {
            // Kept in memory meanwhile; after a restart, playlists look changed once.
        }
    }
}
