using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Data;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;
using Melodex.Plugins.Jellyfin;

namespace Melodex.Core.Tests;

/// <summary>Only one media server is used at a time, and each keeps its own logins for switching back.</summary>
public sealed class MediaServerTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public MediaServerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dataPath, "music"));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", Path.Combine(_dataPath, "music"))
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new JellyfinTests.FakeJellyfin();
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task One_media_server_is_used_at_a_time_and_each_keeps_its_logins()
    {
        var first = await AddAsync("Jellyfin upstairs");
        var second = await AddAsync("Jellyfin downstairs");
        // The first is used straight away; another waits, off, to be switched to.
        Assert.True(first.Enabled);
        Assert.False(second.Enabled);

        await _api.LinkMediaServerAsync(new LinkMediaServer("ann", "secret"));
        Assert.Equal(("Jellyfin upstairs", "ann"), Status(await _api.GetPlaylistSyncAsync()));

        // Turning the other on switches to it, turning the first off: its login isn't linked there yet.
        await _api.UpdatePluginAsync(second.Id, new UpdatePlugin(Enabled: true));
        Assert.Equal([second.Id], (await _api.ListPluginsAsync()).Where(p => p.Type == PluginType.Connector && p.Enabled).Select(p => p.Id));
        Assert.Equal(("Jellyfin downstairs", null), Status(await _api.GetPlaylistSyncAsync()));

        // Switching back finds the login kept for it.
        await _api.UpdatePluginAsync(first.Id, new UpdatePlugin(Enabled: true));
        Assert.Equal(("Jellyfin upstairs", "ann"), Status(await _api.GetPlaylistSyncAsync()));

        // Unlinking forgets only the login for the server in use, and removing a server forgets its logins.
        await _api.UpdatePluginAsync(second.Id, new UpdatePlugin(Enabled: true));
        await _api.LinkMediaServerAsync(new LinkMediaServer("ann", "secret"));
        await _api.UnlinkMediaServerAsync();
        await _api.UpdatePluginAsync(first.Id, new UpdatePlugin(Enabled: true));
        Assert.Equal("ann", (await _api.GetPlaylistSyncAsync()).Username);
        await _api.RemovePluginAsync(first.Id);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<MelodexDbContext>().MediaServerLogins.ToListAsync());
    }

    [Fact]
    public async Task Logins_can_be_kept_for_a_media_server_not_in_use_and_it_sets_playlists_folders()
    {
        var first = await AddAsync("Jellyfin upstairs");
        var second = await AddAsync("Jellyfin downstairs", folders: "::");
        // Each that syncs playlists is listed, on or not, the one in use first.
        Assert.Equal([("Jellyfin upstairs", true, (string?)null), ("Jellyfin downstairs", false, null)],
            (await _api.ListMediaServerAccountsAsync()).Select(a => (a.Server, a.InUse, a.Username)));

        // A login for the one not in use is checked there and kept, without touching the one in use.
        await Assert.ThrowsAsync<MelodexApiException>(() => _api.LinkMediaServerAccountAsync(second.Id, new LinkMediaServer("ann", "wrong")));
        var linked = await _api.LinkMediaServerAccountAsync(second.Id, new LinkMediaServer("ann", "secret"));
        Assert.Equal(("ann", false), (linked.Username, linked.InUse));
        Assert.Null((await _api.GetPlaylistSyncAsync()).Username);

        // The separator is the one in use's: / here, and the other's once it's switched to.
        Assert.Equal("/", (await _api.GetAuthStatusAsync()).PlaylistFolders);
        await _api.UpdatePluginAsync(second.Id, new UpdatePlugin(Enabled: true));
        Assert.Equal("::", (await _api.GetAuthStatusAsync()).PlaylistFolders);
        Assert.Equal(("Jellyfin downstairs", "ann"), Status(await _api.GetPlaylistSyncAsync()));
        var users = await _api.ListUsersAsync();
        Assert.Equal(("ann", "Jellyfin downstairs"), (users[0].MediaServerUsername, users[0].MediaServer));

        // A separator of more than 3 characters, or with a space, isn't one.
        var refused = await Assert.ThrowsAsync<MelodexApiException>(() => _api.UpdatePluginAsync(first.Id,
            new UpdatePlugin(Settings: JsonDocument.Parse("""{ "playlistFolders": "a b" }""").RootElement)));
        Assert.Contains("aren't spaces", refused.Message);
        await _api.UnlinkMediaServerAccountAsync(second.Id);
        Assert.Null((await _api.ListMediaServerAccountsAsync()).Single(a => a.InUse).Username);
    }

    [Fact]
    public void Logins_move_to_the_media_server_in_use_and_only_it_stays_on()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(connection, MelodexDbContext.Sqlite).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("TrustedLossless");

        var (navidrome, jellyfin, user) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.Database.ExecuteSql($"""
            INSERT INTO PluginInstances (Id, PluginId, Type, Name, ContractVersion, SettingsJson, Priority, Enabled, CreatedAt)
            VALUES ({jellyfin}, 'melodex.connector.jellyfin', 'Connector', 'Jellyfin', '1.0', {"{}"}, 1, 1, '2026-11-01'),
                   ({navidrome}, 'melodex.connector.navidrome', 'Connector', 'Navidrome', '1.0', {"{}"}, 0, 1, '2026-11-02')
            """);
        db.Database.ExecuteSql($"""
            INSERT INTO Users (Id, Username, NormalizedUsername, PasswordHash, Permissions, IsAdmin, Stamp, CreatedAt, MediaServerUsername, MediaServerPassword)
            VALUES ({user}, 'ann', 'ann', '', '', 1, '', '2026-11-01', 'ann', 'kept')
            """);
        migrator.Migrate();

        var login = db.MediaServerLogins.Single();
        Assert.Equal((user, navidrome, "ann", "kept"), (login.UserId, login.PluginInstanceId, login.Username, login.Password));
        Assert.Equal([navidrome], db.PluginInstances.Where(p => p.Enabled).Select(p => p.Id).ToList());
    }

    [Fact]
    public void Playlists_folders_move_from_the_admins_look_to_the_media_servers_that_sync_them()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(connection, MelodexDbContext.Sqlite).Options);
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("MediaServerLogins");

        var (navidrome, plex) = (Guid.NewGuid(), Guid.NewGuid());
        var (navidromeSettings, appearance) = ("""{ "url": "http://navidrome:4533" }""", """{ "color": "blue", "playlistFolders": ">" }""");
        db.Database.ExecuteSql($"""
            INSERT INTO PluginInstances (Id, PluginId, Type, Name, ContractVersion, SettingsJson, Priority, Enabled, CreatedAt)
            VALUES ({navidrome}, 'melodex.connector.navidrome', 'Connector', 'Navidrome', '1.0', {navidromeSettings}, 0, 1, '2026-11-01'),
                   ({plex}, 'melodex.connector.plex', 'Connector', 'Plex', '1.0', {"{}"}, 1, 0, '2026-11-02')
            """);
        db.Database.ExecuteSql($"""
            INSERT INTO Users (Id, Username, NormalizedUsername, PasswordHash, Permissions, IsAdmin, Stamp, CreatedAt, Appearance)
            VALUES ({Guid.NewGuid()}, 'ann', 'ann', '', '', 1, '', '2026-11-01', {appearance})
            """);
        migrator.Migrate();

        var settings = db.PluginInstances.AsNoTracking().ToDictionary(p => p.Id, p => JsonNode.Parse(p.SettingsJson)!);
        Assert.Equal((">", "http://navidrome:4533"), ((string?)settings[navidrome]["playlistFolders"], (string?)settings[navidrome]["url"]));
        Assert.Null(settings[plex]["playlistFolders"]);
    }

    private static (string? Server, string? Username) Status(PlaylistSyncStatus status) => (status.Server, status.Username);

    private Task<PluginInfo> AddAsync(string name, string folders = "/") => _api.AddPluginAsync(new AddPlugin(PluginId: JellyfinConnectorFactory.PluginId, Name: name,
        Settings: JsonDocument.Parse($$"""{ "url": "http://jellyfin:8096", "apiKey": "key", "syncPlaylists": true, "playlistFolders": "{{folders}}" }""").RootElement));
}
