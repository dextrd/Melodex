using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;
using Melodex.Plugins.Deezer;

namespace Melodex.Core.Tests;

/// <summary>Deezer's answers mapped to the catalogue contract.</summary>
public class DeezerIndexTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("album", AlbumType.Album, false)]
    [InlineData("ep", AlbumType.Ep, false)]
    [InlineData("single", AlbumType.Single, false)]
    [InlineData("compile", AlbumType.Album, true)]
    public void Record_types_become_album_types(string recordType, AlbumType type, bool compilation)
    {
        var album = DeezerIndex.MapAlbum(Json($$$"""{"id": 10, "title": "Notes", "record_type": "{{{recordType}}}", "release_date": "2020-06-01", "artist": {"id": 1, "name": "Alaskan Tapes"}}"""), null);
        Assert.Equal(new ProviderId(Providers.Deezer, "10"), album.Id);
        Assert.Equal(type, album.Type);
        Assert.Equal(compilation, album.SecondaryTypes.Contains(AlbumSecondaryType.Compilation));
        Assert.Equal("2020-06-01", album.FirstReleaseDate);
        Assert.Equal("Alaskan Tapes", album.ArtistCredit);
    }

    [Fact]
    public void An_album_credits_its_main_artists_and_an_unknown_date_is_left_out()
    {
        var album = DeezerIndex.MapAlbum(Json("""
            {"id": 10, "title": "Dasher", "release_date": "0000-00-00", "artist": {"id": 1, "name": "Gerard Way"},
             "contributors": [{"id": 1, "name": "Gerard Way", "role": "Main"}, {"id": 2, "name": "Lydia Night", "role": "Main"}, {"id": 3, "name": "Someone", "role": "Featured"}]}
            """), null);
        Assert.Equal("Gerard Way & Lydia Night", album.ArtistCredit);
        Assert.Null(album.FirstReleaseDate);
    }

    [Theory]
    [InlineData("https://cdn-images.dzcdn.net/images/cover//500x500-000000-80-0-0.jpg", true)]
    [InlineData("https://cdn-images.dzcdn.net/images/artist//500x500-000000-80-0-0.jpg", true)]
    [InlineData("https://cdn-images.dzcdn.net/images/cover/de44d39f32b2074148c78f9a612245f4/500x500-000000-80-0-0.jpg", false)]
    public void A_blank_cover_or_picture_isnt_offered(string url, bool placeholder) =>
        Assert.Equal(placeholder, Melodex.Plugins.Deezer.Deezer.IsPlaceholder(url));

    [Fact]
    public void A_track_brings_its_length_isrc_and_where_its_from()
    {
        var track = DeezerIndex.MapTrack(Json("""
            {"id": 100, "title": "Notes", "isrc": "CAALN1700023", "duration": 127, "artist": {"id": 1, "name": "Alaskan Tapes"},
             "album": {"id": 10, "title": "Notes"}}
            """), null);
        Assert.Equal(new ProviderId(Providers.Deezer, "100"), track.Id);
        Assert.Equal(TimeSpan.FromSeconds(127), track.Duration);
        Assert.Equal(["CAALN1700023"], track.Isrcs);
        Assert.Equal(new ProviderId(Providers.Deezer, "10"), track.Album?.Id);
    }
}

public class SearchMergeTests
{
    private static IndexAlbum Album(string provider, string id, string title, string artist) =>
        new(new ProviderId(provider, id), title, new ArtistRef(new ProviderId(provider, artist), artist), artist, AlbumType.Album, []);

    [Fact]
    public void The_core_catalogue_comes_first_and_the_others_add_only_what_it_didnt_find()
    {
        var core = new IndexSearchResults([], [Album("musicbrainz", "a", "Dummy", "Portishead")], []);
        var other = new IndexSearchResults([], [Album("deezer", "1", "Dummy", "Portishead"), Album("deezer", "2", "Notes", "Alaskan Tapes")], []);

        var merged = CatalogueSearch.Merge([core, other]);

        Assert.Equal(["a", "2"], merged.Albums.Select(a => a.Id.Value));
    }
}

