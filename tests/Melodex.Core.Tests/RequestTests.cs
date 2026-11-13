using System.Text.Json;
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
                    // Long enough that a pause on a slow runner isn't taken for a stalled fetch.
                    o.FetchTimeout = TimeSpan.FromSeconds(3);
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
    public async Task The_reflink_check_answers_for_the_plugin_and_the_import_worker_together()
    {
        var source = new FakeSource("linked");
        var plugin = await fixture.AddSourceAsync(source);
        var form = JsonDocument.Parse("""{"format":"wav"}""").RootElement;

        var both = await Api.RunPluginCheckAsync(plugin.Id, "reflinks", new RunPluginCheck(form));
        Assert.True(both.Passed);
        Assert.Equal("From qBittorrent's downloads to staging, files can share their disk space. From staging to your music library, they can too.", both.Message);
        Assert.Equal("wav", source.CheckedWith!.Value.GetProperty("format").GetString());

        fixture.Importer.Reflinks = new ReflinkCheck(false, "they're on different filesystems");
        try
        {
            var half = await Api.RunPluginCheckAsync(plugin.Id, "reflinks", new RunPluginCheck());
            Assert.False(half.Passed);
            Assert.EndsWith("From staging to your music library, files can't share their disk space: they're on different filesystems.", half.Message);
        }
        finally
        {
            fixture.Importer.Reflinks = new ReflinkCheck(true);
        }
        await Assert.ThrowsAsync<MelodexApiException>(() => Api.RunPluginCheckAsync(plugin.Id, "nothing", new RunPluginCheck()));
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
    public async Task Every_plugin_is_searched_and_when_a_candidate_fails_the_next_best_of_any_plugin_is_tried()
    {
        var broken = new FakeSource("broken") { OffBySeconds = _ => 90 };
        var good = new FakeSource("good");
        await fixture.AddSourceAsync(broken);
        await fixture.AddSourceAsync(good);
        var track = await MissingTrackAsync("Portishead", "Dummy");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        // Both are as sure and as good, so their best candidates come before either's second.
        Assert.Equal(["broken", "good"], done.Jobs!.Select(j => j.Plugin));
        Assert.Equal(1, good.Searches);
    }

    [TestLibraryFact]
    public async Task The_best_quality_the_profile_allows_is_fetched_whichever_plugin_has_it()
    {
        var lossy = new FakeSource("lossy") { Format = "mp3", BitrateKbps = 320 };
        var lossless = new FakeSource("lossless") { Format = "flac", BitrateKbps = null };
        await fixture.AddSourceAsync(lossy);
        await fixture.AddSourceAsync(lossless);
        var track = await MissingTrackAsync("Daft Punk", "Random Access Memories");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(["lossless"], done.Jobs!.Select(j => j.Plugin));
        // It said FLAC; the file is raw PCM, which is read and noted.
        Assert.Contains("Said to be FLAC, but it's PCM", done.Jobs![0].Log);
    }

    [TestLibraryFact]
    public async Task A_file_in_a_quality_the_profile_turns_off_is_refused_whatever_it_was_said_to_be()
    {
        var profile = await Api.GetQualityProfileAsync();
        await Api.SaveQualityProfileAsync(profile with
        {
            Steps = profile.Ordered().Select(s => new ProfileStep(s.Step.Key, s.Allowed && s.Step.Codec != CodecFamily.Uncompressed)).ToList(),
        });
        try
        {
            var source = new FakeSource("saysflac") { Format = "flac", BitrateKbps = null, Candidates = 1 };
            await fixture.AddSourceAsync(source);
            var track = await MissingTrackAsync("Daft Punk", "Human After All");

            var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
            Dump(done);
            await Api.CancelRequestAsync(done.Id);

            Assert.Equal(RequestState.NeedsReview, done.State);
            Assert.Equal(Verification.QualityRefused, done.Jobs!.Single().Verification);
            Assert.Contains("which the quality profile turns off", done.Jobs![0].VerificationDetail);
        }
        finally
        {
            await Api.SaveQualityProfileAsync(profile);
        }
    }

    [TestLibraryFact]
    public async Task Lossless_whose_highs_stop_as_an_mp3s_do_is_refused_and_the_next_candidate_fetched()
    {
        // Said to be a rip with its log, which puts it first; its spectrum says otherwise.
        var fake = new FakeSource("fakeflac") { Format = "flac", BitrateKbps = null, Candidates = 1, Vouched = true, FileName = "fake.wav" };
        var real = new FakeSource("realflac") { Format = "flac", BitrateKbps = null, Candidates = 1, FileName = "real.wav" };
        fixture.Importer.Cutoffs["fake.wav"] = 16570;
        await fixture.AddSourceAsync(fake);
        await fixture.AddSourceAsync(real);
        var track = await MissingTrackAsync("Daft Punk", "Homework");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(["fakeflac", "realflac"], done.Jobs!.Select(j => j.Plugin));
        Assert.Equal(Verification.SuspectLossless, done.Jobs![0].Verification);
        Assert.EndsWith("but its highs stop at 16.6 kHz: it seems to be lossy, low quality", done.Jobs[0].VerificationDetail);
        // Both were checked, in staging.
        Assert.Contains(fixture.Importer.Spectra, r => r.Files.Any(f => f.Root == SpectrumFile.Staging && f.Path.EndsWith("/real.wav")));
        using var scope = fixture.Factory.Services.CreateScope();
        var file = await scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>().LibraryFiles.SingleAsync(f => f.TrackId == track);
        Assert.Null(file.CutoffHz);
        Assert.NotNull(file.SpectrumCheckedAt);
    }

    [TestLibraryFact]
    public async Task A_copy_turned_away_for_seeming_lossy_can_be_kept_anyway()
    {
        await fixture.AddSourceAsync(new FakeSource("onlyfake") { Format = "flac", BitrateKbps = null, Candidates = 1, FileName = "onlyfake.wav" });
        fixture.Importer.Cutoffs["onlyfake.wav"] = 16570;
        try
        {
            var track = await MissingTrackAsync("Daft Punk", "Homework");
            var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
            Assert.Equal(RequestState.NeedsReview, done.State);
            var refused = Assert.Single(done.Jobs!);
            Assert.Equal(Verification.SuspectLossless, refused.Verification);

            // Kept as it is, without fetching again, and counted as what it seems.
            await Api.KeepRequestAsync(done.Id, refused.Id);
            var kept = await fixture.WaitAsync(done.Id);
            Dump(kept);
            Assert.Equal(RequestState.InLibrary, kept.State);
            Assert.Single(kept.Jobs!);
            Assert.Contains("Keeping it anyway", kept.Jobs![0].Log);
            using var scope = fixture.Factory.Services.CreateScope();
            var file = await scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>().LibraryFiles.SingleAsync(f => f.TrackId == track);
            Assert.Equal(16570, file.CutoffHz);
            Assert.True(file.Quality.Suspect);

            // Once kept, there's nothing left to keep.
            await Assert.ThrowsAnyAsync<Exception>(() => Api.KeepRequestAsync(done.Id, refused.Id));
        }
        finally
        {
            fixture.Importer.Cutoffs.TryRemove("onlyfake.wav", out _);
        }
    }

    [TestLibraryFact]
    public async Task Lossless_that_seems_lossy_can_be_kept_as_what_it_seems()
    {
        var profile = await Api.GetQualityProfileAsync();
        await Api.SaveQualityProfileAsync(profile with { Suspect = SuspectLossless.Keep });
        try
        {
            var source = new FakeSource("seemslossy") { Format = "flac", BitrateKbps = null, Candidates = 1, FileName = "seems.wav" };
            fixture.Importer.Cutoffs["seems.wav"] = 19500;
            await fixture.AddSourceAsync(source);
            var track = await MissingTrackAsync("Daft Punk", "Homework");

            var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
            Dump(done);

            Assert.Equal(RequestState.InLibrary, done.State);
            Assert.Contains("seems lossy: its highs stop at 19.5 kHz", done.Jobs!.Single().Log);
            using var scope = fixture.Factory.Services.CreateScope();
            var file = await scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>().LibraryFiles.SingleAsync(f => f.TrackId == track);
            Assert.Equal(19500, file.CutoffHz);
            Assert.Equal(QualityLevel.Good, file.Quality.Level);
        }
        finally
        {
            await Api.SaveQualityProfileAsync(profile);
        }
    }

    [TestLibraryFact]
    public async Task The_librarys_lossless_files_are_checked_and_those_that_seem_lossy_listed()
    {
        fixture.Importer.Cutoffs["04. Da Funk.flac"] = 16000;
        try
        {
            var task = await Api.CheckSpectraAsync(again: true);
            for (var deadline = DateTime.UtcNow.AddMinutes(1); task.Active && DateTime.UtcNow < deadline; task = (await Api.GetTaskAsync(task.Id))!)
                await Task.Delay(50);
            output.WriteLine(task.Progress);

            var status = await Api.GetSpectrumStatusAsync();
            output.WriteLine($"{status.Checked} of {status.Lossless}; spectra asked: {string.Join(", ", fixture.Importer.Spectra.SelectMany(r => r.Files).Select(f => f.Path).Take(5))}");
            Assert.Equal(status.Lossless, status.Checked);
            var suspect = Assert.Single(status.Suspects);
            Assert.EndsWith("04. Da Funk.flac", suspect.Path);
            Assert.Equal((QualityLevel.Low, true), (suspect.Quality.Level, suspect.Quality.Suspect));
            Assert.Contains("seems lossy: its highs stop at 16.0 kHz", suspect.Quality.Details);
            // Only the library was read, and only its lossless files.
            Assert.All(fixture.Importer.Spectra.SelectMany(r => r.Files).Where(f => f.Root == SpectrumFile.Library),
                f => Assert.True(f.Path.EndsWith(".flac") || f.Path.EndsWith(".wav"), f.Path));

            // Trusted, it's lossless again, and checking everything again leaves it so.
            await Api.TrustLosslessAsync([suspect.Id]);
            Assert.Empty((await Api.GetSpectrumStatusAsync()).Suspects);
            task = await Api.CheckSpectraAsync(again: true);
            for (var deadline = DateTime.UtcNow.AddMinutes(1); task.Active && DateTime.UtcNow < deadline; task = (await Api.GetTaskAsync(task.Id))!)
                await Task.Delay(50);
            Assert.Empty((await Api.GetSpectrumStatusAsync()).Suspects);
        }
        finally
        {
            fixture.Importer.Cutoffs.TryRemove("04. Da Funk.flac", out _);
            using var scope = fixture.Factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>().LibraryFiles
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.TrustedAt, (DateTime?)null));
        }
    }

    [TestLibraryFact]
    public async Task A_slow_fetch_runs_past_the_limit_while_it_moves_and_is_cancelled_once_it_stalls()
    {
        // 200 steps of at least 20 ms is longer than the fixture's three-second limit; then it stays at 80 %.
        var stalls = new FakeSource("stalls") { Candidates = 1, ProgressSteps = Enumerable.Range(1, 200).Select(i => i / 250.0).ToList() };
        var good = new FakeSource("good");
        await fixture.AddSourceAsync(stalls);
        await fixture.AddSourceAsync(good);
        var track = await MissingTrackAsync("Portishead", "Magic Doors");

        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Dump(done);

        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Equal(["stalls", "good"], done.Jobs!.Select(j => j.Plugin));
        Assert.Contains("made no progress for 3 seconds at 80", done.Jobs![0].Log);
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

    [TestLibraryFact]
    public async Task A_request_that_failed_can_be_dismissed()
    {
        var track = await MissingTrackAsync("Daft Punk", "Homework");
        var done = await fixture.WaitAsync((await Api.CreateRequestAsync(new CreateRequest(RequestKind.Track, track))).Id);
        Assert.Equal(RequestState.Failed, done.State);

        await Api.CancelRequestAsync(done.Id);

        var dismissed = (await Api.GetRequestAsync(done.Id))!;
        Assert.Equal(RequestState.Cancelled, dismissed.State);
        Assert.Equal("Dismissed", dismissed.Detail);
    }

    [TestLibraryFact]
    public async Task A_request_that_stopped_short_is_settled_once_its_song_is_here_or_asked_for_again()
    {
        var (owned, scope) = await OwnedAsync("Discovery (2001)/10 Voyager.mp3");
        var missing = await MissingTrackAsync("Daft Punk", "Homework");
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>();
            Melodex.Core.Data.Request Stuck(Guid track, DateTime at) => new()
            {
                TargetKind = Melodex.Core.Data.EntityKind.Track, TargetId = track, Title = "Song", Requester = "test",
                Mode = Melodex.Core.Data.RequestMode.Permanent, Status = Melodex.Core.Data.RequestStatus.NeedsReview, CreatedAt = at,
            };
            var now = DateTime.UtcNow;
            // In the library by other means; asked for twice and failed both times; and still missing.
            var here = Stuck(owned.TrackId!.Value, now);
            var older = Stuck(missing, now.AddMinutes(-5));
            var newer = Stuck(missing, now);
            db.Requests.AddRange(here, older, newer);
            await db.SaveChangesAsync();

            await scope.ServiceProvider.GetRequiredService<RequestService>().SettleAsync(CancellationToken.None);

            foreach (var request in new[] { here, older, newer })
                await db.Entry(request).ReloadAsync();
            Assert.Equal((Melodex.Core.Data.RequestStatus.InLibrary, "In the library now"), (here.Status, here.Detail));
            Assert.Equal((Melodex.Core.Data.RequestStatus.Cancelled, "Asked for again"), (older.Status, older.Detail));
            Assert.Equal(Melodex.Core.Data.RequestStatus.NeedsReview, newer.Status);
            db.Requests.RemoveRange(here, older, newer);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>An owned file of the test library, by the end of its path, with the database to look at it.</summary>
    private async Task<(Melodex.Core.Data.LibraryFile File, IServiceScope Scope)> OwnedAsync(string pathEnd)
    {
        var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>();
        var file = await db.LibraryFiles.SingleAsync(f => f.Path.EndsWith(pathEnd));
        Assert.NotNull(file.TrackId);
        return (file, scope);
    }

    private async Task<RequestInfo> UpgradeAsync(Guid fileId, Guid trackId)
    {
        var found = await Api.SearchUpgradesAsync(new FormatScope(FormatScopeKind.Track, trackId));
        Assert.Equal((1, 1), (found.Upgradable, found.Queued));
        using var scope = fixture.Factory.Services.CreateScope();
        var request = await scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>().Requests
            .Where(r => r.UpgradesFileId == fileId).OrderByDescending(r => r.CreatedAt).FirstAsync();
        var done = await fixture.WaitAsync(request.Id);
        Dump(done);
        Assert.True(done.Upgrade);
        return done;
    }

    [TestLibraryFact]
    public async Task An_owned_mp3_is_upgraded_in_its_place_its_old_file_held_and_put_back()
    {
        await fixture.AddSourceAsync(new FakeSource("better") { Candidates = 1, BitrateKbps = null, FileName = "better.wav" });
        var (file, scope) = await OwnedAsync("Discovery (2001)/10 Voyager.mp3");
        using var owned = scope;
        var (fileId, trackId, oldPath) = (file.Id, file.TrackId!.Value, file.Path);
        try
        {
            var done = await UpgradeAsync(fileId, trackId);

            Assert.Equal(RequestState.InLibrary, done.State);
            Assert.Matches("^Upgraded from MP3 [0-9]+ to PCM [0-9/.]+\\. ", done.Detail!);
            // The same library file, in the old one's place less its extension; the old one is held in staging.
            var replace = Assert.Single(fixture.Importer.Replaces, r => r.Path == oldPath);
            Assert.EndsWith("/better.wav", replace.Staged);
            Assert.StartsWith("held/", replace.Hold);
            var db = scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>();
            var upgraded = await db.LibraryFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
            Assert.Equal(Path.ChangeExtension(oldPath, ".wav"), upgraded.Path);
            Assert.Equal((trackId, "pcm"), (upgraded.TrackId!.Value, upgraded.Codec));
            Assert.False(File.Exists(Path.Combine(fixture.Music, oldPath)));
            var held = Assert.Single(await Api.GetHeldFilesAsync(trackId));
            Assert.Equal((oldPath, "mp3", "pcm"), (held.Path, held.Was.Codec, held.Now?.Codec));
            Assert.True(File.Exists(Path.Combine(fixture.Staging, $"{replace.Hold}/10 Voyager.mp3")));
            Assert.Contains(await Api.GetHeldFilesAsync(), h => h.Id == held.Id);

            // Put back, the better copy goes, and the song isn't upgraded again.
            await Api.RestoreHeldFileAsync(held.Id, never: true);
            var restored = await db.LibraryFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
            Assert.Equal((oldPath, "mp3"), (restored.Path, restored.Codec));
            Assert.True(File.Exists(Path.Combine(fixture.Music, oldPath)));
            Assert.False(File.Exists(Path.Combine(fixture.Music, upgraded.Path)));
            Assert.Empty(await Api.GetHeldFilesAsync(trackId));
            Assert.Equal(QualityOverrideKind.NeverUpgrade, (await Api.GetQualitySettingAsync("tracks", trackId, null))!.Kind);
            Assert.Equal(0, (await Api.SearchUpgradesAsync(new FormatScope(FormatScopeKind.Track, trackId))).Upgradable);
        }
        finally
        {
            await Api.SaveQualityOverrideAsync("tracks", trackId, new QualityOverride(QualityOverrideKind.Inherit));
        }
    }

    [TestLibraryFact]
    public async Task An_upgrade_fetches_only_what_may_be_better_and_keeps_only_what_is()
    {
        var (file, scope) = await OwnedAsync("Discovery (2001)/11 Veridis Quo.mp3");
        using var owned = scope;
        var (fileId, trackId, oldPath) = (file.Id, file.TrackId!.Value, file.Path);

        // Said to be no better: not even fetched.
        var worse = new FakeSource("worse") { Format = "mp3", BitrateKbps = 8, Candidates = 1 };
        await fixture.AddSourceAsync(worse);
        var done = await UpgradeAsync(fileId, trackId);
        Assert.Equal(RequestState.InLibrary, done.State);
        Assert.Matches("^Nothing better found: worse found nothing better than the MP3 [0-9]+ you have", done.Detail!);
        Assert.Equal(0, worse.Fetches);
        await fixture.RemoveSourcesAsync();

        // Said to be lossless, but it seems to be as low as what's there: fetched, checked and not kept.
        var profile = await Api.GetQualityProfileAsync();
        var lowToo = profile.Ordered().Select(s => new ProfileStep(s.Step.Key, s.Allowed || s.Step == new QualityStep(CodecFamily.Mp3, QualityLevel.Low))).ToList();
        await Api.SaveQualityProfileAsync(profile with { Suspect = SuspectLossless.Keep, Steps = lowToo });
        fixture.Importer.Cutoffs["seemslow.wav"] = 15000;
        try
        {
            await fixture.AddSourceAsync(new FakeSource("seemslow") { Format = "flac", BitrateKbps = null, Candidates = 1, FileName = "seemslow.wav" });
            done = await UpgradeAsync(fileId, trackId);
            Assert.Equal(RequestState.InLibrary, done.State);
            Assert.Equal(Verification.NotBetter, done.Jobs!.Single().Verification);
            Assert.Matches("^It's PCM [0-9/.]+, no better than the MP3 [0-9]+ you have$", done.Jobs![0].VerificationDetail!);
            Assert.True(File.Exists(Path.Combine(fixture.Music, oldPath)));
            Assert.Empty(await Api.GetHeldFilesAsync(trackId));
        }
        finally
        {
            fixture.Importer.Cutoffs.TryRemove("seemslow.wav", out _);
            await Api.SaveQualityProfileAsync(profile);
        }
    }

    [TestLibraryFact]
    public async Task Songs_never_to_be_upgraded_and_songs_set_aside_are_not_upgradable()
    {
        var album = await fixture.AlbumAsync("Daft Punk", "Discovery");
        var before = await Api.SearchUpgradesAsync(new FormatScope(FormatScopeKind.Album, album.Id));
        var (file, scope) = await OwnedAsync("Discovery (2001)/06 Nightvision.mp3");
        using var owned = scope;
        var trackId = file.TrackId!.Value;
        await Api.SaveQualityOverrideAsync("tracks", trackId, new QualityOverride(QualityOverrideKind.NeverUpgrade));
        try
        {
            var after = await Api.SearchUpgradesAsync(new FormatScope(FormatScopeKind.Album, album.Id));
            Assert.Equal(before.Upgradable - 1, after.Upgradable);
        }
        finally
        {
            await Api.SaveQualityOverrideAsync("tracks", trackId, new QualityOverride(QualityOverrideKind.Inherit));
            using var cancel = fixture.Factory.Services.CreateScope();
            var db = cancel.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>();
            foreach (var id in await db.Requests.Where(r => r.UpgradesFileId != null && r.Status != Melodex.Core.Data.RequestStatus.Cancelled).Select(r => r.Id).ToListAsync())
                await fixture.WaitAsync(id);
        }
    }

    [TestLibraryFact]
    public async Task Upgrades_warn_without_a_media_server_that_keeps_stars_and_plays()
    {
        var status = await Api.GetUpgradesAsync();
        Assert.StartsWith("No media server is connected", status.Warning);
        Assert.True(status.Settings.Scheduled);
        Assert.Equal([DayOfWeek.Monday], status.Settings.Days);
        Assert.Equal((25, OldFiles.Hold, 14), (status.Settings.PerRun, status.Settings.OldFiles, status.Settings.HoldDays));
        Assert.NotNull(status.NextRun);
    }

    [TestLibraryFact]
    public async Task Scheduled_upgrades_ask_for_no_more_than_the_limit_and_stop_when_the_window_closes()
    {
        var before = await Api.GetUpgradesAsync();
        var now = DateTime.UtcNow;
        // All of today, in UTC.
        await Api.SaveUpgradeSettingsAsync(before.Settings with
        {
            Days = [now.DayOfWeek], From = new(0, 0), Until = new(0, 0), TimeZone = "UTC", PerRun = 1
        });
        using var scope = fixture.Factory.Services.CreateScope();
        var upgrades = scope.ServiceProvider.GetRequiredService<Upgrades>();
        var db = scope.ServiceProvider.GetRequiredService<Melodex.Core.Data.MelodexDbContext>();
        try
        {
            Assert.True((await Api.GetUpgradesAsync()).Running);
            Assert.Equal(1, await upgrades.RunScheduledAsync(now, default));
            Assert.Equal(0, await upgrades.RunScheduledAsync(now.AddMinutes(1), default));
            await Api.SaveUpgradeSettingsAsync((await Api.GetUpgradesAsync()).Settings with { PerRun = null });
            Assert.Null((await Api.GetUpgradesAsync()).Settings.PerRun);

            // Two days on, the window has closed: nothing more is asked for, and any not started never will be.
            Assert.Equal(0, await upgrades.RunScheduledAsync(now.AddDays(2), default));
            Assert.False(await db.Requests.AnyAsync(r => r.Requester == Upgrades.ScheduledRequester && r.Status == Melodex.Core.Data.RequestStatus.Queued));
        }
        finally
        {
            await Api.SaveUpgradeSettingsAsync(before.Settings);
            foreach (var id in await db.Requests.Where(r => r.Requester == Upgrades.ScheduledRequester && r.Status != Melodex.Core.Data.RequestStatus.Cancelled).Select(r => r.Id).ToListAsync())
                await fixture.WaitAsync(id);
        }
    }

    [Theory]
    // Monday 01:00–06:00 in London, which is UTC in winter and an hour ahead in summer.
    [InlineData("2026-11-09T03:00:00Z", true)]
    [InlineData("2026-11-09T06:00:00Z", false)]
    [InlineData("2026-11-10T03:00:00Z", false)]
    [InlineData("2026-07-06T00:30:00Z", true)]
    [InlineData("2026-07-06T05:30:00Z", false)]
    public void An_upgrade_window_is_in_its_own_time_zone(string at, bool open)
    {
        var settings = new UpgradeSettings { TimeZone = "Europe/London" };
        Assert.Equal(open, UpgradeWindows.Current(settings, DateTime.Parse(at).ToUniversalTime()) is not null);
    }

    [Fact]
    public void An_upgrade_window_can_run_past_midnight_into_the_next_day()
    {
        // Fridays and Saturdays, 22:00 until 04:00 the next morning.
        var settings = new UpgradeSettings { Days = [DayOfWeek.Friday, DayOfWeek.Saturday], From = new(22, 0), Until = new(4, 0) };
        var saturdayMorning = new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal((new DateTime(2026, 11, 13, 22, 0, 0), new DateTime(2026, 11, 14, 4, 0, 0)), UpgradeWindows.Current(settings, saturdayMorning));
        Assert.NotNull(UpgradeWindows.Current(settings, new DateTime(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc)));
        Assert.Null(UpgradeWindows.Current(settings, new DateTime(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 14, 22, 0, 0), UpgradeWindows.Next(settings, saturdayMorning));
        Assert.Equal(new DateTime(2026, 11, 20, 22, 0, 0), UpgradeWindows.Next(settings, new DateTime(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc)));
        Assert.Null(UpgradeWindows.Next(settings with { Days = [] }, saturdayMorning));
    }

    [Theory]
    [InlineData("0.64.2 (4d1e8c42)", "0.64.2", false)]
    [InlineData("0.55.0", "0.55.0", false)]
    [InlineData("v0.54.5", "0.54.5", true)]
    [InlineData("0.53", "0.53.0", true)]
    public void Navidrome_older_than_0_55_loses_what_an_upgraded_song_had(string reported, string version, bool loses)
    {
        var parsed = Upgrades.NavidromeVersion(reported);
        Assert.Equal(version, parsed?.ToString(3));
        Assert.Equal(loses, parsed < Upgrades.KeepingNavidrome);
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
