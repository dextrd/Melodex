using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Requests;

/// <summary>Creates, cancels and retries requests, and reports on them.</summary>
public sealed class RequestService(MelodexDbContext db, RequestSignal signal, RequestCancellations cancellations, LibraryReading reading)
{
    public static readonly RequestStatus[] Finished = [RequestStatus.InLibrary, RequestStatus.NeedsReview, RequestStatus.Failed, RequestStatus.Cancelled, RequestStatus.Playable];

    /// <summary>
    /// Queues a request, or returns the active one for the same target, whether asked for directly or as part of an
    /// artist or album. Null when the target isn't in the catalogue.
    /// </summary>
    public async Task<(Request? Request, bool Created)> CreateAsync(CreateRequest create, string requester, CancellationToken ct)
    {
        var kind = create.Kind switch
        {
            RequestKind.Artist => EntityKind.Artist,
            RequestKind.Album => EntityKind.Album,
            _ => EntityKind.Track,
        };
        var existing = await db.Requests
            .Where(r => r.TargetKind == kind && r.TargetId == create.Id && r.Mode == RequestMode.Permanent && !Finished.Contains(r.Status))
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return (existing, false);
        if (await reading.IsBusyAsync(db, ct))
            throw new LibraryBusyException();

        var title = await TitleAsync(kind, create.Id, ct);
        if (title is null)
            return (null, false);
        if (create.EditionId is { } editionId && !await EditionFitsAsync(kind, create.Id, editionId, ct))
            throw new ArgumentException("That edition isn't one of this album's, or doesn't have this track.");

        var request = new Request
        {
            TargetKind = kind,
            TargetId = create.Id,
            EditionId = kind == EntityKind.Artist ? null : create.EditionId,
            Title = title,
            Requester = requester,
            Mode = RequestMode.Permanent,
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return (request, true);
    }

    /// <summary>Cancels a request and its unfinished parts. False if it's already finished.</summary>
    public async Task<bool> CancelAsync(Request request, CancellationToken ct)
    {
        if (Finished.Contains(request.Status))
            return false;
        var ids = await SubtreeAsync(request.Id, ct);
        await db.Requests.Where(r => ids.Contains(r.Id) && !Finished.Contains(r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Cancelled).SetProperty(r => r.Detail, "Cancelled")
                .SetProperty(r => r.UpdatedAt, DateTime.UtcNow), ct);
        foreach (var id in ids)
            cancellations.Cancel(id);
        await UpdateAncestorsAsync(request.ParentRequestId, ct);
        return true;
    }

    /// <summary>Statuses of a request that's being worked on: searching through to telling the media server.</summary>
    public static readonly RequestStatus[] InFlight =
        [RequestStatus.Searching, RequestStatus.Fetching, RequestStatus.Verifying, RequestStatus.Importing, RequestStatus.Notifying];

    /// <summary>
    /// Holds a request and its unfinished parts back until resumed. Parts already fetching stop, and fetch again from
    /// the start when resumed. False if it's already finished.
    /// </summary>
    public async Task<bool> PauseAsync(Request request, CancellationToken ct)
    {
        if (Finished.Contains(request.Status))
            return false;
        var ids = await SubtreeAsync(request.Id, ct);
        await db.Requests.Where(r => ids.Contains(r.Id) && !Finished.Contains(r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Paused, true).SetProperty(r => r.UpdatedAt, DateTime.UtcNow), ct);
        // Stopped mid-fetch: back in the queue, to start that fetch again when resumed.
        await db.Requests.Where(r => ids.Contains(r.Id) && (InFlight.Contains(r.Status) || r.Status == RequestStatus.Queued))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Queued).SetProperty(r => r.Detail, "Paused"), ct);
        await db.Jobs.Where(j => ids.Contains(j.RequestId) && (j.Status == JobStatus.Running || j.Status == JobStatus.Queued))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.FinishedAt, DateTime.UtcNow)
                .SetProperty(j => j.Log, j => j.Log + "Paused\n"), ct);
        foreach (var id in ids)
            cancellations.Cancel(id);
        // The bulk updates skip the change tracker; forget what it holds so what's read next is current.
        db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>Lets a paused request and its parts go again, in their place in the queue.</summary>
    public async Task<bool> ResumeAsync(Request request, CancellationToken ct)
    {
        var ids = await SubtreeAsync(request.Id, ct);
        var resumed = await db.Requests.Where(r => ids.Contains(r.Id) && r.Paused)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Paused, false).SetProperty(r => r.UpdatedAt, DateTime.UtcNow)
                .SetProperty(r => r.Detail, r => r.Detail == "Paused" ? null : r.Detail), ct);
        signal.Wake();
        db.ChangeTracker.Clear();
        return resumed > 0;
    }

    /// <summary>
    /// Moves a request, with its parts, to just before another in the queue, or to the end when there's none. Only
    /// requests asked for directly are ordered; listen-now and library requests each have their own queue.
    /// </summary>
    public async Task<bool> MoveAsync(Request request, Guid? before, CancellationToken ct)
    {
        if (Finished.Contains(request.Status) || request.ParentRequestId is not null)
            return false;
        var queue = await db.Requests.Where(r => r.ParentRequestId == null && r.Mode == request.Mode && r.Id != request.Id && !Finished.Contains(r.Status))
            .OrderBy(r => r.QueueOrder).ThenBy(r => r.CreatedAt).Select(r => new { r.Id, r.QueueOrder }).ToListAsync(ct);
        var index = before is { } id ? queue.FindIndex(r => r.Id == id) : queue.Count;
        if (index < 0)
            return false;
        var order = QueueOrders.Between(index > 0 ? queue[index - 1].QueueOrder : null, index < queue.Count ? queue[index].QueueOrder : null);
        var ids = await SubtreeAsync(request.Id, ct);
        await db.Requests.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.QueueOrder, order), ct);
        signal.Wake();
        return true;
    }

    /// <summary>Queues a finished request again, and any parts of it that didn't make it into the library.</summary>
    public async Task<bool> RetryAsync(Request request, CancellationToken ct)
    {
        if (!Finished.Contains(request.Status) || request.Status is RequestStatus.InLibrary or RequestStatus.Playable)
            return false;
        var ids = await SubtreeAsync(request.Id, ct);
        var children = ids.Where(id => id != request.Id).ToList();
        await db.Requests.Where(r => children.Contains(r.Id) && r.Status != RequestStatus.InLibrary)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Queued).SetProperty(r => r.Detail, (string?)null)
                .SetProperty(r => r.UpdatedAt, DateTime.UtcNow), ct);

        // A request with parts waits for them; one without is planned again from scratch.
        var hasChildren = children.Count > 0;
        request.Status = hasChildren ? RequestStatus.InProgress : RequestStatus.Queued;
        request.Detail = null;
        request.ChoiceJson = null;
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await UpdateAncestorsAsync(request.ParentRequestId, ct);
        signal.Wake();
        return true;
    }

    /// <summary>
    /// Queues a finished track or whole-album request to fetch one candidate from an earlier search, instead of
    /// searching again. False if the request has parts, is still running, or the candidate isn't one of the job's.
    /// </summary>
    public async Task<bool> ChooseAsync(Request request, Guid jobId, string candidateId, CancellationToken ct)
    {
        if (!Finished.Contains(request.Status) || request.Status == RequestStatus.InLibrary
            || await db.Requests.AnyAsync(r => r.ParentRequestId == request.Id, ct))
            return false;
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.RequestId == request.Id, ct);
        var candidates = job?.CandidatesJson is { } json ? JsonSerializer.Deserialize<List<Candidate>>(json, RetrievalJson.Options)! : [];
        if (job?.PluginInstanceId is not { } plugin || candidates.FirstOrDefault(c => c.CandidateId == candidateId) is not { } candidate)
            return false;

        request.ChoiceJson = JsonSerializer.Serialize(new Choice(plugin, candidate), RetrievalJson.Options);
        request.Status = RequestStatus.Queued;
        request.Detail = $"Fetching the chosen {candidate.Title}";
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await UpdateAncestorsAsync(request.ParentRequestId, ct);
        signal.Wake();
        return true;
    }

    /// <summary>Recomputes the state of every request above this one from its parts.</summary>
    public async Task UpdateAncestorsAsync(Guid? parentId, CancellationToken ct)
    {
        while (parentId is { } id)
        {
            var parent = await db.Requests.FindAsync([id], ct);
            if (parent is null || parent.Status is RequestStatus.Queued or RequestStatus.Cancelled)
                return;
            var parts = await PartsAsync(id, ct);
            var (status, detail) = Summarise(parts);
            if (parent.Status != status || parent.Detail != detail)
            {
                parent.Status = status;
                parent.Detail = detail;
                parent.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            parentId = parent.ParentRequestId;
        }
    }

    private static (RequestStatus, string) Summarise(RequestParts p)
    {
        if (p.Active > 0)
            return (RequestStatus.InProgress, $"{p.InLibrary} of {p.Total} in the library");
        if (p.InLibrary == p.Total)
            return (RequestStatus.InLibrary, $"All {p.Total} in the library");
        var problems = new List<string>();
        if (p.NeedsReview > 0) problems.Add($"{p.NeedsReview} need review");
        if (p.Failed > 0) problems.Add($"{p.Failed} failed");
        if (p.Cancelled > 0) problems.Add($"{p.Cancelled} cancelled");
        var summary = $"{p.InLibrary} of {p.Total} in the library; {string.Join(", ", problems)}";
        return (p.NeedsReview > 0 ? RequestStatus.NeedsReview : p.Failed > 0 ? RequestStatus.Failed : RequestStatus.Cancelled, summary);
    }

    public async Task<RequestParts> PartsAsync(Guid parentId, CancellationToken ct)
    {
        var counts = await db.Requests.Where(r => r.ParentRequestId == parentId)
            .GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(RequestStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        var total = counts.Sum(c => c.Count);
        var inLibrary = Count(RequestStatus.InLibrary);
        var review = Count(RequestStatus.NeedsReview);
        var failed = Count(RequestStatus.Failed);
        var cancelled = Count(RequestStatus.Cancelled);
        return new RequestParts(total, inLibrary, total - inLibrary - review - failed - cancelled, review, failed, cancelled);
    }

    /// <summary>The request, its parts and their jobs, for the Activity page.</summary>
    public async Task<RequestInfo?> GetAsync(Guid id, CancellationToken ct)
    {
        var request = await db.Requests.Include(r => r.Jobs).ThenInclude(j => j.PluginInstance).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return null;
        var children = await db.Requests.Where(r => r.ParentRequestId == id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var childInfos = new List<RequestInfo>();
        foreach (var child in children)
            childInfos.Add(await ToInfoAsync(child, ct));
        return (await ToInfoAsync(request, ct)) with
        {
            Children = childInfos,
            Jobs = request.Jobs.OrderBy(j => j.CreatedAt).Select(ToInfo).ToList(),
        };
    }

    /// <summary>Requests asked for directly (not parts of others), newest first.</summary>
    public async Task<IReadOnlyList<RequestInfo>> ListAsync(bool activeOnly, int limit, CancellationToken ct)
    {
        var query = db.Requests.Where(r => r.ParentRequestId == null);
        if (activeOnly)
            query = query.Where(r => !Finished.Contains(r.Status));
        var requests = await query.OrderByDescending(r => r.CreatedAt).Take(limit).ToListAsync(ct);
        var infos = new List<RequestInfo>();
        foreach (var request in requests)
            infos.Add(await ToInfoAsync(request, ct));
        return infos;
    }

    public async Task<RequestInfo> ToInfoAsync(Request r, CancellationToken ct)
    {
        var parts = r.TargetKind == EntityKind.Track ? null : await PartsAsync(r.Id, ct);
        return new RequestInfo(r.Id, ToKind(r.TargetKind), r.TargetId, r.EditionId, r.Title, (RequestState)(int)r.Status, r.Detail,
            r.ParentRequestId, r.CreatedAt, r.UpdatedAt, parts is { Total: > 0 } ? parts : null, ListenNow: r.Mode == RequestMode.ListenNow,
            Paused: r.Paused, QueueOrder: r.QueueOrder);
    }

    public static RequestKind ToKind(EntityKind kind) => kind switch
    {
        EntityKind.Artist => RequestKind.Artist,
        EntityKind.Album => RequestKind.Album,
        _ => RequestKind.Track,
    };

    public static JobInfo ToInfo(Job j)
    {
        var candidates = j.CandidatesJson is null ? [] : JsonSerializer.Deserialize<List<Candidate>>(j.CandidatesJson, RetrievalJson.Options) ?? [];
        var chosen = j.CandidateJson is null ? null : JsonSerializer.Deserialize<Candidate>(j.CandidateJson, RetrievalJson.Options);
        return new JobInfo(j.Id, j.Attempt, j.PluginInstance?.Name, (JobState)(int)j.Status, chosen is null ? null : ToInfo(chosen),
            candidates.Select(ToInfo).ToList(), (Verification)(int)j.Verification, j.VerificationDetail, j.Log, j.CreatedAt, j.FinishedAt);
    }

    private static JobCandidate ToInfo(Candidate c) =>
        new(c.CandidateId, c.Title, c.Artist, c.Album, c.DurationSeconds, c.Source, c.Url, c.Format, c.BitrateKbps, c.Score, c.Vouched);

    private async Task<List<Guid>> SubtreeAsync(Guid id, CancellationToken ct)
    {
        var all = new List<Guid> { id };
        var frontier = new List<Guid> { id };
        while (frontier.Count > 0)
        {
            frontier = await db.Requests.Where(r => r.ParentRequestId != null && frontier.Contains(r.ParentRequestId.Value)).Select(r => r.Id).ToListAsync(ct);
            all.AddRange(frontier);
        }
        return all;
    }

    private async Task<string?> TitleAsync(EntityKind kind, Guid id, CancellationToken ct) => kind switch
    {
        EntityKind.Artist => await db.Artists.Where(a => a.Id == id).Select(a => a.Name).FirstOrDefaultAsync(ct),
        EntityKind.Album => await db.Albums.Where(a => a.Id == id).Select(a => a.Title + " · " + a.ArtistCredit).FirstOrDefaultAsync(ct),
        _ => await db.Tracks.Where(t => t.Id == id).Select(t => t.Title + " · " + t.ArtistCredit).FirstOrDefaultAsync(ct),
    };

    private Task<bool> EditionFitsAsync(EntityKind kind, Guid id, Guid editionId, CancellationToken ct) => kind switch
    {
        EntityKind.Album => db.Editions.AnyAsync(e => e.Id == editionId && e.AlbumId == id, ct),
        EntityKind.Track => db.EditionTracks.AnyAsync(et => et.EditionId == editionId && et.TrackId == id, ct),
        _ => Task.FromResult(false),
    };
}

/// <summary>Wakes the request worker when there's something new to do.</summary>
public sealed class RequestSignal
{
    private readonly System.Threading.Channels.Channel<bool> _channel =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Cancellation for requests in flight, so cancelling one stops its fetch.</summary>
public sealed class RequestCancellations
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public CancellationTokenSource Register(Guid requestId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[requestId] = cts;
        return cts;
    }

    public void Unregister(Guid requestId)
    {
        if (_running.TryRemove(requestId, out var cts))
            cts.Dispose();
    }

    public void Cancel(Guid requestId)
    {
        if (_running.TryGetValue(requestId, out var cts))
            cts.Cancel();
    }
}

/// <summary>A candidate the user picked, and the plugin instance that offered it.</summary>
public sealed record Choice(Guid PluginInstanceId, Candidate Candidate);
