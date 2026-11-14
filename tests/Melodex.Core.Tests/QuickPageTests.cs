using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Pages ask to be quick: they get what's known at once, and the rest is fetched meanwhile.</summary>
public sealed class QuickPageTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public QuickPageTests()
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
    public async Task A_quick_album_answers_with_what_is_known_and_its_editions_follow()
    {
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);

        // An album whose editions are known, but old.
        var artist = (await _api.GetLibraryArtistsAsync()).First();
        var known = (await _api.GetArtistAsync(artist.Id, quick: true))!.Albums.First(a => a.Status == AlbumStatus.Complete);
        var old = DateTime.UtcNow - CatalogueService.EditionsTtl - TimeSpan.FromDays(1);
        await SetRefreshedAsync(known.Id, old);

        // What's known comes at once, and the editions are fetched again meanwhile.
        var album = (await _api.GetAlbumAsync(known.Id, quick: true))!;
        Assert.NotEmpty(album.Editions);
        while (await RefreshedAsync(known.Id) <= old && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.True(await RefreshedAsync(known.Id) > old);
        Assert.False((await _api.GetAlbumAsync(known.Id, quick: true))!.Fetching);
    }

    private async Task SetRefreshedAsync(Guid albumId, DateTime at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        (await db.Albums.FindAsync(albumId))!.EditionsRefreshedAt = at;
        await db.SaveChangesAsync();
    }

    private async Task<DateTime?> RefreshedAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        return (await db.Albums.FindAsync(albumId))!.EditionsRefreshedAt;
    }
}
