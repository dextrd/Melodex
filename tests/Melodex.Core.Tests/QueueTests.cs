using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Pausing, resuming, moving and cancelling requests and background tasks.</summary>
public sealed class QueueTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public QueueTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("Melodex:DataPath", _dataPath));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    // Paused, so the worker leaves them be while the test moves them about.
    private async Task<List<Guid>> AddRequestsAsync(params string[] titles)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var requests = titles.Select((title, i) => new Request
        {
            TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = title, Requester = "admin", Paused = true, QueueOrder = 100 + i,
        }).ToList();
        db.Requests.AddRange(requests);
        await db.SaveChangesAsync();
        return requests.Select(r => r.Id).ToList();
    }

    private async Task<List<string>> QueueAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        return await db.Requests.Where(r => r.ParentRequestId == null).OrderBy(r => r.QueueOrder).Select(r => r.Title).ToListAsync();
    }

    [Fact]
    public async Task Requests_move_before_another_or_to_the_end()
    {
        var ids = await AddRequestsAsync("A", "B", "C");

        await _api.MoveRequestAsync(ids[2], before: ids[0]);
        Assert.Equal(["C", "A", "B"], await QueueAsync());

        await _api.MoveRequestAsync(ids[2], before: null);
        Assert.Equal(["A", "B", "C"], await QueueAsync());

        await _api.MoveRequestAsync(ids[0], before: ids[2]);
        Assert.Equal(["B", "A", "C"], await QueueAsync());
    }

    [Fact]
    public async Task Parts_move_and_pause_with_their_request()
    {
        var ids = await AddRequestsAsync("Album", "Other");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            await db.Requests.Where(r => r.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.InProgress));
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = "Track", Requester = "admin", ParentRequestId = ids[0],
                QueueOrder = 100, Paused = true, Status = RequestStatus.Fetching,
            });
            await db.SaveChangesAsync();
        }

        await _api.MoveRequestAsync(ids[0], before: null);
        await _api.PauseRequestAsync(ids[0]);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var album = await db.Requests.SingleAsync(r => r.Id == ids[0]);
            var part = await db.Requests.SingleAsync(r => r.ParentRequestId == ids[0]);
            Assert.Equal(album.QueueOrder, part.QueueOrder);
            Assert.True(album.QueueOrder > (await db.Requests.SingleAsync(r => r.Id == ids[1])).QueueOrder);
            Assert.True(part.Paused);
            Assert.Equal(RequestStatus.Queued, part.Status);
            Assert.Equal("Paused", part.Detail);
        }

        Assert.True((await _api.ListRequestsAsync()).Single(r => r.Id == ids[0]).Paused);
    }

    [Fact]
    public async Task Pausing_a_request_mid_fetch_queues_it_again_and_closes_the_fetch()
    {
        var ids = await AddRequestsAsync("Fetching");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var request = await db.Requests.SingleAsync();
            request.Paused = false;
            request.Status = RequestStatus.Fetching;
            db.Jobs.Add(new Job { RequestId = request.Id, Status = JobStatus.Running });
            await db.SaveChangesAsync();
        }

        // What comes back is the request as it is now, not as it was loaded before the pause.
        var response = await _factory.CreateSignedInClient().PostAsync($"api/v1/requests/{ids[0]}/pause", null);
        Assert.Contains("\"paused\":true", await response.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var request = await db.Requests.Include(r => r.Jobs).SingleAsync();
            Assert.Equal(RequestStatus.Queued, request.Status);
            Assert.True(request.Paused);
            Assert.Equal(JobStatus.Cancelled, Assert.Single(request.Jobs).Status);
        }
        var finished = await Assert.ThrowsAsync<MelodexApiException>(async () =>
        {
            await _api.CancelRequestAsync(ids[0]);
            await _api.PauseRequestAsync(ids[0]);
        });
        Assert.Equal(HttpStatusCode.Conflict, finished.Status);
    }

    [Fact]
    public async Task Tasks_can_be_moved_paused_resumed_and_cancelled()
    {
        List<Guid> ids;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var tasks = new[] { "first", "second" }.Select((kind, i) => new BackgroundTask { Kind = kind, Paused = true, QueueOrder = i }).ToList();
            db.BackgroundTasks.AddRange(tasks);
            await db.SaveChangesAsync();
            ids = tasks.Select(t => t.Id).ToList();
        }

        await _api.MoveTaskAsync(ids[1], before: ids[0]);
        var queued = (await _api.ListTasksAsync()).Where(t => t.State == TaskState.Queued).OrderBy(t => t.QueueOrder).Select(t => t.Kind);
        Assert.Equal(["second", "first"], queued);

        await _api.CancelTaskAsync(ids[0]);
        var cancelled = await _api.GetTaskAsync(ids[0]);
        Assert.Equal(TaskState.Cancelled, cancelled!.State);
        Assert.False(cancelled.Active);
        var again = await Assert.ThrowsAsync<MelodexApiException>(() => _api.CancelTaskAsync(ids[0]));
        Assert.Equal(HttpStatusCode.Conflict, again.Status);

        // Resumed, the worker picks it up; it has no handler, so it fails rather than staying queued.
        await _api.ResumeTaskAsync(ids[1]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        TaskInfo? task;
        do
        {
            await Task.Delay(100);
            task = await _api.GetTaskAsync(ids[1]);
        } while (task!.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline);
        Assert.False(task.Paused);
        Assert.Equal(TaskState.Failed, task.State);
    }

    [Fact]
    public async Task A_paused_scan_lets_requests_run()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var reading = scope.ServiceProvider.GetRequiredService<Library.LibraryReading>();
        db.BackgroundTasks.Add(new BackgroundTask { Kind = Library.LibraryScanTask.TaskKind, Paused = true });
        await db.SaveChangesAsync();
        Assert.False(await reading.IsBusyAsync(db, CancellationToken.None));
    }
}
