using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The first run's steps, everyone's look, Melodex without a catalogue, and resetting it.</summary>
public sealed class SetupTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public SetupTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", Path.Combine(TestLibrary.Music, "Portishead", "Dummy (1994)"))
            .UseSetting("Melodex:ListenNowPath", Path.Combine(_dataPath, "listen-now")));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task Setup_shows_until_the_admin_finishes_it()
    {
        var anyone = new MelodexClient(_factory.CreateClient());
        Assert.False((await anyone.GetAuthStatusAsync()).SetUp);

        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var status = await admin.GetAuthStatusAsync();
        Assert.True(status.SetUp);
        Assert.False(status.SetupFinished);

        await admin.FinishSetupAsync();
        Assert.True((await admin.GetAuthStatusAsync()).SetupFinished);
    }

    [Fact]
    public async Task Everyone_has_the_default_look_until_they_choose_their_own()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        await admin.SetDefaultAppearanceAsync(new Appearance(ThemeColor.Blue, ThemeMode.Dark));
        var status = await admin.GetAuthStatusAsync();
        Assert.Equal(ThemeColor.Blue, status.Appearance?.Color);
        Assert.False(status.OwnAppearance);

        await admin.SetAppearanceAsync(new Appearance(ThemeColor.Green));
        status = await admin.GetAuthStatusAsync();
        Assert.Equal(ThemeColor.Green, status.Appearance?.Color);
        Assert.True(status.OwnAppearance);

        // A change to everyone's look doesn't reach someone with their own.
        await admin.SetDefaultAppearanceAsync(new Appearance(ThemeColor.Amber));
        Assert.Equal(ThemeColor.Green, (await admin.GetAuthStatusAsync()).Appearance?.Color);

        await admin.ClearAppearanceAsync();
        status = await admin.GetAuthStatusAsync();
        Assert.Equal(ThemeColor.Amber, status.Appearance?.Color);
        Assert.False(status.OwnAppearance);
    }

    [TestLibraryFact]
    public async Task Without_a_catalogue_Melodex_is_a_plain_music_library()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        Assert.True((await admin.GetAuthStatusAsync()).Catalogue);
        // The last catalogue can go.
        foreach (var index in (await admin.ListPluginsAsync()).Where(p => p.Type == PluginType.Index))
            await admin.RemovePluginAsync(index.Id);
        Assert.False((await admin.GetAuthStatusAsync()).Catalogue);
        // And stays gone: MusicBrainz is only offered once.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            await IndexPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
            Assert.DoesNotContain(db.PluginInstances, p => p.Type == PluginType.Index);
            // The test library is shared, so no lyrics are written into it.
            await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);
        }

        var task = await admin.ScanLibraryAsync();
        while (task.State is TaskState.Queued or TaskState.Running)
        {
            await Task.Delay(100);
            task = (await admin.GetTaskAsync(task.Id))!;
        }
        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.Contains("as no catalogue is set up", task.Progress);
        Assert.Empty(await admin.GetReviewAsync());

        // Listed as their tags say, under an artist of their own.
        IReadOnlyList<LibraryArtist> artists = [];
        for (var tries = 0; tries < 50 && artists.Count == 0; tries++, await Task.Delay(100))
            artists = await admin.GetLibraryArtistsAsync();
        var portishead = Assert.Single(artists);
        Assert.Equal("Portishead", portishead.Name);
        Assert.True(portishead.Local);

        // Search answers from the library alone.
        var found = await admin.SearchAsync("Dummy");
        Assert.Contains(found.Albums, a => a.Title == "Dummy" && a.Local);
        Assert.Contains((await admin.SearchAsync("Portis")).Artists, a => a.Name == "Portishead" && a.Local);
    }

    [Fact]
    public async Task Only_the_admin_can_reset_Melodex_and_they_type_reset_to_do_it()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        await admin.CreateUserAsync(new CreateUser("manager", "managing password", [Scopes.Read, Scopes.Settings]));
        var manager = _factory.CreateClient();
        (await manager.PostAsJsonAsync("api/v1/auth/login", new SignIn("manager", "managing password"))).EnsureSuccessStatusCode();
        var mark = Path.Combine(_dataPath, "factory-reset");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PostAsJsonAsync("api/v1/setup/reset", new FactoryResetRequest("reset"), MelodexJson.Options)).StatusCode);
        var wrong = await Assert.ThrowsAsync<MelodexApiException>(() => admin.FactoryResetAsync("yes"));
        Assert.Contains("reset", wrong.Message);
        Assert.False(File.Exists(mark));

        await admin.FactoryResetAsync("Reset");
        Assert.True(File.Exists(mark));
    }

    [Fact]
    public void A_reset_wipes_what_Melodex_holds_but_never_the_music()
    {
        var root = Path.Combine(_dataPath, "reset");
        var options = new MelodexOptions
        {
            DataPath = Path.Combine(root, "data"),
            LibraryPath = Path.Combine(root, "music"),
            ListenNowPath = Path.Combine(root, "listen-now"),
        };
        var database = Path.Combine(options.DataPath, "melodex.db");
        string Make(params string[] parts)
        {
            var path = Path.Combine([root, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }
        var kept = new[]
        {
            Make("music", "Artist", "Album", "01 Song.flac"),
            Make("data", "plugins", "abc", "state.json"),
            Make("data", "keys", "key.xml"),
        };
        var wiped = new[]
        {
            Make("data", "melodex.db"), Make("data", "melodex.db-wal"), Make("data", "cache", "artists.json"),
            Make("data", "logs", "2026-11-08.log"), Make("data", "artwork", "a.jpg"), Make("data", "playlist-covers", "p.jpg"),
            Make("listen-now", "abc", "song.opus"), Make("listen-now", "song.opus"),
        };

        Assert.False(FactoryReset.ApplyIfRequested(options, database));
        Assert.All(wiped, f => Assert.True(File.Exists(f)));

        FactoryReset.Request(options);
        Assert.True(FactoryReset.ApplyIfRequested(options, database));
        Assert.All(kept, f => Assert.True(File.Exists(f), f));
        Assert.All(wiped, f => Assert.False(File.Exists(f), f));
        Assert.True(Directory.Exists(options.ListenNowPath));
        Assert.False(FactoryReset.ApplyIfRequested(options, database));

        // A Listen Now folder set up inside the music folder is left alone.
        var inside = new MelodexOptions { DataPath = options.DataPath, LibraryPath = options.LibraryPath, ListenNowPath = Path.Combine(root, "music", "Artist") };
        FactoryReset.Request(inside);
        Assert.True(FactoryReset.ApplyIfRequested(inside, database));
        Assert.True(File.Exists(kept[0]));
    }
}
