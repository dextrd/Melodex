using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Core.Data;
using Melodex.Core.Plugins;
using Melodex.Plugins.Jellyfin;
using Melodex.Plugins.Plex;

namespace Melodex.Core.Tests;

public sealed class JellyfinTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("melodex-jellyfin-").FullName;

    public void Dispose() => Directory.Delete(_data, recursive: true);

    // As Jellyfin 12.4 answers, cut down to what Melodex reads.
    private const string Folders = """
        [ { "Name": "Films", "CollectionType": "movies", "ItemId": "films", "Locations": ["/films"], "RefreshStatus": "Idle" },
          { "Name": "Music", "CollectionType": "music", "ItemId": "lib1", "Locations": ["/music"], "RefreshStatus": "Idle" } ]
        """;

    private const string Songs = """
        { "Items": [
          { "Id": "s1", "Name": "Glory Box", "Path": "/music/Portishead/Dummy/11 Glory Box.flac", "Album": "Dummy", "AlbumArtist": "Portishead",
            "Artists": ["Portishead"], "RunTimeTicks": 3055730160, "ProviderIds": { "MusicBrainzRecording": "rec-glory", "MusicBrainzTrack": "trk-glory" } },
          { "Id": "s2", "Name": "Glory Box", "Path": "/music/Portishead/Roseland NYC Live/08 Glory Box.m4a", "Album": "Roseland NYC Live",
            "AlbumArtist": "Portishead", "Artists": ["Portishead"], "RunTimeTicks": 3372000000, "ProviderIds": { "MusicBrainzRecording": "rec-live" } }
        ], "TotalRecordCount": 2 }
        """;

    [Fact]
    public async Task A_song_is_found_by_its_recording_its_path_or_its_tags()
    {
        var (jellyfin, server) = Create();
        Assert.Equal("s2", await jellyfin.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", MusicBrainzRecordingId: "rec-live"), default));
        Assert.Equal("s1", await jellyfin.ResolveTrackAsync(new TrackLookup(Path: "Portishead/Dummy/11 Glory Box.flac", Title: "Glory Box"), default));
        Assert.Equal("s1", await jellyfin.ResolveTrackAsync(new TrackLookup(Title: "Glory Box (album version)", Artist: "Portishead", Album: "Dummy",
            Duration: TimeSpan.FromSeconds(306)), default));
        // Only the music library is searched, with the API key in the Authorization header.
        var search = server.Requests.First(r => r.Uri.AbsolutePath == "/Items");
        Assert.Equal("lib1", HttpUtility.ParseQueryString(search.Uri.Query)["ParentId"]);
        Assert.Contains("Token=\"key\"", search.Authorization);
    }

    [Fact]
    public async Task Jellyfin_is_told_which_folders_changed_or_scans_the_library()
    {
        var (jellyfin, server) = Create();
        await jellyfin.TriggerScanAsync(["Portishead/Dummy/11 Glory Box.flac", "Portishead/Dummy/01 Mysterons.flac", "Massive Attack/Mezzanine"], default);
        var updated = server.Requests.Single(r => r.Uri.AbsolutePath == "/Library/Media/Updated");
        Assert.Equal(["/music/Portishead/Dummy", "/music/Massive Attack/Mezzanine"],
            JsonNode.Parse(updated.Body!)!["Updates"]!.AsArray().Select(u => (string?)u!["Path"]));

        await jellyfin.TriggerScanAsync(Enumerable.Range(1, 25).Select(i => $"Artist {i}/Album/01 Song.flac").ToList(), default);
        Assert.Equal("/Items/lib1/Refresh", server.Requests.Last().Uri.AbsolutePath);

        var status = await jellyfin.GetScanStatusAsync(default);
        Assert.Equal(new ScanStatus(true, DateTimeOffset.Parse("2026-11-11T21:06:03Z"), 2), status);
    }

    [Fact]
    public async Task Health_says_what_is_wrong()
    {
        var (jellyfin, _) = Create();
        Assert.Equal(new HealthReport(HealthStatus.Healthy, "Connected to Jellyfin 12.4.0, library \"Music\" at /music"), await jellyfin.CheckHealthAsync(default));
        (jellyfin, _) = Create("""{ "url": "http://jellyfin:8096", "apiKey": "wrong" }""");
        Assert.Contains("refused the API key", (await jellyfin.CheckHealthAsync(default)).Message);
        (jellyfin, _) = Create("""{ "url": "http://jellyfin:8096", "apiKey": "key", "library": "Audiobooks" }""");
        Assert.Equal("Jellyfin has no music library called \"Audiobooks\". It has: Music.", (await jellyfin.CheckHealthAsync(default)).Message);
    }

    [Fact]
    public async Task Playlists_sync_as_the_user_and_count_as_changed_only_when_their_etag_does()
    {
        var (jellyfin, server) = Create("""{ "url": "http://jellyfin:8096", "apiKey": "key", "syncPlaylists": true }""");
        var login = new ServerLogin("ann", "secret");
        Assert.True(jellyfin.SyncsPlaylists);
        Assert.Equal(HealthStatus.Unhealthy, (await jellyfin.CheckLoginAsync(login with { Password = "wrong" }, default)).Status);

        var id = await jellyfin.CreatePlaylistAsync(login, new PlaylistContent("Rainy day", "Songs for it", ["s1", "s2"]), default);
        // Made as Ann, its description set with the API key, as only an admin can.
        Assert.Contains("Token=\"ann-token\"", server.Requests.Single(r => r.Uri.AbsolutePath == "/Playlists" && r.Method == "POST").Authorization);
        Assert.Equal("Songs for it", server.Overviews[id]);
        var first = (await jellyfin.GetPlaylistAsync(login, id, default))!;
        Assert.Equal(("Rainy day", "Songs for it", 2), (first.Playlist.Name, first.Playlist.Comment, first.Songs.Count));
        Assert.Equal("rec-glory", first.Songs[0].MusicBrainzRecordingId);

        // Unchanged, it keeps its time, even to another instance of the plugin, as after a restart.
        await Task.Delay(1100);
        Assert.Equal(first.Playlist.Changed, (await jellyfin.GetPlaylistAsync(login, id, default))!.Playlist.Changed);
        var (again, _) = Create("""{ "url": "http://jellyfin:8096", "apiKey": "key", "syncPlaylists": true }""", server);
        Assert.Equal(first.Playlist.Changed, (await again.GetPlaylistAsync(login, id, default))!.Playlist.Changed);

        // Changed in Jellyfin, it's newer.
        server.Etags[id] = "changed-in-jellyfin";
        Assert.True((await jellyfin.GetPlaylistAsync(login, id, default))!.Playlist.Changed > first.Playlist.Changed);

        await jellyfin.DeletePlaylistAsync(login, id, default);
        Assert.Null(await jellyfin.GetPlaylistAsync(login, id, default));
    }

    [Fact]
    public async Task Songs_are_found_again_on_a_media_server_that_takes_over()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var options = Options.Create(new MelodexOptions { PluginsPath = Path.Combine(_data, "plugins"), DataPath = _data });
        var host = new PluginHost(new PluginCatalog(options, NullLogger<PluginCatalog>.Instance), options, Options.Create(new PluginHttpOptions()), NullLoggerFactory.Instance);
        var connectors = new ConnectorPlugins(db, host);
        var plex = new PluginInstance { PluginId = PlexConnectorFactory.PluginId, Type = PluginType.Connector, Name = "Plex", Priority = 0, ContractVersion = "1.0",
            SettingsJson = """{ "url": "http://plex:32400", "token": "t" }""" };
        var jellyfin = new PluginInstance { PluginId = JellyfinConnectorFactory.PluginId, Type = PluginType.Connector, Name = "Jellyfin", Priority = 1, ContractVersion = "1.0",
            SettingsJson = """{ "url": "http://jellyfin:8096", "apiKey": "key" }""" };
        db.PluginInstances.AddRange(plex, jellyfin);
        db.LibraryFiles.Add(new LibraryFile { Path = "Portishead/Dummy/11 Glory Box.flac", MediaServerTrackId = "plex-1" });
        await db.SaveChangesAsync();

        // An install from before servers were noted keeps its IDs when it has only the one server: they're its.
        db.PluginInstances.Remove(jellyfin);
        await db.SaveChangesAsync();
        Assert.Equal(plex.Id, (await connectors.GetActiveAsync(default))!.Value.Instance.Id);
        Assert.Equal("plex-1", await db.LibraryFiles.Select(f => f.MediaServerTrackId).SingleAsync());

        // Another server taking over forgets them, to be found on it.
        jellyfin = new PluginInstance { PluginId = JellyfinConnectorFactory.PluginId, Type = PluginType.Connector, Name = "Jellyfin", Priority = 1, ContractVersion = "1.0",
            SettingsJson = jellyfin.SettingsJson };
        db.PluginInstances.Add(jellyfin);
        plex.Priority = 2;
        await db.SaveChangesAsync();
        Assert.Equal(jellyfin.Id, (await connectors.GetActiveAsync(default))!.Value.Instance.Id);
        db.ChangeTracker.Clear();
        Assert.Null(await db.LibraryFiles.Select(f => f.MediaServerTrackId).SingleAsync());
    }

    private (JellyfinConnector, FakeJellyfin) Create(string settings = """{ "url": "http://jellyfin:8096/", "apiKey": "key" }""", FakeJellyfin? server = null)
    {
        server ??= new FakeJellyfin();
        var connector = new JellyfinConnector(new PluginContext
        {
            Settings = JsonDocument.Parse(settings).RootElement,
            HttpClient = new HttpClient(server),
            LoggerFactory = NullLoggerFactory.Instance,
            DataDirectory = _data,
        });
        return (connector, server);
    }

    private sealed record Call(string Method, Uri Uri, string Authorization, string? Body);

    private sealed class FakeJellyfin : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Requests { get; } = new();
        public ConcurrentDictionary<string, string> Etags { get; } = new();
        public ConcurrentDictionary<string, string> Overviews { get; } = new();
        private readonly ConcurrentDictionary<string, (string Name, List<string> Ids)> _playlists = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var authorization = string.Join(",", request.Headers.GetValues("Authorization"));
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new Call(request.Method.Method, uri, authorization, body));
            var path = uri.AbsolutePath;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (path == "/System/Info/Public")
                return Json("""{ "ServerName": "jf", "Version": "12.4.0", "ProductName": "Jellyfin Server", "Id": "abc" }""");
            if (path == "/Users/AuthenticateByName")
            {
                var login = JsonNode.Parse(body!)!;
                return (string?)login["Pw"] == "secret"
                    ? Json($$"""{ "AccessToken": "{{login["Username"]}}-token", "User": { "Id": "u1" } }""")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            var token = authorization.Contains("Token=\"key\"") ? "key" : authorization.Contains("Token=\"ann-token\"") ? "ann" : null;
            if (token is null)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            switch (request.Method.Method, path)
            {
                case ("GET", "/System/Info"):
                    return Json("{}");
                case ("GET", "/Library/VirtualFolders"):
                    return Json(Folders);
                case ("POST", "/Library/Media/Updated") or ("POST", "/Items/lib1/Refresh"):
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ("GET", "/ScheduledTasks"):
                    return Json("""[ { "Key": "RefreshLibrary", "State": "Running", "LastExecutionResult": { "EndTimeUtc": "2026-11-11T21:06:03Z" } } ]""");
                case ("GET", "/Items") when query["Limit"] == "0":
                    return Json("""{ "Items": [], "TotalRecordCount": 2 }""");
                case ("GET", "/Items"):
                    return Json(query["SearchTerm"] == "Glory Box" ? Songs : """{ "Items": [], "TotalRecordCount": 0 }""");
                case ("POST", "/Playlists"):
                {
                    var made = JsonNode.Parse(body!)!;
                    _playlists["p1"] = ((string)made["Name"]!, made["Ids"]!.AsArray().Select(i => (string)i!).ToList());
                    Etags["p1"] = "made";
                    return Json("""{ "Id": "p1" }""");
                }
                case ("GET", "/Items/p1"):
                    return Json("""{ "Id": "p1", "Name": "Rainy day", "Type": "Playlist" }""");
                case ("POST", "/Items/p1") when token == "key":
                    Overviews["p1"] = (string)JsonNode.Parse(body!)!["Overview"]!;
                    Etags["p1"] = "described";
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ("GET", "/Users/u1/Items"):
                    return Json(JsonSerializer.Serialize(new
                    {
                        Items = _playlists.Select(p => new { Id = p.Key, p.Value.Name, MediaType = "Audio", Etag = Etags[p.Key], ChildCount = p.Value.Ids.Count, Overview = Overviews.GetValueOrDefault(p.Key) }),
                    }));
                case ("GET", "/Playlists/p1/Items"):
                {
                    var all = JsonNode.Parse(Songs)!["Items"]!.AsArray();
                    return Json(new JsonObject { ["Items"] = new JsonArray(_playlists["p1"].Ids.Select(id => all.First(s => (string?)s!["Id"] == id)!.DeepClone()).ToArray()) }.ToJsonString());
                }
                case ("DELETE", "/Items/p1"):
                    _playlists.TryRemove("p1", out _);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
