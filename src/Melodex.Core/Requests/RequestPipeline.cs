using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;

namespace Melodex.Core.Requests;

/// <summary>
/// Fetches one track, or one whole album, into the library. Each retrieval plugin in the user's order is asked for
/// candidates; the best are fetched into staging one at a time and checked against the catalogue's track lengths. The
/// first that passes is handed to beets, pinned to the edition's MusicBrainz release, recorded as owned, and announced
/// to the media server. When every plugin has had its turn, the request goes to the review list.
/// </summary>
/// <remarks>
/// An upgrade only tries copies better than the file it upgrades, and puts the one it keeps in that file's place,
/// tagged as it was, instead of filing it with beets (see <see cref="Upgrades"/>).
/// A listen-now request stops after the checks: its file goes to the listen-now store instead of to beets. Adding it
/// to the library later runs the request again as an ordinary one, which files that same file without fetching.
/// </remarks>
public sealed class RequestPipeline(
    MelodexDbContext db,
    RetrievalPlugins retrieval,
    IImportWorker importer,
    ConnectorPlugins connectors,
    SettingsService settings,
    RequestService requests,
    ListenNowService listen,
    Upgrades upgrades,
    IOptions<MelodexOptions> options,
    IOptions<RequestPipelineOptions> pipelineOptions,
    ILogger<RequestPipeline> logger)
{
    /// <summary>Runs a planned leaf request to the end. Album requests no plugin could fetch whole are split into tracks.</summary>
    public async Task RunAsync(Request request, CancellationToken ct)
    {
        var fetch = await settings.GetFetchAsync(ct);
        // A listened track that's added keeps its file, if it's a quality the profile allows; else a better one is found.
        if (request.Mode == RequestMode.Permanent
            && await db.ListenNowItems.FirstOrDefaultAsync(i => i.RequestId == request.Id && i.State == Data.ListenNowState.Importing, ct) is { } adding
            && listen.FullPath(adding) is { } kept && File.Exists(kept)
            && (await settings.GetQualityAsync(ct)).Allows(MediaTypes.ReadQuality(kept)))
        {
            await AddListenedAsync(request, adding, kept, fetch, ct);
            return;
        }

        // An upgrade whose file has gone, or become good enough meanwhile, has nothing to do.
        FileQuality? upgrading = null;
        if (request.UpgradesFileId is { } fileId)
        {
            if (await db.LibraryFiles.FindAsync([fileId], ct) is not { } old)
            {
                await SetAsync(request, RequestStatus.Failed, "The file it would upgrade isn't in the library any more", ct);
                return;
            }
            if ((await upgrades.UpgradableAsync(db.LibraryFiles.Where(f => f.Id == fileId), ct)).Count == 0)
            {
                await SetAsync(request, RequestStatus.InLibrary, $"It's {old.Quality.Label} now, which is good enough", ct);
                return;
            }
            upgrading = old.Quality;
        }

        var whole = request.TargetKind == EntityKind.Album;
        var chain = (await retrieval.GetChainAsync(ct))
            .Where(p => p.Capabilities.Contains(whole ? RetrievalCapabilities.FetchAlbum : RetrievalCapabilities.FetchTrack))
            .ToList();

        var target = await FetchTarget.LoadAsync(db, request, whole, ct);
        if (chain.Count == 0)
        {
            if (whole)
            {
                await SplitIntoTracksAsync(request, target, "No retrieval plugin fetches whole albums, so it's fetched track by track", ct);
                return;
            }
            await SetAsync(request, RequestStatus.Failed, "No retrieval plugins are set up. Add one under Settings → Plugins, then retry.", ct);
            return;
        }

        var attempt = await db.Jobs.Where(j => j.RequestId == request.Id).MaxAsync(j => (int?)j.Attempt, ct) ?? 0;
        async Task<Job> StartJobAsync(RetrievalPlugins.Entry plugin, Candidate candidate, IReadOnlyList<Candidate> candidates)
        {
            var job = new Job
            {
                RequestId = request.Id,
                PluginInstanceId = plugin.Instance.Id,
                Attempt = ++attempt,
                Status = JobStatus.Running,
                StartedAt = DateTime.UtcNow,
                CandidateJson = JsonSerializer.Serialize(candidate, RetrievalJson.Options),
                CandidatesJson = JsonSerializer.Serialize(candidates, RetrievalJson.Options),
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            return job;
        }

        // A candidate the user picked on the Activity page is tried on its own, without searching.
        if (request.ChoiceJson is { } chosen)
        {
            var choice = JsonSerializer.Deserialize<Choice>(chosen, RetrievalJson.Options)!;
            request.ChoiceJson = null;
            await db.SaveChangesAsync(ct);
            if (choice.Keep is { } keepId)
            {
                await KeepAsync(request, target, keepId, fetch, ct);
                return;
            }
            if (chain.FirstOrDefault(p => p.Instance.Id == choice.PluginInstanceId) is not { } offeredBy)
            {
                await SetAsync(request, RequestStatus.NeedsReview, "The plugin that offered the chosen candidate is no longer set up or enabled", ct);
                return;
            }
            var picked = await StartJobAsync(offeredBy, choice.Candidate, [choice.Candidate]);
            if (await TryCandidateAsync(request, target, offeredBy, picked, choice.Candidate, fetch, ct) is Outcome.NextCandidate)
                await SetAsync(request, RequestStatus.NeedsReview, $"The chosen candidate didn't work: {picked.VerificationDetail ?? LastLine(picked.Log)}", ct);
            return;
        }

        // Every plugin is searched at once, and their candidates tried in one order: the right song, then the quality
        // the profile likes best, then each plugin's own confidence.
        var tried = new List<string>();
        await SetAsync(request, RequestStatus.Searching, chain.Count == 1 ? $"Searching {chain[0].Instance.Name}" : $"Searching {chain.Count} plugins", ct);
        var searches = await Task.WhenAll(chain.Select(async plugin =>
        {
            try
            {
                return (plugin, Candidates: await plugin.Plugin.SearchAsync(target.ToRetrieval(), 10, ct), Error: (string?)null);
            }
            catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (plugin, Candidates: (IReadOnlyList<Candidate>)[], Error: e.Message);
            }
        }));

        var profile = await settings.GetQualityAsync(ct);
        var listenNow = request.Mode == RequestMode.ListenNow;
        var found = new List<(RetrievalPlugins.Entry Plugin, Candidate Candidate)>();
        foreach (var (plugin, candidates, error) in searches)
        {
            if (error is not null)
            {
                logger.LogWarning("[{Request}] {Plugin} couldn't search: {Reason}", request.Title, plugin.Instance.Name, error);
                tried.Add($"{plugin.Instance.Name} couldn't search ({error})");
                continue;
            }
            var fitting = Rank(candidates, target, fetch).Select(c => (plugin, c)).ToList();
            // An upgrade only fetches what may be better: said to be, or not saying.
            var better = upgrading is null ? fitting : fitting.Where(f => Better(profile, CandidateRanking.QualityOf(f.c), upgrading) != false).ToList();
            var wanted = CandidateRanking.Order(better, profile, listenNow, c => LengthOff(c, target)).Take(fetch.CandidatesPerPlugin).ToList();
            if (wanted.Count == 0)
            {
                var why = better.Count < fitting.Count ? $"found nothing better than the {upgrading!.Label} you have"
                    : fitting.Count > 0 ? "found nothing in a quality the profile allows" : "found nothing that fits";
                logger.LogInformation("[{Request}] {Plugin} {Why}", request.Title, plugin.Instance.Name, why);
                tried.Add($"{plugin.Instance.Name} {why}");
                continue;
            }
            found.AddRange(wanted);
        }

        foreach (var (plugin, candidate) in CandidateRanking.Order(found, profile, listenNow, c => LengthOff(c, target)).ToList())
        {
            var job = await StartJobAsync(plugin, candidate, searches.First(s => s.plugin == plugin).Candidates);
            var outcome = await TryCandidateAsync(request, target, plugin, job, candidate, fetch, ct);
            if (outcome is Outcome.Imported or Outcome.Stop)
                return;
            tried.Add($"{plugin.Instance.Name}: {job.VerificationDetail ?? LastLine(job.Log)}");
        }

        if (whole)
        {
            await SplitIntoTracksAsync(request, target, "No plugin could fetch the whole album, so it's fetched track by track", ct);
            return;
        }
        // The song is still in the library, as it was: it's looked for again another time.
        if (upgrading is not null)
        {
            await SetAsync(request, RequestStatus.InLibrary, $"Nothing better found: {Summary(tried)}", ct);
            logger.LogInformation("[{Request}] No upgrade found: {Reason}", request.Title, Summary(tried));
            return;
        }
        await SetAsync(request, RequestStatus.NeedsReview, Summary(tried), ct);
        logger.LogWarning("[{Request}] Needs review: {Reason}", request.Title, Summary(tried));
    }

    private enum Outcome { Imported, NextCandidate, Stop }

    private async Task<Outcome> TryCandidateAsync(Request request, FetchTarget target, RetrievalPlugins.Entry plugin, Job job, Candidate candidate,
        FetchSettings fetch, CancellationToken ct)
    {
        var staging = Path.GetFullPath(options.Value.StagingPath);
        job.StagingDirectory = job.Id.ToString("N");
        var directory = Path.Combine(staging, job.StagingDirectory);
        Directory.CreateDirectory(directory);
        // The plugin and the beets worker run as other users in their own containers, and write and delete here.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
        Log(job, $"Fetching {Describe(candidate)} from {plugin.Instance.Name}");
        await SetAsync(request, RequestStatus.Fetching, $"Fetching from {plugin.Instance.Name}", ct);

        // Fetch
        FetchStatus status;
        try
        {
            status = await plugin.Plugin.StartFetchAsync(job.Id.ToString(), target.ToRetrieval(), candidate, job.StagingDirectory, ct);
            // A torrent can take hours and still be fine, so the limit is on time without progress.
            var deadline = DateTime.UtcNow + pipelineOptions.Value.FetchTimeout;
            var lastProgress = status.Progress;
            while (status.State is FetchState.Queued or FetchState.Running)
            {
                if (DateTime.UtcNow > deadline)
                {
                    if (plugin.Capabilities.Contains(RetrievalCapabilities.Cancel))
                        await IgnoreErrors(() => plugin.Plugin.CancelFetchAsync(job.Id.ToString(), CancellationToken.None));
                    var done = lastProgress is { } p ? $" at {p:P0}" : "";
                    var limit = pipelineOptions.Value.FetchTimeout;
                    var span = limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0} minutes" : $"{limit.TotalSeconds:0} seconds";
                    throw new TimeoutException($"The fetch made no progress for {span}{done}");
                }
                await Task.Delay(pipelineOptions.Value.PollInterval, ct);
                status = await plugin.Plugin.GetFetchStatusAsync(job.Id.ToString(), ct);
                if (status.Progress is { } progress)
                {
                    if (progress > (lastProgress ?? -1))
                    {
                        lastProgress = progress;
                        deadline = DateTime.UtcNow + pipelineOptions.Value.FetchTimeout;
                    }
                    await SetAsync(request, RequestStatus.Fetching, $"Fetching from {plugin.Instance.Name} ({progress:P0})", ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (plugin.Capabilities.Contains(RetrievalCapabilities.Cancel))
                await IgnoreErrors(() => plugin.Plugin.CancelFetchAsync(job.Id.ToString(), CancellationToken.None));
            await FinishJobAsync(job, JobStatus.Cancelled, "Cancelled", CancellationToken.None);
            DeleteStaging(job);
            throw;
        }
        catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TimeoutException or TaskCanceledException)
        {
            await FinishJobAsync(job, JobStatus.Failed, $"The fetch failed: {e.Message}", ct);
            DeleteStaging(job);
            return Outcome.NextCandidate;
        }
        if (status.State != FetchState.Completed)
        {
            await FinishJobAsync(job, JobStatus.Failed, $"The fetch {status.State.ToString().ToLowerInvariant()}: {status.Error ?? "no reason given"}", ct);
            DeleteStaging(job);
            return Outcome.NextCandidate;
        }

        return await VerifyAndFileAsync(request, target, job, directory, status.Files ?? [], fetch, ct);
    }

    /// <summary>Keeps the copy an attempt fetched but didn't keep for its quality, as it is: it's checked to be the song, and filed.</summary>
    private async Task KeepAsync(Request request, FetchTarget target, Guid jobId, FetchSettings fetch, CancellationToken ct)
    {
        var job = await db.Jobs.FindAsync([jobId], ct);
        var directory = job?.StagingDirectory is { } staged ? Path.Combine(Path.GetFullPath(options.Value.StagingPath), staged) : null;
        if (job is null || directory is null || !Directory.Exists(directory))
        {
            await SetAsync(request, RequestStatus.NeedsReview, "The copy that wasn't kept isn't in staging any more", ct);
            return;
        }
        job.Status = JobStatus.Running;
        job.FinishedAt = null;
        Log(job, "Keeping it anyway, as asked");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => new FetchedFile(Path.GetRelativePath(directory, f).Replace('\\', '/'), null, null, null, null)).ToList();
        if (await VerifyAndFileAsync(request, target, job, directory, files, fetch, ct, keep: true) is Outcome.NextCandidate)
            await SetAsync(request, RequestStatus.NeedsReview, $"It couldn't be kept: {job.VerificationDetail ?? LastLine(job.Log)}", ct);
    }

    /// <summary>Adds a track fetched for listen now: its file is checked and filed like a fresh fetch, without fetching again.</summary>
    private async Task AddListenedAsync(Request request, ListenNowItem adding, string kept, FetchSettings fetch, CancellationToken ct)
    {
        var target = await FetchTarget.LoadAsync(db, request, false, ct);
        var attempt = await db.Jobs.Where(j => j.RequestId == request.Id).MaxAsync(j => (int?)j.Attempt, ct) ?? 0;
        var job = new Job { RequestId = request.Id, Attempt = attempt + 1, Status = JobStatus.Running, StartedAt = DateTime.UtcNow };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        job.StagingDirectory = job.Id.ToString("N");
        var directory = Path.Combine(Path.GetFullPath(options.Value.StagingPath), job.StagingDirectory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
        // A copy, so the track stays playable if filing fails.
        var name = "listened" + Path.GetExtension(kept);
        File.Copy(kept, Path.Combine(directory, name), overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(directory, name), (UnixFileMode)0b110_110_110);
        Log(job, "Using the file fetched for listen now");

        var outcome = await VerifyAndFileAsync(request, target, job, directory, [new FetchedFile(name, adding.Codec, adding.Container, adding.BitrateKbps, null)], fetch, ct);
        if (outcome == Outcome.NextCandidate)
        {
            DeleteStaging(job);
            await SetAsync(request, RequestStatus.Failed, $"Couldn't add it: {job.VerificationDetail ?? LastLine(job.Log)}", ct);
        }
    }

    /// <summary>
    /// Checks fetched files against the catalogue, then files them: with beets for an ordinary request, or in the
    /// listen-now store for a listen-now one.
    /// </summary>
    private async Task<Outcome> VerifyAndFileAsync(Request request, FetchTarget target, Job job, string directory, IReadOnlyList<FetchedFile> reported,
        FetchSettings fetch, CancellationToken ct, bool keep = false)
    {
        // Verify
        await SetAsync(request, RequestStatus.Verifying, "Checking the files", ct);
        var verified = Verifier.Check(directory, reported, target.Tracks, fetch);
        job.Verification = verified.Result;
        job.VerificationDetail = verified.Detail;
        if (verified.Result != VerificationResult.Passed)
        {
            // Kept in staging, so the review list can still accept it.
            await FinishJobAsync(job, JobStatus.Failed, $"Didn't pass: {verified.Detail}", ct);
            return Outcome.NextCandidate;
        }
        Log(job, $"Checked: {verified.Detail}");

        // The quality is what the files are, not what they were said to be: a FLAC that's really an MP3 is an MP3.
        var qualities = verified.Files.Select(f => MediaTypes.ReadQuality(Path.Combine(directory, f.Path))).ToList();
        // Lossless music to keep is checked for being lossy music passed off as it.
        var cutoffs = request.Mode == RequestMode.Permanent ? await CheckSpectraAsync(job, verified.Files, qualities, ct) : [];
        qualities = qualities.Select((q, i) => cutoffs.GetValueOrDefault(verified.Files[i].Path) is { } hz ? q with { CutoffHz = hz } : q).ToList();
        var actual = FileQuality.Lowest(qualities.Where(q => q.Codec is not null));
        if (actual is not null)
        {
            var labelled = job.CandidateJson is { } json ? CandidateRanking.QualityOf(JsonSerializer.Deserialize<Candidate>(json, RetrievalJson.Options)!) : null;
            var profile = await settings.GetQualityAsync(ct);
            if (labelled?.Codec is not null && profile.Position(labelled) is { } said && profile.Position(actual) is { } @is && @is > said)
                Log(job, $"Said to be {labelled.Label}, but it's {actual.Label} ({actual.Details})");
            else
                Log(job, $"Quality: {actual.Label} ({actual.Details})");
            // Kept anyway, as asked, its quality is what it is, and counts as what it seems.
            if (keep)
                Log(job, "Kept whatever its quality");
            else if (request.Mode == RequestMode.Permanent && qualities.Where(q => q.Suspect).MinBy(q => q.Rank) is { } suspect
                && profile.Suspect == SuspectLossless.Refuse)
            {
                var seems = suspect.Seems == QualityLevel.Lossless ? "made from a CD" : $"lossy, {FileQuality.Describe(suspect.Level).ToLowerInvariant()} quality";
                job.Verification = VerificationResult.SuspectLossless;
                job.VerificationDetail = $"It's {suspect.Label}, but its highs stop at {FileQuality.CutoffKilohertz(suspect.CutoffHz!.Value)} kHz: it seems to be {seems}";
                // Kept in staging, so the review list can still accept it.
                await FinishJobAsync(job, JobStatus.Failed, $"Didn't pass: {job.VerificationDetail}", ct);
                return Outcome.NextCandidate;
            }
            if (!keep && request.Mode == RequestMode.Permanent && !profile.Allows(actual))
            {
                job.Verification = VerificationResult.QualityRefused;
                job.VerificationDetail = $"It's {actual.Label} ({actual.Details}), which the quality profile turns off";
                // Kept in staging, so the review list can still accept it.
                await FinishJobAsync(job, JobStatus.Failed, $"Didn't pass: {job.VerificationDetail}", ct);
                return Outcome.NextCandidate;
            }
        }

        // An upgrade keeps a copy only if it's better than the file it upgrades, by what it is, not what it was said to be.
        if (request.UpgradesFileId is { } upgradesId)
        {
            var old = await db.LibraryFiles.FindAsync([upgradesId], ct);
            if (old is null)
            {
                await FinishJobAsync(job, JobStatus.Failed, "The file it would upgrade isn't in the library any more", ct);
                DeleteStaging(job);
                await SetAsync(request, RequestStatus.Failed, "The file it would upgrade isn't in the library any more", ct);
                return Outcome.Stop;
            }
            var profile = await settings.GetQualityAsync(ct);
            if (verified.Files.Count != 1 || Better(profile, actual, old.Quality) != true)
            {
                job.Verification = VerificationResult.NotBetter;
                job.VerificationDetail = actual is null
                    ? $"Its quality couldn't be told, so it may be no better than the {old.Quality.Label} you have"
                    : $"It's {actual.Label}, no better than the {old.Quality.Label} you have";
                await FinishJobAsync(job, JobStatus.Failed, $"Didn't pass: {job.VerificationDetail}", ct);
                DeleteStaging(job);
                return Outcome.NextCandidate;
            }
            return await UpgradeAsync(request, job, old, verified.Files[0], actual!, cutoffs, ct);
        }

        if (request.Mode == RequestMode.ListenNow)
        {
            await StoreForListeningAsync(request, job, directory, verified.Files[0], reported, ct);
            return Outcome.Imported;
        }

        // Import
        await SetAsync(request, RequestStatus.Importing, "Filing with beets", ct);
        ImportResult imported;
        try
        {
            imported = await ImportAsync(target, job, verified.Files, ct);
        }
        catch (Exception e) when (e is ImportException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Beets failing isn't the plugin's fault; another plugin's file would fail the same way.
            await FinishJobAsync(job, JobStatus.Failed, $"Beets couldn't import it: {e.Message}", ct);
            await SetAsync(request, RequestStatus.Failed, $"Beets couldn't import it: {e.Message}", ct);
            logger.LogError("[{Request}] Beets couldn't import it: {Reason}", request.Title, e.Message);
            return Outcome.Stop;
        }
        var files = await RecordAsync(target, imported, verified.Files, cutoffs, ct);
        await FinishJobAsync(job, JobStatus.Succeeded, $"Filed {string.Join(", ", files.Select(f => f.Path))}", ct);
        DeleteStaging(job);
        await DeleteOtherStagingAsync(request.Id, job.Id, ct);

        // Added from listen now: the library has it now, so the listen-now copy goes.
        if (await db.ListenNowItems.FirstOrDefaultAsync(i => i.RequestId == request.Id && i.State == Data.ListenNowState.Importing, ct) is { } added)
        {
            listen.DeleteFile(added);
            added.State = Data.ListenNowState.InLibrary;
            await db.SaveChangesAsync(ct);
        }

        // Notify
        var detail = await NotifyAsync(request, files, ct);
        await SetAsync(request, RequestStatus.InLibrary, detail, ct);
        logger.LogInformation(Diagnostics.LogEvents.Success, "[{Request}] In the library: {Detail}", request.Title, detail);
        return Outcome.Imported;
    }

    /// <summary>Whether a quality is better than another by the profile; null when it isn't known.</summary>
    private static bool? Better(QualityProfile profile, FileQuality? quality, FileQuality than) =>
        profile.Position(quality) is { } at && profile.Position(than) is { } old ? at < old : null;

    /// <summary>Puts a checked better copy in place of the file it upgrades, then tells the media server.</summary>
    private async Task<Outcome> UpgradeAsync(Request request, Job job, LibraryFile old, Verifier.Matched file, FileQuality actual,
        IReadOnlyDictionary<string, int?> cutoffs, CancellationToken ct)
    {
        await SetAsync(request, RequestStatus.Importing, "Putting it in place of the old file", ct);
        var was = old.Quality;
        var path = old.Path;
        LibraryFile upgraded;
        try
        {
            Log(job, $"Replacing {path} ({was.Label}), keeping its tags");
            upgraded = await upgrades.ReplaceAsync(request, old, $"{job.StagingDirectory}/{file.Path}",
                (cutoffs.ContainsKey(file.Path), cutoffs.GetValueOrDefault(file.Path)), ct);
        }
        catch (Exception e) when (e is ImportException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await FinishJobAsync(job, JobStatus.Failed, $"Couldn't replace the old file: {e.Message}", ct);
            await SetAsync(request, RequestStatus.Failed, $"Couldn't replace the old file: {e.Message}", ct);
            logger.LogError("[{Request}] Couldn't replace the old file: {Reason}", request.Title, e.Message);
            return Outcome.Stop;
        }
        await FinishJobAsync(job, JobStatus.Succeeded, $"Upgraded {path} to {upgraded.Path}", ct);
        DeleteStaging(job);
        await DeleteOtherStagingAsync(request.Id, job.Id, ct);

        var detail = await NotifyAsync(request, [upgraded], ct);
        detail = $"Upgraded from {was.Label} to {upgraded.Quality.Label}. {detail}";
        await SetAsync(request, RequestStatus.InLibrary, detail, ct);
        logger.LogInformation(Diagnostics.LogEvents.Success, "[{Request}] {Detail}", request.Title, detail);
        return Outcome.Imported;
    }

    /// <summary>Moves a checked file into the listen-now store and marks the track playable, for the expiry setting from now.</summary>
    private async Task StoreForListeningAsync(Request request, Job job, string directory, Verifier.Matched file, IReadOnlyList<FetchedFile> reported,
        CancellationToken ct)
    {
        var item = await db.ListenNowItems.FirstAsync(i => i.RequestId == request.Id, ct);
        var store = listen.Store;
        Directory.CreateDirectory(store);
        var name = item.Id.ToString("N") + Path.GetExtension(file.Path).ToLowerInvariant();
        var destination = Path.Combine(store, name);
        // Staging and the store are often separate volumes, where a move is a copy and a delete.
        File.Move(Path.Combine(directory, file.Path), destination, overwrite: true);

        var said = reported.FirstOrDefault(f => f.Path == file.Path);
        var probed = MediaTypes.Probe(destination);
        item.FilePath = name;
        item.Codec = said?.Codec?.ToLowerInvariant() ?? probed.Codec ?? MediaTypes.CodecFor(name);
        item.Container = said?.Container?.ToLowerInvariant() ?? MediaTypes.ContainerFor(name);
        item.BitrateKbps = said?.BitrateKbps ?? probed.BitrateKbps;
        item.EditionId = request.EditionId;
        item.State = Data.ListenNowState.Playable;
        item.ExpiresAt = DateTime.UtcNow + await listen.ExpiryAsync(ct);
        await FinishJobAsync(job, JobStatus.Succeeded, $"Ready to play ({item.Codec ?? "unknown codec"}{(item.BitrateKbps is { } kbps ? $", {kbps} kbps" : "")})", ct);
        DeleteStaging(job);
        await DeleteOtherStagingAsync(request.Id, job.Id, ct);
        await SetAsync(request, RequestStatus.Playable, "Ready to play", ct);
        logger.LogInformation(Diagnostics.LogEvents.Success, "[{Request}] Ready to listen to", request.Title);
    }

    /// <summary>
    /// Candidates worth fetching: the right kind, and not so far off in length that verification would refuse them, so
    /// fetching them would only fail. <see cref="CandidateRanking"/> puts them in order.
    /// </summary>
    public static IEnumerable<Candidate> Rank(IReadOnlyList<Candidate> candidates, FetchTarget target, FetchSettings fetch)
    {
        var expected = ExpectedSeconds(target);
        return candidates
            .Where(c => c.Kind == (target.Whole ? RetrievalKind.Album : RetrievalKind.Track))
            .Where(c => expected is null || c.DurationSeconds is null
                        || Math.Abs(c.DurationSeconds.Value - expected.Value) <= 2 * Verifier.Tolerance(expected.Value, fetch) * (target.Whole ? target.Tracks.Count : 1));
    }

    private static double? ExpectedSeconds(FetchTarget target) => target.Whole
        ? target.Tracks.All(t => t.Duration is not null) ? target.Tracks.Sum(t => t.Duration!.Value.TotalSeconds) : null
        : target.Tracks[0].Duration?.TotalSeconds;

    private static double LengthOff(Candidate c, FetchTarget target) =>
        ExpectedSeconds(target) is { } expected && c.DurationSeconds is { } seconds ? Math.Abs(seconds - expected) : double.MaxValue;

    private async Task<ImportResult> ImportAsync(FetchTarget target, Job job, IReadOnlyList<Verifier.Matched> files, CancellationToken ct)
    {
        var fetch = await settings.GetFetchAsync(ct);
        var server = await db.PluginInstances.Where(p => p.PluginId == Melodex.Plugins.MusicBrainz.MusicBrainzIndexFactory.PluginId).Select(p => p.SettingsJson).FirstOrDefaultAsync(ct);
        string? musicBrainz = null;
        if (server is not null && JsonDocument.Parse(server).RootElement.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String)
            musicBrainz = s.GetString();

        var request = new ImportRequest(
            job.StagingDirectory!,
            new ImportRelease(target.ReleaseId, target.Edition.Title, target.Album.ArtistCredit, target.Edition.ReleaseDate, target.EditionTrackCount),
            files.Select(f => new ImportFile(f.Path, f.Track.RecordingId ?? "", null, f.Track.Title, f.Track.ArtistCredit, f.Track.Disc, f.Track.Position,
                f.Track.Ids.FirstOrDefault(i => i.Provider == Providers.Isrc)?.Value)).ToList(),
            target.DestinationDirectory,
            fetch.PathTemplate,
            musicBrainz);
        var into = target.DestinationDirectory is null ? "" : $", into {target.DestinationDirectory}";
        Log(job, target.ReleaseId is null
            ? $"Filing with beets as tagged, since MusicBrainz doesn't have this edition{into}"
            : $"Importing with beets, pinned to release {target.ReleaseId}{into}");
        return await importer.ImportAsync(request, ct);
    }

    /// <summary>
    /// Where each lossless file's highs stop, by its path in staging: a file in it was checked, and its value is null
    /// when its highs aren't cut off. Files that couldn't be checked aren't in it.
    /// </summary>
    private async Task<Dictionary<string, int?>> CheckSpectraAsync(Job job, IReadOnlyList<Verifier.Matched> files, IReadOnlyList<FileQuality> qualities,
        CancellationToken ct)
    {
        var lossless = files.Where((_, i) => qualities[i].Lossless).ToList();
        if (lossless.Count == 0 || job.StagingDirectory is not { } staging)
            return [];
        try
        {
            var found = await importer.SpectrumAsync(new SpectrumRequest(lossless.Select(f => new SpectrumFile(SpectrumFile.Staging, $"{staging}/{f.Path}")).ToList()), ct);
            return found.Files.Where(f => f.Error is null)
                .ToDictionary(f => f.Path[(staging.Length + 1)..], f => f.CutoffHz);
        }
        catch (Exception e) when (e is ImportException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // An older worker can't tell; the files are taken as they say they are.
            Log(job, $"Couldn't check whether it's really lossless: {e.Message}");
            return [];
        }
    }

    /// <summary>Records the imported files as owned straight away, so the library shows them without a rescan.</summary>
    private async Task<List<LibraryFile>> RecordAsync(FetchTarget target, ImportResult imported, IReadOnlyList<Verifier.Matched> sent,
        IReadOnlyDictionary<string, int?> cutoffs, CancellationToken ct)
    {
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var recorded = new List<LibraryFile>();
        foreach (var file in imported.Files)
        {
            var match = sent.FirstOrDefault(s => s.Path == file.Source);
            if (match is null)
                continue;
            var path = file.Path.Replace('\\', '/');
            var full = Path.Combine(root, path);
            var row = await db.LibraryFiles.FirstOrDefaultAsync(f => f.Path == path, ct);
            if (row is null)
            {
                row = new LibraryFile { Path = path };
                db.LibraryFiles.Add(row);
            }
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                row.Size = info.Length;
                row.ModifiedAt = info.LastWriteTimeUtc;
                try
                {
                    var (tags, duration, lyrics, quality) = LibraryScanner.ReadTags(full);
                    row.TagsJson = tags.ToJson();
                    row.SetQuality(quality);
                    // Its spectrum, as checked in staging: the music is the same, whatever beets did to its tags.
                    if (cutoffs.TryGetValue(file.Source, out var cutoff))
                    {
                        row.CutoffHz = cutoff;
                        row.SpectrumCheckedAt = DateTime.UtcNow;
                    }
                    row.Duration = duration;
                    row.Lyrics = lyrics;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning("Couldn't read the imported file {Path}: {Reason}", path, e.Message);
                }
            }
            row.Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            row.Duration ??= file.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
            row.ArtistId = match.Track.ArtistId;
            row.TrackId = match.Track.TrackId;
            row.EditionId = target.Edition.Id;
            row.MatchMethod = target.ReleaseId is null ? MatchMethod.Crosswalk : MatchMethod.MusicBrainzTags;
            row.MatchConfidence = 1;
            row.MatchedAt = row.ScannedAt = DateTime.UtcNow;
            row.ReviewReason = null;
            row.SuggestedTrackId = row.SuggestedEditionId = null;
            recorded.Add(row);
        }
        var artists = recorded.Select(f => f.ArtistId).OfType<Guid>().Distinct().ToList();
        await db.SaveChangesAsync(ct);
        await db.Artists.Where(a => artists.Contains(a.Id) && !a.InLibrary).ExecuteUpdateAsync(s => s.SetProperty(a => a.InLibrary, true), ct);
        return recorded;
    }

    /// <summary>Tells the media server, then waits until it has every file. Not finding them in time isn't a failure.</summary>
    private async Task<string> NotifyAsync(Request request, List<LibraryFile> files, CancellationToken ct)
    {
        var active = await connectors.GetActiveAsync(ct);
        if (active is not { } connector)
            return $"Filed {Plural(files.Count, "file", "files")}; no media server is set up to tell";

        await SetAsync(request, RequestStatus.Notifying, $"Waiting for {connector.Instance.Name} to pick it up", ct);
        try
        {
            await connector.Connector.TriggerScanAsync(files.Select(f => f.Path).ToList(), ct);
            var deadline = DateTime.UtcNow + pipelineOptions.Value.MediaServerTimeout;
            var delay = pipelineOptions.Value.PollInterval;
            var waiting = files.ToList();
            while (waiting.Count > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(delay, ct);
                if ((await connector.Connector.GetScanStatusAsync(ct)).Scanning)
                    continue;
                foreach (var file in waiting.ToList())
                {
                    var tags = FileTags.Parse(file.TagsJson);
                    var id = await connector.Connector.ResolveTrackAsync(new TrackLookup(file.Path, tags.MusicBrainzRecordingId, tags.MusicBrainzReleaseId,
                        tags.Title, tags.Artist ?? tags.AlbumArtist, tags.Album, file.Duration, tags.Isrc), ct);
                    if (id is null)
                        continue;
                    file.MediaServerTrackId = id;
                    waiting.Remove(file);
                }
                await db.SaveChangesAsync(ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
            return waiting.Count == 0
                ? $"Filed and in {connector.Instance.Name}"
                : $"Filed, but {connector.Instance.Name} hadn't picked up {Plural(waiting.Count, "file", "files")} after {pipelineOptions.Value.MediaServerTimeout.TotalMinutes:0} minutes";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("[{Request}] Couldn't tell {Server}: {Reason}", request.Title, connector.Instance.Name, e.Message);
            return $"Filed, but telling {connector.Instance.Name} failed: {e.Message}";
        }
    }

    private async Task SplitIntoTracksAsync(Request request, FetchTarget target, string why, CancellationToken ct)
    {
        foreach (var track in target.Tracks)
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track,
                TargetId = track.TrackId,
                EditionId = target.Edition.Id,
                Title = $"{track.Title} · {track.ArtistCredit}",
                Mode = request.Mode,
                Requester = request.Requester,
                ParentRequestId = request.Id,
                QueueOrder = request.QueueOrder,
                Paused = request.Paused,
            });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("[{Request}] {Why}", request.Title, why);
        await SetAsync(request, RequestStatus.InProgress, why, ct);
    }

    /// <summary>Sets a request's state unless it was cancelled meanwhile, and updates the requests it's part of.</summary>
    private async Task SetAsync(Request request, RequestStatus status, string? detail, CancellationToken ct)
    {
        var updated = await db.Requests.Where(r => r.Id == request.Id && r.Status != RequestStatus.Cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.Detail, detail).SetProperty(r => r.UpdatedAt, DateTime.UtcNow),
                CancellationToken.None);
        if (updated == 0)
            return;
        request.Status = status;
        request.Detail = detail;
        // Already written; left marked as changed, the next save would write them again over a cancel made meanwhile.
        var entry = db.Entry(request);
        if (entry.State != EntityState.Detached)
            foreach (var property in new[] { nameof(Request.Status), nameof(Request.Detail), nameof(Request.UpdatedAt) })
                entry.Property(property).IsModified = false;
        if (status is RequestStatus.InLibrary or RequestStatus.NeedsReview or RequestStatus.Failed or RequestStatus.InProgress)
            await requests.UpdateAncestorsAsync(request.ParentRequestId, CancellationToken.None);
    }

    private async Task FinishJobAsync(Job job, JobStatus status, string line, CancellationToken ct)
    {
        Log(job, line);
        job.Status = status;
        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private void DeleteStaging(Job job)
    {
        if (job.StagingDirectory is null)
            return;
        var path = Path.Combine(Path.GetFullPath(options.Value.StagingPath), job.StagingDirectory);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException e)
        {
            logger.LogWarning("Couldn't clear staging folder {Path}: {Reason}", path, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            logger.LogWarning("Couldn't clear staging folder {Path}: {Reason}", path, e.Message);
        }
    }

    /// <summary>Once a request is in, files kept from attempts that didn't pass are no longer needed.</summary>
    private async Task DeleteOtherStagingAsync(Guid requestId, Guid keep, CancellationToken ct)
    {
        var others = await db.Jobs.Where(j => j.RequestId == requestId && j.Id != keep && j.StagingDirectory != null).ToListAsync(ct);
        foreach (var other in others)
            DeleteStaging(other);
    }

    private static void Log(Job job, string line) =>
        job.Log += $"{DateTime.UtcNow:HH:mm:ss} {line}\n";

    private static string LastLine(string log) =>
        log.TrimEnd('\n').Split('\n').LastOrDefault()?[9..] ?? "";

    private static string Describe(Candidate c)
    {
        var text = new StringBuilder($"\"{c.Title}\"");
        if (c.Artist is not null) text.Append($" by {c.Artist}");
        if (c.DurationSeconds is { } d) text.Append($" ({TimeSpan.FromSeconds(d):m\\:ss})");
        return text.ToString();
    }

    private static string Summary(List<string> tried) =>
        tried.Count == 0 ? "No plugin found it" : string.Join("; ", tried);

    private static string Plural(int n, string one, string many) => LibraryMatcher.Plural(n, one, many);

    private static async Task IgnoreErrors(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Best effort: the job is finished either way.
        }
    }
}

public sealed class RequestPipelineOptions
{
    /// <summary>How often a running fetch, and the media server, are polled at first.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest a fetch may go without making progress.</summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long to wait for the media server to have the new files.</summary>
    public TimeSpan MediaServerTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
