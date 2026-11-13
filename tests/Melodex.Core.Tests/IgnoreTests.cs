using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Ignoring a song stops it counting against every album it's on.</summary>
public sealed class IgnoreTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public IgnoreTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new ReplayHandler(TestLibrary.MusicBrainzFixtures);
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    [TestLibraryFact]
    public async Task Ignoring_the_missing_tracks_of_a_partly_owned_album_makes_it_complete()
    {
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);

        var partial = await FindAsync(a => a.Status == AlbumStatus.Partial);
        Assert.NotNull(partial);
        var album = (await _api.GetAlbumAsync(partial.Id))!;
        var missing = album.Tracks.Where(t => t.Status == TrackStatus.Missing).ToList();
        Assert.NotEmpty(missing);

        foreach (var track in missing)
            await _api.SetTrackIgnoredAsync(track.TrackId, true);

        album = (await _api.GetAlbumAsync(partial.Id))!;
        Assert.Equal(AlbumStatus.Complete, album.Album.Status);
        Assert.Equal(album.Album.OwnedTracks, album.Album.TotalTracks);
        Assert.All(album.Tracks.Where(t => missing.Any(m => m.TrackId == t.TrackId)), t =>
        {
            Assert.Equal(TrackStatus.Ignored, t.Status);
            Assert.True(t.Ignored);
        });

        foreach (var track in missing)
            await _api.SetTrackIgnoredAsync(track.TrackId, false);
        Assert.Equal(AlbumStatus.Partial, (await _api.GetAlbumAsync(partial.Id))!.Album.Status);
    }

    private async Task<AlbumSummary?> FindAsync(Func<AlbumSummary, bool> match)
    {
        foreach (var artist in await _api.GetLibraryArtistsAsync())
            if ((await _api.GetArtistAsync(artist.Id))!.Albums.FirstOrDefault(match) is { } album)
                return album;
        return null;
    }
}
