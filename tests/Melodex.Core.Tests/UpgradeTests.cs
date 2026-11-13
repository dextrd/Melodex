using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Data;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>An install from when Melodex was called Navidex carries on: its database, plugins and keys.</summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    private WebApplicationFactory<Program> Start() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("Melodex:DataPath", _dataPath));

    [Fact]
    public async Task A_navidex_database_is_moved_and_its_plugins_and_keys_carry_on()
    {
        string oldKey;
        using (var before = Start())
        {
            var admin = new MelodexClient(before.CreateSignedInClient());
            oldKey = "ndx_" + (await admin.CreateApiKeyAsync(new CreateApiKey("Old app", ["read"]))).Key["mdx_".Length..];
            using var scope = before.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.PluginInstances.Add(new PluginInstance
            {
                PluginId = "navidex.retrieval.inbox", Type = PluginType.Retrieval, Name = "Inbox", ContractVersion = "1.0", Endpoint = "http://inbox.test",
            });
            // A key made under the old name kept its old prefix, and only its hash is stored.
            var key = await db.ApiKeys.SingleAsync();
            key.Hash = Access.AccessService.HashKey(oldKey);
            await db.SaveChangesAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Move(Path.Combine(_dataPath, "melodex.db"), Path.Combine(_dataPath, "navidex.db"));
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(Path.Combine(_dataPath, "melodex.db" + suffix)))
                File.Move(Path.Combine(_dataPath, "melodex.db" + suffix), Path.Combine(_dataPath, "navidex.db" + suffix));

        using var after = Start();
        var app = after.CreateClient();
        app.DefaultRequestHeaders.Add(Access.AccessService.KeyHeader, oldKey);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.GetAsync("api/v1/library")).StatusCode);

        Assert.True(File.Exists(Path.Combine(_dataPath, "melodex.db")));
        Assert.False(File.Exists(Path.Combine(_dataPath, "navidex.db")));
        using var check = after.Services.CreateScope();
        var plugins = await check.ServiceProvider.GetRequiredService<MelodexDbContext>().PluginInstances.Select(p => p.PluginId).ToListAsync();
        Assert.Contains("melodex.retrieval.inbox", plugins);
        Assert.DoesNotContain(plugins, id => id.StartsWith("navidex."));
    }

    [Fact]
    public async Task A_new_install_offers_setup_until_its_done_and_an_old_one_doesnt()
    {
        var music = Path.Combine(_dataPath, "music", "Artist", "Album");
        Directory.CreateDirectory(music);
        await File.WriteAllBytesAsync(Path.Combine(music, "01 Song.flac"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(music, "cover.jpg"), [0]);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", Path.Combine(_dataPath, "music")));
        var admin = new MelodexClient(factory.CreateSignedInClient());

        var setup = await admin.GetSetupAsync();
        Assert.False(setup.Done);
        Assert.True(setup.LibraryReadable);
        Assert.Equal(1, setup.AudioFiles);
        Assert.False(setup.MediaServer);
        Assert.False(setup.Scanned);

        await admin.FinishSetupAsync();
        Assert.True((await admin.GetSetupAsync()).Done);

        // An install with music already in its library, from before there were setup steps, skips them.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.Settings.Remove(await db.Settings.SingleAsync(s => s.Key == "setup.done"));
            db.LibraryFiles.Add(new LibraryFile { Path = "Artist/Album/01 Song.flac", Size = 1, ModifiedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.True((await admin.GetSetupAsync()).Done);
    }

    [Theory]
    [InlineData("navidex.retrieval.ytdlp", "melodex.retrieval.ytdlp")]
    [InlineData("melodex.retrieval.ytdlp", "melodex.retrieval.ytdlp")]
    [InlineData("someone.retrieval.other", "someone.retrieval.other")]
    public void Old_plugin_ids_are_read_as_new(string id, string now) => Assert.Equal(now, FromNavidex.PluginId(id));
}
