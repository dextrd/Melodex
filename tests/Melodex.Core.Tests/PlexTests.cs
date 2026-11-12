using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Plugins.Plex;

namespace Melodex.Core.Tests;

public sealed class PlexTests
{
    // As Plex answers with Accept: application/json, numbers and flags as it gives them.
    private const string Sections = """
        { "MediaContainer": { "size": 2, "Directory": [
          { "key": "1", "type": "movie", "title": "Films", "Location": [{ "id": 1, "path": "/films" }] },
          { "key": "3", "type": "artist", "title": "Music", "refreshing": false, "scannedAt": 1762500000,
            "Location": [{ "id": 3, "path": "/data/music" }] }
        ] } }
        """;

    private const string Songs = """
        { "MediaContainer": { "size": 3, "Metadata": [
          { "ratingKey": "101", "type": "track", "title": "Glory Box", "grandparentTitle": "Portishead", "parentTitle": "Dummy", "duration": 306000,
            "Guid": [{ "id": "mbid://rec-glory" }], "Media": [{ "Part": [{ "file": "/data/music/Portishead/Dummy/11 Glory Box.flac" }] }] },
          { "ratingKey": "102", "type": "track", "title": "Glory Box", "grandparentTitle": "Portishead", "parentTitle": "Roseland NYC Live", "duration": 375000,
            "Media": [{ "Part": [{ "file": "/data/music/Portishead/Roseland NYC Live/09 Glory Box.flac" }] }] },
          { "ratingKey": "103", "type": "track", "title": "Glory Box", "grandparentTitle": "Various Artists", "originalTitle": "Portishead",
            "parentTitle": "Trip Hop Classics", "duration": 305000, "Media": [{ "Part": [{ "file": "/data/music/Compilations/Trip Hop Classics/03 Glory Box.mp3" }] }] }
        ] } }
        """;

    [Fact]
    public async Task A_song_is_found_by_its_path_then_its_musicbrainz_id_then_its_tags()
    {
        var (plex, server) = Create();
        Assert.Equal("102", await plex.ResolveTrackAsync(new TrackLookup(Path: "Portishead/Roseland NYC Live/09 Glory Box.flac", Title: "Glory Box"), default));
        Assert.Equal("101", await plex.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", MusicBrainzRecordingId: "rec-glory"), default));
        // A song on a compilation is by its own artist, not the album's.
        Assert.Equal("103", await plex.ResolveTrackAsync(new TrackLookup(Title: "glory box", Artist: "Portishead", Album: "Trip Hop Classics"), default));
        Assert.Null(await plex.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", Artist: "Portishead", Album: "Dummy", Duration: TimeSpan.FromSeconds(250)), default));

        var search = server.Requests.First(r => r.AbsolutePath == "/library/sections/3/all");
        Assert.Equal("Glory Box", HttpUtility.ParseQueryString(search.Query)["title"]);
        Assert.Equal("10", HttpUtility.ParseQueryString(search.Query)["type"]);
    }

    [Fact]
    public async Task A_scan_is_of_the_folders_the_files_are_in_on_the_plex_server()
    {
        var (plex, server) = Create();
        await plex.TriggerScanAsync(["Portishead/Dummy/11 Glory Box.flac", "Portishead/Dummy/01 Mysterons.flac", "Massive Attack/Mezzanine"], default);
        var scans = server.Requests.Where(r => r.AbsolutePath == "/library/sections/3/refresh").Select(r => HttpUtility.ParseQueryString(r.Query)["path"]).ToList();
        Assert.Equal(["/data/music/Portishead/Dummy", "/data/music/Massive Attack/Mezzanine"], scans);

        // Where Plex sees the music folder can be set, when it isn't the library's first folder.
        (plex, server) = Create("""{ "url": "http://plex:32400", "token": "tok", "musicFolder": "/mnt/melodex/" }""");
        await plex.TriggerScanAsync(["Portishead/Dummy/11 Glory Box.flac"], default);
        Assert.Equal("/mnt/melodex/Portishead/Dummy", HttpUtility.ParseQueryString(server.Requests.Last().Query)["path"]);

        // Many folders at once, and the whole library is scanned.
        await plex.TriggerScanAsync(Enumerable.Range(1, 25).Select(i => $"Artist {i}/Album/01 Song.flac").ToList(), default);
        Assert.Null(HttpUtility.ParseQueryString(server.Requests.Last().Query)["path"]);
    }

    [Fact]
    public async Task Status_identity_and_health_come_from_the_chosen_music_library()
    {
        var (plex, server) = Create();
        var status = await plex.GetScanStatusAsync(default);
        Assert.Equal(new ScanStatus(false, DateTimeOffset.FromUnixTimeSeconds(1762500000), 2841), status);
        Assert.Equal(new ServerIdentity("plex", "1.42.1.10060-4e8b05daf"), await plex.GetServerAsync(default));
        var health = await plex.CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Contains("library \"Music\" at /data/music", health.Message);
        // Every call carries the token, never in the address.
        Assert.All(server.Tokens, token => Assert.Equal("tok", token));
        Assert.DoesNotContain(server.Requests, r => r.Query.Contains("tok"));

        (plex, _) = Create("""{ "url": "http://plex:32400", "token": "tok", "library": "Audiobooks" }""");
        var missing = await plex.CheckHealthAsync(default);
        Assert.Equal((HealthStatus.Unhealthy, "Plex has no music library called \"Audiobooks\". It has: Music."), (missing.Status, missing.Message));

        (plex, _) = Create("""{ "url": "http://plex:32400", "token": "wrong" }""");
        Assert.Contains("refused the token", (await plex.CheckHealthAsync(default)).Message);
    }

    private static (PlexConnector, FakePlex) Create(string settings = """{ "url": "http://plex:32400/", "token": "tok" }""")
    {
        var server = new FakePlex();
        var connector = new PlexConnector(new PluginContext
        {
            Settings = JsonDocument.Parse(settings).RootElement,
            HttpClient = new HttpClient(server),
            LoggerFactory = NullLoggerFactory.Instance,
            DataDirectory = Path.GetTempPath(),
        });
        return (connector, server);
    }

    private sealed class FakePlex : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public ConcurrentQueue<string> Tokens { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var token = request.Headers.GetValues("X-Plex-Token").Single();
            Tokens.Enqueue(token);
            if (token != "tok")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var query = HttpUtility.ParseQueryString(uri.Query);
            return Task.FromResult(uri.AbsolutePath switch
            {
                "/identity" => Json("""{ "MediaContainer": { "size": 0, "claimed": true, "machineIdentifier": "abc", "version": "1.42.1.10060-4e8b05daf" } }"""),
                "/library/sections" => Json(Sections),
                "/library/sections/3/refresh" => new HttpResponseMessage(HttpStatusCode.OK),
                "/library/sections/3/all" when query["X-Plex-Container-Size"] == "0" => Json("""{ "MediaContainer": { "size": 0, "totalSize": 2841 } }"""),
                "/library/sections/3/all" when query["title"] is { } title && "Glory Box".Contains(title, StringComparison.OrdinalIgnoreCase) => Json(Songs),
                "/library/sections/3/all" => Json("""{ "MediaContainer": { "size": 0 } }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
