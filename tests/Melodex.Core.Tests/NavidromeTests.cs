using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Plugins.Navidrome;

namespace Melodex.Core.Tests;

public sealed class NavidromeTests
{
    private static readonly string Songs = """
        [
          { "id": "a", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "duration": 306, "path": "Portishead/Dummy/01-11 - Glory Box.flac",
            "musicBrainzId": "rec-glory", "isrc": ["GBAQT9400066"] },
          { "id": "b", "title": "Glory Box", "artist": "Portishead", "album": "Roseland NYC Live", "duration": 375, "musicBrainzId": "rec-live" },
          { "id": "c", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "duration": 305 }
        ]
        """;

    [Fact]
    public async Task A_track_is_found_by_its_musicbrainz_recording_first()
    {
        var (connector, _) = Create();
        Assert.Equal("b", await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", MusicBrainzRecordingId: "rec-live"), default));
        Assert.Equal("a", await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", Isrc: "GB-AQT-94-00066"), default));
    }

    [Fact]
    public async Task A_title_with_brackets_is_looked_for_without_them_too()
    {
        var (connector, server) = Create();
        Assert.Equal("a", await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box (album version)", MusicBrainzRecordingId: "rec-glory"), default));
        Assert.Equal(["Glory Box (album version)", "Glory Box"], server.Requests.Select(r => HttpUtility.ParseQueryString(r.Query)["query"]));
    }

    [Fact]
    public async Task Without_ids_the_tags_and_length_must_all_agree()
    {
        var (connector, _) = Create();
        Assert.Equal("c", await connector.ResolveTrackAsync(new TrackLookup(Title: "glory box", Artist: "Portishead", Album: "Dummy",
            Duration: TimeSpan.FromSeconds(304), MusicBrainzRecordingId: "rec-unknown"), default));
        Assert.Null(await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", Artist: "Portishead", Album: "Dummy",
            Duration: TimeSpan.FromSeconds(250)), default));
    }

    [Fact]
    public async Task Calls_use_token_auth_and_never_send_the_password()
    {
        var (connector, server) = Create();
        await connector.TriggerScanAsync(["Portishead/Dummy/11 Glory Box.flac"], default);
        var query = HttpUtility.ParseQueryString(server.Requests.Single().Query);
        Assert.Equal("/rest/startScan", server.Requests.Single().AbsolutePath);
        Assert.Equal("admin", query["u"]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("secret" + query["s"]))), query["t"]);
        Assert.DoesNotContain("secret", server.Requests.Single().ToString());
    }

    [Fact]
    public async Task Scan_status_and_health_are_read_from_the_server()
    {
        var (connector, server) = Create();
        var status = await connector.GetScanStatusAsync(default);
        Assert.True(status.Scanning);
        Assert.Equal(1234, status.TrackCount);
        var health = await connector.CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Contains("navidrome 0.64.2", health.Message);

        server.WrongPassword = true;
        health = await connector.CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Equal("Wrong username or password.", health.Message);
    }

    [Fact]
    public async Task The_whole_library_is_read_page_by_page()
    {
        var (connector, _) = Create();
        var tracks = new List<ServerTrack>();
        await foreach (var track in connector.GetTracksAsync(default))
            tracks.Add(track);
        Assert.Equal(["a", "b", "c"], tracks.Select(t => t.Id));
        Assert.Equal("rec-glory", tracks[0].MusicBrainzRecordingId);
    }

    [Fact]
    public async Task Playlists_are_handled_as_their_owner_and_only_theirs_are_listed()
    {
        var (connector, server) = Create();
        var login = new ServerLogin("someone", "their-secret");

        var playlists = await connector.GetPlaylistsAsync(login, default);
        var mine = Assert.Single(playlists);
        Assert.Equal(("pl-1", "Moods/Calm", "Quiet ones", 2), (mine.Id, mine.Name, mine.Comment, mine.SongCount));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), mine.Changed);
        var query = HttpUtility.ParseQueryString(server.Requests.Single().Query);
        Assert.Equal("someone", query["u"]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("their-secret" + query["s"]))), query["t"]);

        var songs = await connector.GetPlaylistAsync(login, "pl-1", default);
        Assert.Equal(["a", "c"], songs!.Songs.Select(s => s.Id));
        Assert.Null(await connector.GetPlaylistAsync(login, "gone", default));

        // Replacing the songs goes through createPlaylist with the ID; the name and comment through updatePlaylist.
        server.Requests.Clear();
        await connector.UpdatePlaylistAsync(login, "pl-1", new PlaylistContent("Moods/Quiet", null, ["c", "a"]), default);
        Assert.Equal(["/rest/createPlaylist", "/rest/updatePlaylist"], server.Requests.Select(r => r.AbsolutePath));
        var replace = HttpUtility.ParseQueryString(server.Requests[0].Query);
        Assert.Equal("pl-1", replace["playlistId"]);
        Assert.Equal(["c", "a"], replace.GetValues("songId")!);
        Assert.Equal("Moods/Quiet", HttpUtility.ParseQueryString(server.Requests[1].Query)["name"]);
    }

    private (NavidromeConnector, FakeSubsonic) Create()
    {
        var server = new FakeSubsonic(Songs);
        var settings = JsonDocument.Parse("""{ "url": "http://navidrome:4533/", "username": "admin", "password": "secret" }""").RootElement;
        var connector = new NavidromeConnector(new PluginContext
        {
            Settings = settings,
            HttpClient = new HttpClient(server),
            LoggerFactory = NullLoggerFactory.Instance,
            DataDirectory = Path.GetTempPath(),
        });
        return (connector, server);
    }

    private sealed class FakeSubsonic(string songs) : HttpMessageHandler
    {
        private static string SongsById(string songs, params string[] ids) => string.Join(", ",
            JsonDocument.Parse(songs).RootElement.EnumerateArray().Where(s => ids.Contains(s.GetProperty("id").GetString())).Select(s => s.GetRawText()));

        public List<Uri> Requests { get; } = [];
        public bool WrongPassword { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var body = WrongPassword
                ? """ "status": "failed", "error": { "code": 40, "message": "Wrong username or password" } """
                : request.RequestUri!.AbsolutePath == "/rest/getPlaylist" && query["id"] != "pl-1"
                ? """ "status": "failed", "error": { "code": 70, "message": "Playlist not found" } """
                : """ "status": "ok", """ + (request.RequestUri.AbsolutePath switch
                {
                    "/rest/getScanStatus" => """ "scanStatus": { "scanning": true, "count": 1234, "lastScan": "2026-10-25T10:00:00Z" } """,
                    "/rest/search3" => query["songOffset"] == "0" && query["query"]?.Contains('(') != true ? $$""" "searchResult3": { "song": {{songs}} } """ : """ "searchResult3": {} """,
                    "/rest/getPlaylists" => """
                        "playlists": { "playlist": [
                          { "id": "pl-1", "name": "Moods/Calm", "comment": "Quiet ones", "owner": "someone", "songCount": 2, "changed": "2026-10-30T12:00:00Z" },
                          { "id": "pl-2", "name": "Theirs", "owner": "admin", "public": true, "songCount": 9 }
                        ] }
                        """,
                    "/rest/getPlaylist" when query["id"] == "pl-1" => $$""" "playlist": { "id": "pl-1", "name": "Moods/Calm", "owner": "someone", "songCount": 2, "entry": [ {{SongsById(songs, "a", "c")}} ] } """,
                    "/rest/getPlaylist" => """ "x": 0 """,
                    _ => """ "x": 0 """,
                });
            var json = $$"""{ "subsonic-response": { "version": "1.16.1", "type": "navidrome", "serverVersion": "0.64.2", "openSubsonic": true, {{body}} } }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
