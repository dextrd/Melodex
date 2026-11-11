using System.Globalization;
using System.Net;
using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Plex;

/// <summary>
/// Plex Media Server, over its HTTP API with a Plex token. Plex can scan just the folders that changed, and lists each
/// song's file, so songs are found by their path first, then their MusicBrainz ID, then their tags.
/// </summary>
/// <remarks>
/// Plex's accounts are plex.tv's, not logins to the server, so users' playlists don't sync with it as they do with
/// Navidrome.
/// </remarks>
public sealed class PlexConnector : IMediaServerConnector, IFullScan, IServerIdentity
{
    // More folders than this at once, and the whole library is scanned instead.
    private const int MostFolders = 20;

    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly string _token;
    private readonly string? _library;
    private readonly string? _musicFolder;

    public PlexConnector(PluginContext context)
    {
        _http = context.HttpClient;
        var settings = context.Settings;
        _server = new Uri(settings.GetProperty("url").GetString()!.TrimEnd('/') + "/");
        _token = settings.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
        _library = settings.TryGetProperty("library", out var l) && l.GetString() is { Length: > 0 } library ? library.Trim() : null;
        _musicFolder = settings.TryGetProperty("musicFolder", out var f) && f.GetString() is { Length: > 0 } folder ? folder.Trim() : null;
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var server = await GetServerAsync(cancellationToken);
            var section = await SectionAsync(cancellationToken);
            return new HealthReport(HealthStatus.Healthy, $"Connected to Plex {server?.Version}, library \"{section.Title}\" at {section.Root}");
        }
        catch (PlexException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach {_server}: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"{_server} didn't answer in time.");
        }
    }

    public async Task<ServerIdentity?> GetServerAsync(CancellationToken cancellationToken)
    {
        var container = await GetAsync("identity", cancellationToken);
        return new ServerIdentity("plex", Text(container, "version"));
    }

    /// <summary>Scans the folders the files are in, or the whole library when there are many.</summary>
    public async Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var section = await SectionAsync(cancellationToken);
        var folders = paths.Select(FolderOf).Distinct(StringComparer.Ordinal).ToList();
        if (folders.Count is 0 or > MostFolders || folders.Contains(""))
        {
            await GetAsync($"library/sections/{section.Key}/refresh", cancellationToken);
            return;
        }
        foreach (var folder in folders)
            await GetAsync($"library/sections/{section.Key}/refresh?path={Uri.EscapeDataString(section.Root.TrimEnd('/') + "/" + folder)}", cancellationToken);
    }

    public async Task TriggerFullScanAsync(CancellationToken cancellationToken)
    {
        var section = await SectionAsync(cancellationToken);
        await GetAsync($"library/sections/{section.Key}/refresh", cancellationToken);
    }

    public async Task<ScanStatus> GetScanStatusAsync(CancellationToken cancellationToken)
    {
        var section = await SectionAsync(cancellationToken);
        var songs = await GetAsync($"library/sections/{section.Key}/all?type=10&X-Plex-Container-Start=0&X-Plex-Container-Size=0", cancellationToken);
        long? count = Number(songs, "totalSize") ?? Number(songs, "size");
        return new ScanStatus(section.Refreshing, section.ScannedAt, count);
    }

    public async Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lookup.Title))
            return null;
        var section = await SectionAsync(cancellationToken);
        if (TrackMatching.Pick(await SongsAsync(section, lookup.Title, cancellationToken), lookup) is { } found)
            return found.Id;
        // Plex finds titles containing the words asked for, so "Digital Love (album version)" doesn't find "Digital Love".
        if (TrackMatching.WithoutBrackets(lookup.Title) is { } bare)
            return TrackMatching.Pick(await SongsAsync(section, bare, cancellationToken), lookup with { Title = bare })?.Id;
        return null;
    }

    private async Task<IReadOnlyList<TrackMatching.Candidate>> SongsAsync(Section section, string title, CancellationToken ct)
    {
        var container = await GetAsync($"library/sections/{section.Key}/all?type=10&includeGuids=1&title={Uri.EscapeDataString(title)}", ct);
        return Array(container, "Metadata").Select(Song).ToList();
    }

    private static TrackMatching.Candidate Song(JsonElement track)
    {
        var file = Array(track, "Media").SelectMany(m => Array(m, "Part")).Select(p => Text(p, "file")).FirstOrDefault(f => f is not null);
        // Plex's MusicBrainz agent gives a song the ID of its recording.
        var mbid = Array(track, "Guid").Select(g => Text(g, "id")).FirstOrDefault(id => id?.StartsWith("mbid://", StringComparison.Ordinal) == true)?["mbid://".Length..];
        return new TrackMatching.Candidate(
            Text(track, "ratingKey")!,
            Text(track, "title") ?? "",
            // A song by someone other than its album's artist names them as its original title.
            Text(track, "originalTitle") ?? Text(track, "grandparentTitle"),
            Text(track, "parentTitle"),
            Number(track, "duration") is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            file,
            mbid,
            []);
    }

    /// <summary>The library-relative folder a file is in, or the folder itself.</summary>
    private static string FolderOf(string path)
    {
        var normal = path.Replace('\\', '/').Trim('/');
        return Path.HasExtension(normal) ? Path.GetDirectoryName(normal)?.Replace('\\', '/') ?? "" : normal;
    }

    private sealed record Section(string Key, string Title, string Root, bool Refreshing, DateTimeOffset? ScannedAt);

    /// <summary>The music library Melodex files into: the one named in the settings, or the first.</summary>
    private async Task<Section> SectionAsync(CancellationToken ct)
    {
        var container = await GetAsync("library/sections", ct);
        var music = Array(container, "Directory").Where(d => Text(d, "type") == "artist").ToList();
        var chosen = _library is null
            ? music.FirstOrDefault()
            : music.FirstOrDefault(d => string.Equals(Text(d, "title"), _library, StringComparison.OrdinalIgnoreCase));
        if (chosen.ValueKind == JsonValueKind.Undefined)
            throw new PlexException(_library is null
                ? "Plex has no music library. Add one in Plex that holds the music folder Melodex files into."
                : $"Plex has no music library called \"{_library}\". It has: {(music.Count == 0 ? "none" : string.Join(", ", music.Select(d => Text(d, "title"))))}.");
        var root = _musicFolder ?? Array(chosen, "Location").Select(l => Text(l, "path")).FirstOrDefault(p => p is not null)
            ?? throw new PlexException($"Plex's library \"{Text(chosen, "title")}\" has no folder. Set the music folder on the Plex server in this plugin's settings.");
        DateTimeOffset? scanned = Number(chosen, "scannedAt") is { } at and > 0 ? DateTimeOffset.FromUnixTimeSeconds(at) : null;
        return new Section(Text(chosen, "key")!, Text(chosen, "title") ?? "", root, Flag(chosen, "refreshing"), scanned);
    }

    /// <summary>Calls the API and returns its <c>MediaContainer</c>.</summary>
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, path));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-Plex-Token", _token);
        request.Headers.Add("X-Plex-Product", "Melodex");
        request.Headers.Add("X-Plex-Client-Identifier", "melodex");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new PlexException("Plex refused the token. Check it's the token of the server's owner or an admin.");
        if (!response.IsSuccessStatusCode)
            throw new PlexException($"Plex answered {(int)response.StatusCode} {response.ReasonPhrase} to {path.Split('?')[0]}.");
        // A scan's answer is empty.
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return default;
        var body = JsonDocument.Parse(text).RootElement.Clone();
        return body.TryGetProperty("MediaContainer", out var container) ? container : body;
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    // Plex's JSON gives some numbers as strings and some strings as numbers.
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Number(JsonElement element, string name) =>
        Text(element, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True || Text(element, name) is "1" or "true");
}

/// <summary>Plex said no, or has nothing Melodex can use.</summary>
public sealed class PlexException(string message) : Exception(message);
