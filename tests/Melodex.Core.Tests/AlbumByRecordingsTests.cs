using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>
/// Albums whose files name them otherwise than the catalogue does (アニップス, tagged Anipps): found by the songs'
/// recordings, so the songs are on the album rather than on none (#26). Against MusicBrainz, with recorded responses.
/// </summary>
public sealed class AlbumByRecordingsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private MelodexClient _api = null!;

    private string Music => Path.Combine(_root, "music");

    public Task InitializeAsync()
    {
        if (!TestLibrary.Exists)
            return Task.CompletedTask;
        // Dummy, with its MusicBrainz recording IDs but not its album's, under a name the catalogue doesn't give it.
        Retag(Path.Combine("Portishead", "Dummy (1994)"), Path.Combine("Portishead", "ダミー"), "ダミー");
        // Discovery, with only ISRCs, under another.
        Retag(Path.Combine("Daft Punk", "Discovery (2001)"), Path.Combine("Daft Punk", "Discovery (Remastered Japan Edition)"), "ディスカバリー");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", Path.Combine(_root, "data"))
            .UseSetting("Melodex:LibraryPath", Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new ReplayHandler(TestLibrary.MusicBrainzFixtures);
                http.RateLimit = ReplayHandler.Recording;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_root);
    }

    private void Retag(string from, string to, string album)
    {
        var folder = Path.Combine(Music, to);
        Directory.CreateDirectory(folder);
        foreach (var source in Directory.GetFiles(Path.Combine(TestLibrary.Music, from)))
        {
            var file = Path.Combine(folder, Path.GetFileName(source));
            File.Copy(source, file);
            using var tagged = TagLib.File.Create(file);
            tagged.Tag.Album = album;
            tagged.Tag.MusicBrainzReleaseId = null;
            tagged.Tag.MusicBrainzReleaseGroupId = null;
            tagged.Save();
        }
    }

    [TestLibraryFact]
    public async Task Songs_on_an_album_named_otherwise_are_found_on_it_by_their_recordings()
    {
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + (ReplayHandler.Recording ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(2));
        while (task.State is TaskState.Queued or TaskState.Running)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The scan didn't finish in time: {task.Progress}");
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var files = await db.LibraryFiles.Select(f => new { f.Path, f.TrackId, AlbumId = (Guid?)f.Edition!.AlbumId }).ToListAsync();
        var groups = await db.Crosswalk.Where(c => c.Provider == Providers.MusicBrainz && c.EntityKind == EntityKind.Album)
            .ToDictionaryAsync(c => c.EntityId, c => c.Value);

        foreach (var (folder, releaseGroup) in new[]
                 {
                     // Release groups as the answer key has them.
                     ("Portishead/ダミー/", "48140466-cff6-3222-bd55-63c27e43190d"),
                     ("Daft Punk/Discovery (Remastered Japan Edition)/", "48117b90-a16e-34ca-a514-19c702df1158"),
                 })
        {
            var inFolder = files.Where(f => f.Path.StartsWith(folder, StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(inFolder);
            Assert.All(inFolder, f =>
            {
                Assert.NotNull(f.TrackId);
                Assert.True(f.AlbumId is { } album && groups.GetValueOrDefault(album) == releaseGroup, $"{f.Path} isn't on its album");
            });
        }
    }
}
