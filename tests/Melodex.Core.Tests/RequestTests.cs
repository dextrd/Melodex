using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Melodex.Client;
using Melodex.Core.Import;
using Melodex.Core.Library;
using Melodex.Core.Plugins;
using Melodex.Core.Requests;
using Melodex.Core.Tests.Support;
using Xunit.Abstractions;

namespace Melodex.Core.Tests;

/// <summary>
/// The test library, scanned once with recorded MusicBrainz responses, with fake retrieval plugins and a fake beets.
/// Its music folder is a copy, so imports don't touch the generated library. Each test asks for different music.
/// </summary>
public sealed class RequestFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));

    public string Music => Path.Combine(_root, "music");
    public string Staging => Path.Combine(_root, "staging");
    public string ListenNow => Path.Combine(_root, "listen-now");
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public MelodexClient Api { get; private set; } = null!;
    public FakeRetrieval Retrieval { get; private set; } = null!;
    public FakeImporter Importer { get; private set; } = null!;

    /// <summary>The songs LRCLIB has lyrics for, by title.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Lyrics { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Glory Box"] = "[00:01.00] Glory box",
    };

    public async Task InitializeAsync()
    {
        if (!TestLibrary.Exists)
            return;
        Copy(TestLibrary.Music, Music);
        Directory.CreateDirectory(Staging);
        Retrieval = new FakeRetrieval(Staging);
        Importer = new FakeImporter(Staging, Music);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", Path.Combine(_root, "data"))
            .UseSetting("Melodex:LibraryPath", Music)
            .UseSetting("Melodex:StagingPath", Staging)
            .UseSetting("Melodex:ListenNowPath", ListenNow)
            .ConfigureServices(services =>
            {
                services.Configure<PluginHttpOptions>(http =>
                {
                    http.PrimaryHandler = () => new FakeLrclib(new ReplayHandler(TestLibrary.MusicBrainzFixtures), Lyrics);
                    http.RateLimit = false;
                });
                services.Configure<RequestPipelineOptions>(o =>
                {
                    o.PollInterval = TimeSpan.FromMilliseconds(20);
                    o.FetchTimeout = TimeSpan.FromSeconds(1);
                });
                services.AddHttpClient(RetrievalPlugins.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Retrieval);
                services.RemoveAll<IImportWorker>();
                services.AddSingleton<IImportWorker>(Importer);
            }));
        Api = new MelodexClient(Factory.CreateSignedInClient());
        // Lyrics are found after a scan only where a test asks, so none are written behind other tests' backs.
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);

        var task = await Api.ScanLibraryAsync();
        while (task.State is TaskState.Queued or TaskState.Running)
        {
            await Task.Delay(200);
            task = (await Api.GetTaskAsync(task.Id))!;
        }
        if (task.State != TaskState.Succeeded)
            throw new InvalidOperationException($"The scan failed: {task.Error}");
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Adds a fake retrieval plugin by URL, the way a user adds a real one.</summary>
    public async Task<PluginInfo> AddSourceAsync(FakeSource source)
    {
        Retrieval.Sources[source.Name] = source;
        return await Api.AddPluginAsync(new AddPlugin(Endpoint: $"http://{source.Name}:8080"));
    }

    public async Task RemoveSourcesAsync()
    {
        foreach (var plugin in (await Api.ListPluginsAsync()).Where(p => p.Type == Contracts.Plugins.PluginType.Retrieval))
            await Api.RemovePluginAsync(plugin.Id);
        Retrieval.Sources.Clear();
    }

    /// <summary>Waits until the request and all its parts are finished.</summary>
    public async Task<RequestInfo> WaitAsync(Guid id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            var request = (await Api.GetRequestAsync(id))!;
            if (!request.Active)
                return request;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Still {request.State}: {request.Detail}");
            await Task.Delay(50);
        }
    }

    public async Task<AlbumSummary> AlbumAsync(string artist, string title)
    {
        var id = (await Api.GetLibraryArtistsAsync()).Single(a => a.Name == artist).Id;
        return (await Api.GetArtistAsync(id))!.Albums.First(a => a.Title == title);
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}

[CollectionDefinition(Name)]
public sealed class RequestCollection : ICollectionFixture<RequestFixture>
{
    public const string Name = "requests";
}

