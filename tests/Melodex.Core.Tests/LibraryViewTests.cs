using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The library page's three views, the cache behind them, and the task list the Activity page shows.</summary>
public sealed class LibraryViewTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;
    private readonly FakeCoverArt _coverArt = new(new ReplayHandler(TestLibrary.MusicBrainzFixtures));

    public LibraryViewTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => _coverArt;
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [TestLibraryFact]
    public async Task Albums_and_tracks_list_what_you_have_and_follow_changes()
    {
        // Asked for before the scan, so the cache holds an empty answer the scan has to replace.
        Assert.Empty(await _api.GetLibraryAlbumsAsync());
        await ScanAsync();

        var albums = await _api.GetLibraryAlbumsAsync();
        Assert.All(albums, a => Assert.Contains(a.Album.Status, new[] { AlbumStatus.Complete, AlbumStatus.Partial }));
        var dummy = Assert.Single(albums, a => a.Album.Title == "Dummy");
        Assert.Equal("Portishead", dummy.ArtistName);
        Assert.Equal($"api/v1/artwork/edition/{(await _api.GetAlbumAsync(dummy.Album.Id))!.OwnedEditionId}", dummy.Album.CoverUrl);
        var partial = Assert.Single(albums, a => a.Album.Title == "Portishead" && a.Album.Status == AlbumStatus.Partial);

        var tracks = await _api.GetLibraryTracksAsync();
        Assert.Equal((await _api.GetLibraryStatusAsync())!.Matched, tracks.Count);
        var gloryBox = Assert.Single(tracks, t => t.Title == "Glory Box" && t.AlbumTitle == "Dummy");
        Assert.Equal("11", gloryBox.Number);
        Assert.Equal(dummy.Album.CoverUrl, gloryBox.CoverUrl);

        // Ignoring the missing tracks completes the album, and the cached view catches up at once.
        var missing = (await _api.GetAlbumAsync(partial.Album.Id))!.Tracks.Where(t => t.Status == TrackStatus.Missing).ToList();
        foreach (var track in missing)
            await _api.SetTrackIgnoredAsync(track.TrackId, true);
        Assert.Equal(AlbumStatus.Complete, (await _api.GetLibraryAlbumsAsync()).Single(a => a.Album.Id == partial.Album.Id).Album.Status);
    }

    [TestLibraryFact]
    public async Task Scans_are_listed_as_tasks()
    {
        await ScanAsync();
        var scan = Assert.Single(await _api.ListTasksAsync(), t => t.Kind == "library-scan");
        Assert.Equal("Library scan", scan.Title);
        Assert.Equal(TaskState.Succeeded, scan.State);
        Assert.False(scan.Active);
        // What it queues after it, such as checking lossless files, can still be going.
        Assert.DoesNotContain(await _api.ListTasksAsync(active: true), t => t.Kind == "library-scan");
    }

    [TestLibraryFact]
    public async Task Artwork_is_found_through_the_plugins_once_and_forgotten_when_they_change()
    {
        await ScanAsync();
        var http = _factory.CreateSignedInClient();
        var dummy = (await _api.GetLibraryAlbumsAsync()).Single(a => a.Album.Title == "Dummy");

        // Covers of what you have are fetched during the scan, before anyone looks.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (_coverArt.Requests == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.NotEqual(0, _coverArt.Requests);
        // Let it finish warming the other covers, so the counts below are only this test's.
        for (var seen = -1; seen != _coverArt.Requests && DateTime.UtcNow < deadline; await Task.Delay(1000))
            seen = _coverArt.Requests;

        using (var response = await http.GetAsync(dummy.Album.CoverUrl))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/gif", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(FakeCoverArt.Image, await response.Content.ReadAsByteArrayAsync());
        }
        var asked = _coverArt.Requests;
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(dummy.Album.CoverUrl)).StatusCode);
        Assert.Equal(asked, _coverArt.Requests);

        // The archive has no artist pictures, and nothing else is set up to find them.
        var artist = (await _api.GetLibraryArtistsAsync()).Single(a => a.Name == "Portishead");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(artist.ImageUrl)).StatusCode);

        // Turning the archive off forgets what it found.
        var archive = (await _api.ListPluginsAsync()).Single(p => p.PluginId == "melodex.artwork.coverartarchive");
        await _api.UpdatePluginAsync(archive.Id, new UpdatePlugin(Enabled: false));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(dummy.Album.CoverUrl)).StatusCode);
        Assert.Equal(asked, _coverArt.Requests);
    }

    private async Task ScanAsync()
    {
        // The test library is shared, so no lyrics are written into it.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Melodex.Core.Library.SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.Active && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);
    }
}
