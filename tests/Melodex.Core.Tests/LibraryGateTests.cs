using System.Net.Http.Json;
using System.Text.Json;
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
/// Phase 1's gate, against the generated test library: scan it with MusicBrainz (recorded responses), then check every
/// file's match and every album's status against the answer key, in both ways of handling songs on several albums.
/// </summary>
public sealed class LibraryGateTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;
    private readonly List<string> _problems = [];

    public LibraryGateTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new ReplayHandler(TestLibrary.MusicBrainzFixtures);
                http.RateLimit = ReplayHandler.Recording;
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
    public async Task Scanning_the_test_library_matches_the_answer_key()
    {
        var key = TestLibrary.LoadAnswerKey();

        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + (ReplayHandler.Recording ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(3));
        while (task.State is TaskState.Queued or TaskState.Running)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The scan didn't finish in time: {task.Progress}");
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);
        Assert.DoesNotContain("failed", task.Progress);

        await CheckFilesAsync(key.GetProperty("files"));

        var settings = await _api.GetLibrarySettingsAsync();
        Assert.Equal(DuplicateTracks.KeepInAlbum, settings.DuplicateTracks);
        await SettleAsync();
        await CheckAlbumsAsync(key.GetProperty("artists"), fetchedOnce: true);

        await _api.SaveLibrarySettingsAsync(settings with { DuplicateTracks = DuplicateTracks.KeepBoth });
        await SettleAsync();
        await CheckAlbumsAsync(key.GetProperty("artists"), fetchedOnce: false);

        Assert.True(_problems.Count == 0, $"{_problems.Count} differences from the answer key:\n" + string.Join('\n', _problems));
    }

    /// <summary>
    /// Waits for the cached library views to catch up with what was just written. Pages show the last answer meanwhile,
    /// but here they're compared with views worked out afresh.
    /// </summary>
    private async Task SettleAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await _factory.Services.GetRequiredService<Melodex.Core.Library.LibraryCache>().SettleAsync(timeout.Token);
    }

    private async Task CheckFilesAsync(JsonElement expected)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var mbids = await db.Crosswalk.Where(c => c.Provider == Providers.MusicBrainz).ToDictionaryAsync(c => (c.EntityKind, c.EntityId), c => c.Value);
        var files = await db.LibraryFiles.ToDictionaryAsync(f => f.Path);

        foreach (var want in expected.EnumerateArray())
        {
            var path = want.GetProperty("path").GetString()!;
            var method = want.GetProperty("method").GetString()!;
            if (!files.TryGetValue(path, out var file))
            {
                _problems.Add($"{path}: not scanned");
                continue;
            }
            var recording = file.TrackId is { } t ? mbids.GetValueOrDefault((EntityKind.Track, t)) : null;
            if (method == nameof(MatchMethod.Unmatched))
            {
                if (file.TrackId is not null)
                    _problems.Add($"{path}: should go to review, but matched {recording} by {file.MatchMethod} ({file.MatchConfidence:0.00})");
                continue;
            }

            var wantRecording = want.GetProperty("recordingMbid").GetString();
            if (recording != wantRecording || file.MatchMethod.ToString() != method)
                _problems.Add($"{path}: want {wantRecording} by {method}, got {recording ?? "nothing"} by {file.MatchMethod} ({file.ReviewReason})");
            else if (method == nameof(MatchMethod.MusicBrainzTags))
            {
                // Tagged files name their edition, so that must match too. Untagged ones may fairly land on an identical one.
                var edition = file.EditionId is { } e ? mbids.GetValueOrDefault((EntityKind.Edition, e)) : null;
                if (edition != want.GetProperty("releaseMbid").GetString())
                    _problems.Add($"{path}: want edition {want.GetProperty("releaseMbid").GetString()}, got {edition}");
            }
        }

        var unmatched = expected.EnumerateArray().Count(f => f.GetProperty("method").GetString() == nameof(MatchMethod.Unmatched));
        var review = (await _api.GetReviewAsync()).Count;
        if (review != unmatched)
            _problems.Add($"review list: want {unmatched} files, got {review}");
    }

    private async Task CheckAlbumsAsync(JsonElement expected, bool fetchedOnce)
    {
        var mode = fetchedOnce ? "fetched once" : "kept both";
        var libraryArtists = await _api.GetLibraryArtistsAsync();
        var missingView = await _api.GetMissingAsync();

        foreach (var want in expected.EnumerateArray())
        {
            var name = want.GetProperty("name").GetString()!;
            var summary = libraryArtists.SingleOrDefault(a => MusicBrainzId(a.Ids) == want.GetProperty("mbid").GetString());
            if (summary is null)
            {
                _problems.Add($"{name}: not in the library");
                continue;
            }
            var artist = (await _api.GetArtistAsync(summary.Id))!;
            var albums = artist.Albums.Where(a => MusicBrainzId(a.Ids) is not null).ToDictionary(a => MusicBrainzId(a.Ids)!);

            if (albums.Count != want.GetProperty("releaseGroupCount").GetInt32())
                _problems.Add($"{name}: want {want.GetProperty("releaseGroupCount").GetInt32()} albums in the discography, got {albums.Count}");

            foreach (var owned in want.GetProperty("owned").EnumerateArray())
                await CheckOwnedAsync(name, owned, albums);

            var missing = want.GetProperty("missing").EnumerateArray().ToList();
            var wantMissing = missing.Where(m => !fetchedOnce || !m.GetProperty("coveredWhenFetchedOnce").GetBoolean())
                .Select(m => m.GetProperty("releaseGroupMbid").GetString()!).ToHashSet();
            var wantCovered = missing.Where(m => fetchedOnce && m.GetProperty("coveredWhenFetchedOnce").GetBoolean())
                .Select(m => m.GetProperty("releaseGroupMbid").GetString()!).ToHashSet();
            var gotMissing = missingView.Where(m => m.Album.ArtistId == artist.Id).Select(m => MusicBrainzId(m.Album.Ids)!).ToHashSet();
            var gotCovered = albums.Where(a => a.Value is { Status: AlbumStatus.Covered, HiddenByDefault: false }).Select(a => a.Key).ToHashSet();

            Compare($"{name}, missing view ({mode})", wantMissing, gotMissing, albums);
            Compare($"{name}, covered ({mode})", wantCovered, gotCovered, albums);
            if (summary.MissingAlbums != gotMissing.Count)
                _problems.Add($"{name}: library list says {summary.MissingAlbums} missing, missing view lists {gotMissing.Count} ({mode})");

            var hidden = albums.Values.Count(a => a.HiddenByDefault && a.Status is AlbumStatus.Missing or AlbumStatus.Covered);
            if (hidden != want.GetProperty("missingHiddenByDefault").GetInt32())
                _problems.Add($"{name}: want {want.GetProperty("missingHiddenByDefault").GetInt32()} unowned albums hidden by default, got {hidden}");
        }
    }

    private async Task CheckOwnedAsync(string artist, JsonElement owned, Dictionary<string, AlbumSummary> albums)
    {
        var title = owned.GetProperty("title").GetString();
        if (!albums.TryGetValue(owned.GetProperty("releaseGroupMbid").GetString()!, out var album))
        {
            _problems.Add($"{artist}, {title}: not in the discography");
            return;
        }
        var want = owned.GetProperty("complete").GetBoolean() ? AlbumStatus.Complete : AlbumStatus.Partial;
        if (album.Status != want)
            _problems.Add($"{artist}, {title}: want {want}, got {album.Status}");

        var detail = (await _api.GetAlbumAsync(album.Id))!;
        var edition = detail.Editions.SingleOrDefault(e => e.Id == detail.OwnedEditionId);
        if (MusicBrainzId(edition?.Ids ?? []) != owned.GetProperty("editionMbid").GetString())
            _problems.Add($"{artist}, {title}: want edition {owned.GetProperty("editionMbid").GetString()}, got {MusicBrainzId(edition?.Ids ?? [])}");

        var wantMissing = owned.GetProperty("missingTracks").EnumerateArray().Select(t => t.GetProperty("recordingMbid").GetString()!).ToHashSet();
        var gotMissing = detail.Tracks.Where(t => t.Status == TrackStatus.Missing).Select(t => MusicBrainzId(t.Ids)!).ToHashSet();
        if (!wantMissing.SetEquals(gotMissing))
            _problems.Add($"{artist}, {title}: want missing tracks [{string.Join(", ", wantMissing)}], got [{string.Join(", ", gotMissing)}]");
    }

    private void Compare(string what, HashSet<string> want, HashSet<string> got, Dictionary<string, AlbumSummary> albums)
    {
        string Name(string mbid) => albums.TryGetValue(mbid, out var a) ? $"{a.Title} ({a.Status})" : mbid;
        foreach (var extra in got.Except(want))
            _problems.Add($"{what}: shouldn't list {Name(extra)}");
        foreach (var lacking in want.Except(got))
            _problems.Add($"{what}: should list {Name(lacking)}");
    }

    private static string? MusicBrainzId(IReadOnlyList<ProviderId> ids) =>
        ids.FirstOrDefault(i => i.Provider == Providers.MusicBrainz)?.Value;
}