[Collection(RequestCollection.Name)]
public sealed class RequestTests(RequestFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private MelodexClient Api => fixture.Api;

    public Task InitializeAsync() => TestLibrary.Exists ? fixture.RemoveSourcesAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void A_partial_release_date_sorts_after_full_dates_in_its_period()
    {
        string?[] dates = [null, "2005", "2005-03", "2005-03-14", "2005-03-01", "2004"];
        Assert.Equal(["2004", "2005-03-01", "2005-03-14", "2005-03", "2005", null], dates.OrderBy(RequestPlanner.DateKey, StringComparer.Ordinal));
    }

    [TestLibraryFact]
    public async Task Nothing_can_be_asked_for_while_a_scan_reads_the_library()
    {
        var album = await fixture.AlbumAsync("Daft Punk", "Homework");
        using (fixture.Factory.Services.GetRequiredService<Melodex.Core.Library.LibraryReading>().Begin())
        {
            Assert.True((await Api.GetLibraryStatusAsync())!.Reading);
            var refused = await Assert.ThrowsAsync<MelodexApiException>(() => Api.CreateRequestAsync(new CreateRequest(RequestKind.Album, album.Id)));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.Status);
            Assert.Contains("scan", refused.Message);
        }
        Assert.False((await Api.GetLibraryStatusAsync())!.Reading);
    }

    [TestLibraryFact]
    public async Task A_missing_album_is_fetched_track_by_track_and_shows_as_owned()
    {
        await fixture.AddSourceAsync(new FakeSource("tracks"));
        var album = await fixture.AlbumAsync("Daft Punk", "Human After All");
        Assert.Equal(AlbumStatus.Missing, album.Status);

        var request = await Api.CreateRequestAsync(new CreateRequest(RequestKind.Album, album.Id));
        var done = await fixture.WaitAsync(request.Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(album.TotalTracks, done.Children!.Count);
        Assert.All(done.Children, c => Assert.Equal(RequestState.InLibrary, c.State));
        Assert.Equal(AlbumStatus.Complete, (await fixture.AlbumAsync("Daft Punk", "Human After All")).Status);

        // Every track was filed against one edition, pinned to its MusicBrainz release.
        var releases = fixture.Importer.Requests.Where(r => r.Release.Title.StartsWith("Human After All")).Select(r => r.Release.MusicBrainzId).Distinct().ToList();
        Assert.Single(releases);
        Assert.All(fixture.Importer.Requests.SelectMany(r => r.Files), f => Assert.NotEmpty(f.RecordingId));
    }

    [TestLibraryFact]
    public async Task A_plugin_that_fetches_albums_gets_the_whole_album_in_one_job()
    {
        var source = new FakeSource("albums", albums: true);
        await fixture.AddSourceAsync(source);
        var album = await fixture.AlbumAsync("Portishead", "Third");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Album, album.Id))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Empty(done.Children!);
        Assert.Single(done.Jobs!);
        Assert.Equal(1, source.Fetches);
        Assert.Equal(AlbumStatus.Complete, (await fixture.AlbumAsync("Portishead", "Third")).Status);
    }

    [TestLibraryFact]
    public async Task An_edition_MusicBrainz_doesnt_have_is_filed_as_tagged()
    {
        await fixture.AddSourceAsync(new FakeSource("tracks"));
        // The last missing song; another test asks for the first.
        var album = await fixture.AlbumAsync("Portishead", "Roseland NYC Live");
        var track = Guid.Empty;
        foreach (var edition in (await Api.GetAlbumAsync(album.Id))!.Editions)
            if ((await Api.GetAlbumAsync(album.Id, edition.Id))!.Tracks.LastOrDefault(t => t.Status == TrackStatus.Missing) is { } missing)
                track = missing.TrackId;
        var albumId = album.Id;
        // As if only a later catalogue, such as Deezer, listed its editions.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>();
            var editions = db.Editions.Where(e => e.AlbumId == albumId).Select(e => e.Id).ToList();
            await db.Crosswalk.Where(m => m.EntityKind == Melodex.Core.Data.EntityKind.Edition && editions.Contains(m.EntityId)
                                          && m.Provider == Contracts.Catalogue.Providers.MusicBrainz).ExecuteDeleteAsync();
        }

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Contains(done.Jobs!, j => j.Log.Contains("as tagged"));
        var import = fixture.Importer.Requests.Last(r => r.Files.Any(f => done.Title.StartsWith(f.Title)));
        Assert.Null(import.Release.MusicBrainzId);
    }

    [TestLibraryFact]
    public async Task A_missing_track_of_an_owned_album_joins_the_files_you_have()
    {
        await fixture.AddSourceAsync(new FakeSource("tracks"));
        var album = await fixture.AlbumAsync("Daft Punk", "Random Access Memories");
        Assert.Equal(AlbumStatus.Partial, album.Status);

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Album, album.Id))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        var track = Assert.Single(done.Children!);
        var import = fixture.Importer.Requests.Last(r => r.Files.Any(f => track.Title.StartsWith(f.Title)));
        Assert.StartsWith("Daft Punk/Random Access Memories (2013)/Disc 2", import.DestinationDirectory);
        Assert.Equal(AlbumStatus.Complete, (await fixture.AlbumAsync("Daft Punk", "Random Access Memories")).Status);
    }

    [TestLibraryFact]
    public async Task A_file_of_the_wrong_length_is_rejected_and_the_next_candidate_tried()
    {
        var source = new FakeSource("sloppy") { OffBySeconds = i => i == 0 ? 45 : 1 };
        await fixture.AddSourceAsync(source);
        var track = await MissingTrackAsync("Portishead", "Roseland NYC Live");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(2, done.Jobs!.Count);
        Assert.Equal(Verification.DurationMismatch, done.Jobs[0].Verification);
        Assert.Equal(Verification.Passed, done.Jobs[1].Verification);
    }

    [TestLibraryFact]
    public async Task When_the_first_plugin_fails_the_next_in_the_order_gets_the_request()
    {
        var broken = new FakeSource("broken") { OffBySeconds = _ => 90 };
        var good = new FakeSource("good");
        await fixture.AddSourceAsync(broken);
        await fixture.AddSourceAsync(good);
        var track = await MissingTrackAsync("Portishead", "Dummy");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(["broken", "broken", "good"], done.Jobs!.Select(j => j.Plugin));
    }

    [TestLibraryFact]
    public async Task A_slow_fetch_runs_past_the_limit_while_it_moves_and_is_cancelled_once_it_stalls()
    {
        // 80 steps of 20 ms is longer than the fixture's one-second limit; then it stays at 80 %.
        var stalls = new FakeSource("stalls") { Candidates = 1, ProgressSteps = Enumerable.Range(1, 80).Select(i => i / 100.0).ToList() };
        var good = new FakeSource("good");
        await fixture.AddSourceAsync(stalls);
        await fixture.AddSourceAsync(good);
        var track = await MissingTrackAsync("Portishead", "Magic Doors");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(["stalls", "good"], done.Jobs!.Select(j => j.Plugin));
        Assert.Contains("made no progress for 1 seconds at 80", done.Jobs![0].Log);
        Assert.Equal(1, stalls.Cancels);
    }

    [TestLibraryFact]
    public async Task A_request_cancelled_while_its_fetch_reports_progress_stays_cancelled()
    {
        var slow = new FakeSource("slow") { Candidates = 1, ProgressSteps = Enumerable.Range(1, 1000).Select(i => i / 1000.0).ToList() };
        await fixture.AddSourceAsync(slow);
        var track = await MissingTrackAsync("Portishead", "Magic Doors");
        var id = (await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id;
        while ((await Api.GetRequestAsync(id))!.Detail?.Contains('%') != true)
            await Task.Delay(20);

        await Api.CancelRequestAsync(id);
        await Task.Delay(300);

        var done = (await Api.GetRequestAsync(id))!;
        Dump(done);
        Assert.Equal(RequestState.Cancelled, done.State);
        Assert.Equal(JobState.Cancelled, done.Jobs!.Single().State);
        Assert.Equal(1, slow.Cancels);
    }

    [TestLibraryFact]
    public async Task When_every_plugin_fails_the_request_needs_review_and_can_be_retried()
    {
        var source = new FakeSource("nothing") { Finds = false };
        await fixture.AddSourceAsync(source);
        var track = await MissingTrackAsync("Portishead", "Portishead");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);
        Assert.Equal(RequestState.NeedsReview, done.State);
        Assert.Contains("found nothing", done.Detail);

        source.Finds = true;
        await Api.RetryRequestAsync(done.Id);
        done = await fixture.WaitAsync(done.Id);
        Dump(done);
        Assert.Equal(RequestState.InLibrary, done.State);
    }

    [TestLibraryFact]
    public async Task A_candidate_picked_from_an_earlier_search_is_fetched_without_searching_again()
    {
        // Only the third candidate is the right length, and only two are tried per plugin.
        var source = new FakeSource("picky") { OffBySeconds = i => i == 2 ? 0 : 60 };
        await fixture.AddSourceAsync(source);
        var track = await MissingTrackAsync("Portishead", "Portishead");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Assert.Equal(RequestState.NeedsReview, done.State);
        var job = done.Jobs![0];
        Assert.Equal(3, job.Candidates.Count);

        var searches = source.Searches;
        await Api.ChooseCandidateAsync(done.Id, new ChooseCandidate(job.Id, job.Candidates[2].CandidateId));
        done = await fixture.WaitAsync(done.Id);
        Dump(done);
        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(searches, source.Searches);
        Assert.Equal(Verification.Passed, done.Jobs!.Last().Verification);
    }

    [TestLibraryFact]
    public async Task Approving_a_review_suggestion_matches_the_file()
    {
        var item = (await Api.GetReviewAsync()).First(i => i.Suggestion is not null);
        await Api.MatchFileAsync(item.FileId, new MatchFile(item.Suggestion!.TrackId));
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        if (item.Suggestion.AlbumId is { } albumId)
            Assert.Contains((await Api.GetAlbumAsync(albumId, item.Suggestion.EditionId))!.Tracks,
                t => t.TrackId == item.Suggestion.TrackId && t.Status == TrackStatus.Owned);
    }

    [TestLibraryFact]
    public async Task Asking_twice_returns_the_same_request()
    {
        await fixture.AddSourceAsync(new FakeSource("slow") { Finds = false });
        var album = await fixture.AlbumAsync("Portishead", "Dummy");
        var first = await Api.CreateRequestAsync(new CreateRequest(RequestKind.Artist, album.ArtistId));
        var second = await Api.CreateRequestAsync(new CreateRequest(RequestKind.Artist, album.ArtistId));
        Assert.Equal(first.Id, second.Id);
        await Api.CancelRequestAsync(first.Id);
        Assert.Equal(RequestState.Cancelled, (await Api.GetRequestAsync(first.Id))!.State);
    }

    [TestLibraryFact]
    public async Task Without_retrieval_plugins_a_request_fails_and_says_to_add_one()
    {
        var track = await MissingTrackAsync("Daft Punk", "Homework");
        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Assert.Equal(RequestState.Failed, done.State);
        Assert.Contains("Add one under Settings → Plugins", done.Detail);
        await Api.CancelRequestAsync(done.Id);
    }

    /// <summary>A track of this album you have no copy of, on any of its editions.</summary>
    private async Task<Guid> MissingTrackAsync(string artist, string title)
    {
        var album = await fixture.AlbumAsync(artist, title);
        foreach (var edition in (await Api.GetAlbumAsync(album.Id))!.Editions)
            if ((await Api.GetAlbumAsync(album.Id, edition.Id))!.Tracks.FirstOrDefault(t => t.Status == TrackStatus.Missing) is { } missing)
                return missing.TrackId;
        throw new InvalidOperationException($"Every track of {title} is owned.");
    }

    private void Dump(RequestInfo request, string indent = "")
    {
        output.WriteLine($"{indent}{request.Title}: {request.State} ({request.Detail})");
        foreach (var job in request.Jobs ?? [])
            output.WriteLine($"{indent}  job {job.Attempt} {job.Plugin}: {job.State} {job.Verification} {job.VerificationDetail}\n{indent}    {job.Log.Replace("\n", $"\n{indent}    ")}");
        foreach (var child in request.Children ?? [])
            output.WriteLine($"{indent}  - {child.Title}: {child.State} ({child.Detail})");
    }
}
