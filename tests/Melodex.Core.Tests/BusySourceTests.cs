using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>
/// When MusicBrainz is overloaded, busy work goes to the back of the queue; a scan finishes anyway and leaves what it
/// couldn't match for the next one.
/// </summary>
public sealed class BusySourceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly MelodexClient _api;
    private int _requests;

    // How many requests MusicBrainz is busy for before it answers from the recorded fixtures.
    private int _busyFor = int.MaxValue;

    public BusySourceTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new BusyHandler(() => Interlocked.Increment(ref _requests) <= _busyFor)
                {
                    InnerHandler = new ReplayHandler(TestLibrary.MusicBrainzFixtures),
                };
                http.RateLimit = false;
                http.RetryDelay = TimeSpan.FromMilliseconds(1);
                http.UnavailablePause = TimeSpan.FromMilliseconds(1);
                http.LongUnavailablePause = TimeSpan.FromMilliseconds(1);
            })));
        _http = _factory.CreateSignedInClient();
        _api = new MelodexClient(_http);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    [TestLibraryFact]
    public async Task A_scan_while_musicbrainz_is_busy_finishes_and_leaves_files_for_the_next_scan()
    {
        var task = await ScanAsync();

        Assert.True(task.State == TaskState.Succeeded, task.Error);
        Assert.Contains("left to match next scan", task.Progress);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        // Only files that need no lookup, like one with no title tag, got anywhere.
        Assert.Equal(await db.LibraryFiles.CountAsync(), await db.LibraryFiles.CountAsync(f => f.MatchedAt == null || f.ReviewReason == "The file has no title tag."));

        // Busy groups went to the back of the queue, and after three long waits with nothing getting through it
        // stopped asking rather than trying every folder.
        var log = (await _api.GetLogAsync()).Entries;
        Assert.Contains(log, e => e.Message.Contains("goes to the back of the queue (attempt 1 of 3)"));
        Assert.Contains(log, e => e.Message.Contains("stops asking"));
        Assert.Equal(SourceBackoff.InARowBeforeLongWait * SourceBackoff.LongWaitsBeforeStopping,
            log.Count(e => e.Message.Contains("goes to the back of the queue") || e.Message.Contains("Gave up on")));
        Assert.True(_requests < 150, $"{_requests} requests");
    }

    [TestLibraryFact]
    public async Task A_folder_musicbrainz_was_busy_for_is_matched_later_in_the_same_scan()
    {
        // Busy through every retry of the first request, then fine.
        _busyFor = 6;

        var task = await ScanAsync();

        Assert.True(task.State == TaskState.Succeeded, task.Error);
        var log = (await _api.GetLogAsync()).Entries;
        Assert.Contains(log, e => e.Message.Contains("goes to the back of the queue (attempt 1 of 3)"));
        Assert.DoesNotContain(log, e => e.Message.Contains("Gave up on"));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        Assert.Equal(0, await db.LibraryFiles.CountAsync(f => f.MatchedAt == null));
    }

    [Fact]
    public async Task A_request_that_needs_musicbrainz_while_its_busy_gets_503()
    {
        var response = await _http.GetAsync("/api/v1/catalogue/search?q=Portishead");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private async Task<TaskInfo> ScanAsync()
    {
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        return task;
    }

    private sealed class BusyHandler(Func<bool> busy) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!busy())
                return base.SendAsync(request, cancellationToken);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = new StringContent("""{"error":"The MusicBrainz web server is currently busy. Please try again later."}"""),
            });
        }
    }
}