/// <summary>
/// A scan with Deezer after MusicBrainz: a song MusicBrainz doesn't know is matched from Deezer, filed with its
/// album. MusicBrainz and Deezer are stand-ins here, answering from what each test sets up.
/// </summary>
public sealed class GapFillingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private MelodexClient _api = null!;
    private int _seconds;

    private string Music => Path.Combine(_root, "music");

    public async Task InitializeAsync()
    {
        if (!TestLibrary.Exists)
            return;
        // A file tagged the way a streaming download is, for a single only Deezer has.
        var file = Path.Combine(Music, "spotiflac", "Alaskan Tapes", "Notes", "01. Notes.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(Path.Combine(TestLibrary.Music, "Unknown", "Track 01.mp3"), file);
        using (var tagged = TagLib.File.Create(file))
        {
            tagged.Tag.Title = "Notes";
            tagged.Tag.Performers = ["Alaskan Tapes"];
            tagged.Tag.AlbumArtists = ["Alaskan Tapes"];
            tagged.Tag.Album = "Notes - Single";
            tagged.Tag.Track = 1;
            tagged.Save();
            _seconds = (int)Math.Round(tagged.Properties.Duration.TotalSeconds);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", Path.Combine(_root, "data"))
            .UseSetting("Melodex:LibraryPath", Music)
            .UseSetting("Melodex:StagingPath", Path.Combine(_root, "staging"))
            .UseSetting("Melodex:ListenNowPath", Path.Combine(_root, "listen-now"))
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new StandIns(Deezer);
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Deezer's answers by path and query; anything else is "no data".</summary>
    private Dictionary<string, string> Deezer => new()
    {
        ["/search/artist?limit=10&q=Alaskan%20Tapes"] = """{"data": [{"id": 7852146, "name": "Alaskan Tapes"}]}""",
        ["/search/album?limit=10&q=Notes%20-%20Single%20Alaskan%20Tapes"] =
            """{"data": [{"id": 277727832, "title": "Notes", "record_type": "single", "artist": {"id": 7852146, "name": "Alaskan Tapes"}}]}""",
        ["/album/277727832"] = """
            {"id": 277727832, "title": "Notes", "upc": "067003088763", "record_type": "single", "release_date": "2020-06-01",
             "artist": {"id": 7852146, "name": "Alaskan Tapes"}, "contributors": [{"id": 7852146, "name": "Alaskan Tapes", "role": "Main"}]}
            """,
        ["/album/277727832/tracks?limit=100"] = $$$"""
            {"data": [{"id": 1577225662, "title": "Notes", "isrc": "CAALN1700023", "duration": {{{_seconds}}}, "track_position": 1, "disk_number": 1,
                       "artist": {"id": 7852146, "name": "Alaskan Tapes"}}], "total": 1}
            """,
    };

    [TestLibraryFact]
    public async Task A_song_the_core_catalogue_doesnt_have_is_matched_from_the_next_one()
    {
        await ScanAsync();
        Assert.Single(await _api.GetReviewAsync());

        await _api.AddPluginAsync(new AddPlugin(PluginId: DeezerIndexFactory.PluginId));
        await _api.SetFileAsideAsync((await _api.GetReviewAsync())[0].FileId, SetAside.Deferred);
        await ScanAsync();

        Assert.Empty(await _api.GetReviewAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var file = await db.LibraryFiles.Include(f => f.Track).Include(f => f.Edition!).ThenInclude(e => e.Album).ThenInclude(a => a.Artist).SingleAsync();
        Assert.Equal("Notes", file.Track!.Title);
        Assert.Equal("Notes", file.Edition!.Album.Title);
        Assert.Equal(AlbumType.Single, file.Edition.Album.Type);
        Assert.Equal("Alaskan Tapes", file.Edition.Album.Artist.Name);
        Assert.Equal(file.Edition.Album.ArtistId, file.ArtistId);
        Assert.Contains(await db.Crosswalk.Where(m => m.EntityId == file.TrackId).Select(m => m.Provider).ToListAsync(), p => p == Providers.Deezer);
    }

    private async Task ScanAsync()
    {
        var task = await _api.ScanLibraryAsync();
        while (task.State is TaskState.Queued or TaskState.Running)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.Equal(TaskState.Succeeded, task.State);
    }

    /// <summary>
    /// MusicBrainz that knows nothing, and Deezer that knows what the test gives it. Artwork and lyrics sources get
    /// nothing either.
    /// </summary>
    private sealed class StandIns(Dictionary<string, string> deezer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string body;
            if (uri.Host == "api.deezer.com")
                body = deezer.GetValueOrDefault(uri.PathAndQuery) ?? """{"error": {"type": "DataException", "message": "no data", "code": 800}}""";
            else if (uri.Host == "musicbrainz.org" && uri.AbsolutePath.StartsWith("/ws/2/", StringComparison.Ordinal) && uri.Query.Contains("query=", StringComparison.Ordinal))
            {
                var entity = uri.AbsolutePath["/ws/2/".Length..];
                var list = entity switch { "release-group" => "release-groups", "recording" => "recordings", _ => entity + "s" };
                body = $$"""{"created": "2026-11-03T00:00:00.000Z", "count": 0, "offset": 0, "{{list}}": []}""";
            }
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("{}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
