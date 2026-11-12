using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Jellyfin;

/// <summary>
/// Jellyfin, over its HTTP API with an API key. Jellyfin can be told which folders changed, and lists each song's file
/// and MusicBrainz recording ID, so songs are found by those first, then by their tags. Users' playlists sync with
/// each user's own Jellyfin login.
/// </summary>
public sealed class JellyfinConnector : IMediaServerConnector, IFullScan, IServerIdentity, IPlaylistSync
{
    // More folders than this at once, and the whole library is scanned instead.
    private const int MostFolders = 20;

    // How often, and how many times, a playlist just written is checked for Jellyfin having finished with it.
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(600);
    private const int SettleChecks = 6;

    private static readonly string Version =
        typeof(JellyfinConnector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    // Jellyfin's own names, as its API documents them.
    private static readonly JsonSerializerOptions Pascal = new();

    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly string _apiKey;
    private readonly string? _library;
    private readonly string? _musicFolder;
    private readonly PlaylistClock _clock;

    // Each user's session, by their login, so they aren't signed in again for every call.
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private sealed record Session(string Token, string UserId);

    public JellyfinConnector(PluginContext context)
    {
        _http = context.HttpClient;
        var settings = context.Settings;
        _server = new Uri(settings.GetProperty("url").GetString()!.TrimEnd('/') + "/");
        _apiKey = settings.TryGetProperty("apiKey", out var k) ? k.GetString() ?? "" : "";
        _library = settings.TryGetProperty("library", out var l) && l.GetString() is { Length: > 0 } library ? library.Trim() : null;
        _musicFolder = settings.TryGetProperty("musicFolder", out var f) && f.GetString() is { Length: > 0 } folder ? folder.Trim() : null;
        SyncsPlaylists = settings.TryGetProperty("syncPlaylists", out var sync) && sync.ValueKind == JsonValueKind.True;
        _clock = new PlaylistClock(Path.Combine(context.DataDirectory, "playlists.json"));
    }

    public bool SyncsPlaylists { get; }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var server = await GetServerAsync(cancellationToken);
            // Checks the API key too: the public info doesn't need one.
            await SendAsync(HttpMethod.Get, "System/Info", null, _apiKey, cancellationToken);
            var library = await LibraryAsync(cancellationToken);
            return new HealthReport(HealthStatus.Healthy, $"Connected to Jellyfin {server?.Version}, library \"{library.Name}\" at {library.Root}");
        }
        catch (Exception e) when (e is JellyfinException or HttpRequestException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, e switch
            {
                HttpRequestException => $"Can't reach {_server}: {e.Message}",
                TaskCanceledException => $"{_server} didn't answer in time.",
                _ => e.Message,
            });
        }
    }

    public async Task<ServerIdentity?> GetServerAsync(CancellationToken cancellationToken)
    {
        var info = await SendAsync(HttpMethod.Get, "System/Info/Public", null, null, cancellationToken);
        return new ServerIdentity("jellyfin", Text(info, "Version"));
    }

    // ---- Scans ----

    /// <summary>Tells Jellyfin the folders the files are in changed, or scans the whole library when there are many.</summary>
    public async Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var library = await LibraryAsync(cancellationToken);
        var folders = paths.Select(FolderOf).Distinct(StringComparer.Ordinal).ToList();
        if (folders.Count is 0 or > MostFolders || folders.Contains(""))
        {
            await RefreshAsync(library, cancellationToken);
            return;
        }
        var updates = folders.Select(folder => new { Path = library.Root.TrimEnd('/') + "/" + folder, UpdateType = "Modified" }).ToList();
        await SendAsync(HttpMethod.Post, "Library/Media/Updated", new { Updates = updates }, _apiKey, cancellationToken);
    }

    public async Task TriggerFullScanAsync(CancellationToken cancellationToken) =>
        await RefreshAsync(await LibraryAsync(cancellationToken), cancellationToken);

    private Task RefreshAsync(Library library, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"Items/{library.Id}/Refresh?Recursive=true&MetadataRefreshMode=Default&ImageRefreshMode=Default", null, _apiKey, ct);

    public async Task<ScanStatus> GetScanStatusAsync(CancellationToken cancellationToken)
    {
        var library = await LibraryAsync(cancellationToken);
        var tasks = await SendAsync(HttpMethod.Get, "ScheduledTasks?isHidden=false", null, _apiKey, cancellationToken);
        var scan = Items(tasks).FirstOrDefault(t => Text(t, "Key") == "RefreshLibrary");
        DateTimeOffset? last = scan.ValueKind == JsonValueKind.Object && scan.TryGetProperty("LastExecutionResult", out var result)
                               && result.ValueKind == JsonValueKind.Object && Text(result, "EndTimeUtc") is { } end
                               && DateTimeOffset.TryParse(end, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : null;
        var songs = await SendAsync(HttpMethod.Get, $"Items?ParentId={library.Id}&Recursive=true&IncludeItemTypes=Audio&Limit=0", null, _apiKey, cancellationToken);
        long? count = songs.TryGetProperty("TotalRecordCount", out var total) && total.TryGetInt64(out var n) ? n : null;
        return new ScanStatus(library.Refreshing || Text(scan, "State") == "Running", last, count);
    }

    // ---- Finding songs ----

    public async Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lookup.Title))
            return null;
        var library = await LibraryAsync(cancellationToken);
        if (TrackMatching.Pick(await SongsAsync(library, lookup.Title, cancellationToken), lookup) is { } found)
            return found.Id;
        // Jellyfin searches the words in its own titles, so "Digital Love (album version)" doesn't find "Digital Love".
        if (TrackMatching.WithoutBrackets(lookup.Title) is { } bare)
            return TrackMatching.Pick(await SongsAsync(library, bare, cancellationToken), lookup with { Title = bare })?.Id;
        return null;
    }

    private async Task<IReadOnlyList<TrackMatching.Candidate>> SongsAsync(Library library, string title, CancellationToken ct)
    {
        var found = await SendAsync(HttpMethod.Get,
            $"Items?ParentId={library.Id}&Recursive=true&IncludeItemTypes=Audio&Fields=Path,ProviderIds&Limit=50&SearchTerm={Uri.EscapeDataString(title)}",
            null, _apiKey, ct);
        return Items(found).Select(Song).ToList();
    }

    private static TrackMatching.Candidate Song(JsonElement item) => new(
        Text(item, "Id")!,
        Text(item, "Name") ?? "",
        item.TryGetProperty("Artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0
            ? artists[0].GetString()
            : Text(item, "AlbumArtist"),
        Text(item, "Album"),
        item.TryGetProperty("RunTimeTicks", out var ticks) && ticks.TryGetInt64(out var t) ? TimeSpan.FromTicks(t) : null,
        Text(item, "Path"),
        item.TryGetProperty("ProviderIds", out var ids) ? Text(ids, "MusicBrainzRecording") : null,
        []);

    private static ServerTrack Track(JsonElement item)
    {
        var song = Song(item);
        return new ServerTrack(song.Id, song.Path, song.Title, song.Artist, song.Album, song.Duration, song.MusicBrainzRecordingId);
    }

    // ---- Each user's playlists ----

    public async Task<HealthReport> CheckLoginAsync(ServerLogin login, CancellationToken cancellationToken)
    {
        try
        {
            _sessions.TryRemove(Key(login), out _);
            await SessionAsync(login, cancellationToken);
            return new HealthReport(HealthStatus.Healthy, $"Signed in to Jellyfin as {login.Username}");
        }
        catch (JellyfinException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
    }

    public async Task<IReadOnlyList<UserPlaylist>> GetPlaylistsAsync(ServerLogin login, CancellationToken cancellationToken)
    {
        var found = await AsUserAsync(login, session =>
            $"Users/{session.UserId}/Items?IncludeItemTypes=Playlist&Recursive=true&Fields=Overview,Etag,ChildCount", HttpMethod.Get, null, cancellationToken);
        return Items(found).Where(p => Text(p, "MediaType") is null or "Audio").Select(Playlist).ToList();
    }

    public async Task<UserPlaylistSongs?> GetPlaylistAsync(ServerLogin login, string playlistId, CancellationToken cancellationToken)
    {
        var playlist = (await GetPlaylistsAsync(login, cancellationToken)).FirstOrDefault(p => p.Id == playlistId);
        if (playlist is null)
            return null;
        var songs = await AsUserAsync(login, session =>
            $"Playlists/{playlistId}/Items?UserId={session.UserId}&Fields=Path,ProviderIds", HttpMethod.Get, null, cancellationToken);
        return new UserPlaylistSongs(playlist, Items(songs).Select(Track).ToList());
    }

    public async Task<string> CreatePlaylistAsync(ServerLogin login, PlaylistContent content, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(login, cancellationToken);
        var created = await AsUserAsync(login, _ => "Playlists", HttpMethod.Post,
            new { content.Name, Ids = content.TrackIds, UserId = session.UserId, MediaType = "Audio" }, cancellationToken);
        var id = Text(created, "Id") ?? throw new JellyfinException("Jellyfin didn't say what it called the new playlist.");
        if (content.Comment is { Length: > 0 })
            await SetCommentAsync(id, session.UserId, content.Comment, cancellationToken);
        await SettleAsync(login, id, cancellationToken);
        return id;
    }

    public async Task UpdatePlaylistAsync(ServerLogin login, string playlistId, PlaylistContent content, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(login, cancellationToken);
        // Jellyfin replaces the songs, in this order, and keeps the playlist's description.
        await AsUserAsync(login, _ => $"Playlists/{playlistId}", HttpMethod.Post, new { content.Name, Ids = content.TrackIds }, cancellationToken);
        var current = (await GetPlaylistsAsync(login, cancellationToken)).FirstOrDefault(p => p.Id == playlistId);
        if (current is not null && (current.Comment ?? "") != (content.Comment ?? ""))
            await SetCommentAsync(playlistId, session.UserId, content.Comment ?? "", cancellationToken);
        await SettleAsync(login, playlistId, cancellationToken);
    }

    /// <summary>
    /// Waits, for a few seconds at most, for Jellyfin to finish with a playlist Melodex just wrote: it changes it once
    /// more within a second or so, which would otherwise look like a change made in Jellyfin at the next sync.
    /// </summary>
    private async Task SettleAsync(ServerLogin login, string playlistId, CancellationToken ct)
    {
        var last = await EtagAsync(login, playlistId, ct);
        for (var i = 0; i < SettleChecks; i++)
        {
            await Task.Delay(SettleInterval, ct);
            var now = await EtagAsync(login, playlistId, ct);
            if (now == last)
                break;
            last = now;
        }
    }

    private async Task<string?> EtagAsync(ServerLogin login, string playlistId, CancellationToken ct)
    {
        var found = await AsUserAsync(login, session =>
            $"Users/{session.UserId}/Items?IncludeItemTypes=Playlist&Recursive=true&Fields=Etag&Ids={playlistId}", HttpMethod.Get, null, ct);
        return Items(found).Select(p => Text(p, "Etag")).FirstOrDefault();
    }

    public async Task DeletePlaylistAsync(ServerLogin login, string playlistId, CancellationToken cancellationToken)
    {
        await AsUserAsync(login, _ => $"Items/{playlistId}", HttpMethod.Delete, null, cancellationToken);
        _clock.Forget(playlistId);
    }

    /// <summary>
    /// A playlist's description can only be changed by editing the playlist as a whole, which takes an admin: the API
    /// key, on the playlist as its owner sees it.
    /// </summary>
    private async Task SetCommentAsync(string playlistId, string userId, string comment, CancellationToken ct)
    {
        var item = (await SendAsync(HttpMethod.Get, $"Items/{playlistId}?userId={userId}", null, _apiKey, ct)).GetRawText();
        var edited = JsonNode.Parse(item)!.AsObject();
        edited["Overview"] = comment;
        await SendAsync(HttpMethod.Post, $"Items/{playlistId}", edited, _apiKey, ct);
    }

    /// <summary>
    /// A playlist as Melodex sees it. Jellyfin dates a playlist's songs being added, but not their removal or its
    /// renaming, so a playlist counts as changed when it's first seen with a new ETag, which every change gives it.
    /// </summary>
    private UserPlaylist Playlist(JsonElement item)
    {
        var id = Text(item, "Id")!;
        return new UserPlaylist(id, Text(item, "Name") ?? "", Text(item, "Overview") is { Length: > 0 } overview ? overview : null,
            item.TryGetProperty("ChildCount", out var count) && count.TryGetInt32(out var n) ? n : 0,
            _clock.Changed(id, Text(item, "Etag")));
    }

    /// <summary>Calls the API as the user, signing them in again once if their session has ended.</summary>
    private async Task<JsonElement> AsUserAsync(ServerLogin login, Func<Session, string> path, HttpMethod method, object? body, CancellationToken ct)
    {
        var session = await SessionAsync(login, ct);
        try
        {
            return await SendAsync(method, path(session), body, session.Token, ct);
        }
        catch (JellyfinException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            _sessions.TryRemove(Key(login), out _);
            session = await SessionAsync(login, ct);
            return await SendAsync(method, path(session), body, session.Token, ct);
        }
    }

    private async Task<Session> SessionAsync(ServerLogin login, CancellationToken ct)
    {
        if (_sessions.TryGetValue(Key(login), out var session))
            return session;
        JsonElement signedIn;
        try
        {
            signedIn = await SendAsync(HttpMethod.Post, "Users/AuthenticateByName", new { login.Username, Pw = login.Password }, null, ct, DeviceOf(login.Username));
        }
        catch (JellyfinException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            throw new JellyfinException($"Jellyfin didn't accept the username and password for {login.Username}.", e.Status);
        }
        session = new Session(Text(signedIn, "AccessToken")!, Text(signedIn.GetProperty("User"), "Id")!);
        _sessions[Key(login)] = session;
        return session;
    }

    private static string Key(ServerLogin login) => $"{login.Username}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.Password)))}";

    // One device per user, so signing in again replaces their Melodex session in Jellyfin rather than adding another.
    private static string DeviceOf(string username) => "melodex-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..16];

    // ---- The library ----

    private sealed record Library(string Id, string Name, string Root, bool Refreshing);

    /// <summary>The music library Melodex files into: the one named in the settings, or the first.</summary>
    private async Task<Library> LibraryAsync(CancellationToken ct)
    {
        var folders = await SendAsync(HttpMethod.Get, "Library/VirtualFolders", null, _apiKey, ct);
        var music = Items(folders).Where(f => Text(f, "CollectionType") == "music").ToList();
        var chosen = _library is null ? music.FirstOrDefault() : music.FirstOrDefault(f => string.Equals(Text(f, "Name"), _library, StringComparison.OrdinalIgnoreCase));
        if (chosen.ValueKind == JsonValueKind.Undefined)
            throw new JellyfinException(_library is null
                ? "Jellyfin has no music library. Add one in Jellyfin that holds the music folder Melodex files into."
                : $"Jellyfin has no music library called \"{_library}\". It has: {(music.Count == 0 ? "none" : string.Join(", ", music.Select(f => Text(f, "Name"))))}.");
        var root = _musicFolder ?? (chosen.TryGetProperty("Locations", out var locations) ? locations.EnumerateArray().Select(l => l.GetString()).FirstOrDefault() : null)
            ?? throw new JellyfinException($"Jellyfin's library \"{Text(chosen, "Name")}\" has no folder. Set the music folder on the Jellyfin server in this plugin's settings.");
        return new Library(Text(chosen, "ItemId")!, Text(chosen, "Name") ?? "", root, Text(chosen, "RefreshStatus") is { } status && status != "Idle");
    }

    /// <summary>The library-relative folder a file is in, or the folder itself.</summary>
    private static string FolderOf(string path)
    {
        var normal = path.Replace('\\', '/').Trim('/');
        return Path.HasExtension(normal) ? Path.GetDirectoryName(normal)?.Replace('\\', '/') ?? "" : normal;
    }

    // ---- Calling Jellyfin ----

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, string? token, CancellationToken ct, string device = "melodex")
    {
        using var request = new HttpRequestMessage(method, new Uri(_server, path));
        var authorization = $"MediaBrowser Client=\"Melodex\", Device=\"Melodex\", DeviceId=\"{device}\", Version=\"{Version}\"";
        request.Headers.TryAddWithoutValidation("Authorization", token is null ? authorization : $"{authorization}, Token=\"{token}\"");
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Pascal);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && token == _apiKey)
            throw new JellyfinException("Jellyfin refused the API key. Make one for Melodex in Jellyfin's Dashboard, under API Keys.", response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new JellyfinException($"Jellyfin answered {(int)response.StatusCode} {response.ReasonPhrase} to {path.Split('?')[0]}.", response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    // A list comes as an array, or as an object with Items.
    private static IEnumerable<JsonElement> Items(JsonElement found) => found.ValueKind switch
    {
        JsonValueKind.Array => found.EnumerateArray(),
        JsonValueKind.Object when found.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array => items.EnumerateArray(),
        _ => [],
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Jellyfin said no, or has nothing Melodex can use.</summary>
public sealed class JellyfinException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
