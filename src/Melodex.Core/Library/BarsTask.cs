using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Listening;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Keeps the visualiser's bars beside the library's songs, as .bars files, while the settings say to make them when
/// matching (#89): after each scan, for every song without them. The core works them out and the beets worker writes
/// them, as the core only reads the library. Asked to delete them, it takes every one away instead.
/// </summary>
public sealed class BarsTask(MelodexDbContext db, SettingsService settings, BarsMaker maker, IImportWorker worker,
    IOptions<MelodexOptions> options, ILogger<BarsTask> logger) : ITaskHandler
{
    public const string TaskKind = "library-bars";
    public const string Delete = "\"delete\"";
    private const int BatchSize = 10;

    public string Kind => TaskKind;

    public static string Title(string? payload) => payload == Delete ? "Deleting the visualiser's bars" : "Making the visualiser's bars";

    /// <summary>Where the library's songs are, with the full path of each.</summary>
    private async Task<List<(string Path, string Full)>> SongsAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var paths = await db.LibraryFiles.Where(f => f.HiddenAt == null).OrderBy(f => f.Path).Select(f => f.Path).ToListAsync(ct);
        return paths.Select(p => (p, Path.Combine(root, p))).ToList();
    }

    /// <summary>How many songs there are, about how much room bars for all of them would take, and those beside them now.</summary>
    public static async Task<PlaybackStatus> StatusAsync(MelodexDbContext db, SettingsService settings, string libraryPath, CancellationToken ct)
    {
        var root = Path.GetFullPath(libraryPath);
        var files = await db.LibraryFiles.Where(f => f.HiddenAt == null).Select(f => new { f.Path, f.Duration }).ToListAsync(ct);
        int count = 0;
        long bytes = 0;
        foreach (var file in files)
        {
            var sidecar = new FileInfo(BarsFile.SidecarOf(Path.Combine(root, file.Path)));
            if (sidecar.Exists)
            {
                count++;
                bytes += sidecar.Length;
            }
        }
        var seconds = files.Sum(f => (f.Duration ?? TimeSpan.FromMinutes(4)).TotalSeconds);
        return new PlaybackStatus(await settings.GetPlaybackAsync(ct), files.Count, (long)(seconds * BarsFile.BytesPerSecond), count, bytes);
    }

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        if (task.PayloadJson == Delete)
            await DeleteAsync(progress, ct);
        else
            await MakeAsync(progress, ct);
    }

    private async Task MakeAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Looking for songs without the visualiser's bars");
        var todo = (await SongsAsync(ct)).Where(s => File.Exists(s.Full) && !BarsFile.IsBars(BarsFile.SidecarOf(s.Full))).ToList();
        int done = 0, made = 0, failed = 0;
        string? why = null;
        foreach (var batch in todo.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            // Turned to on demand or off since: they're no longer wanted.
            if ((await settings.GetPlaybackAsync(ct)).Visualiser != VisualiserBars.WhenMatching)
            {
                progress.Report($"Stopped after {LibraryMatcher.Plural(made, "song", "songs")}: the visualiser's bars are no longer made when matching");
                return;
            }
            progress.Report($"Making the visualiser's bars: {done} of {LibraryMatcher.Plural(todo.Count, "song", "songs")}");
            var sidecars = new List<BarsSidecar>();
            foreach (var (path, full) in batch)
            {
                try
                {
                    // Any already made on demand are used.
                    sidecars.Add(new BarsSidecar(path, Convert.ToBase64String(await maker.GetAsync(full, null, ct))));
                }
                catch (BarsException e)
                {
                    failed++;
                    why ??= e.Message;
                    logger.LogWarning("Couldn't make the visualiser's bars for {Path}: {Reason}", path, e.Message);
                }
            }
            if (sidecars.Count > 0)
            {
                BarsResult result;
                try
                {
                    result = await worker.BarsAsync(new BarsRequest(sidecars), ct);
                }
                catch (Exception e) when (e is HttpRequestException or ImportException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // Those written so far are kept; the rest are made after the next scan.
                    throw new InvalidOperationException($"The beets worker couldn't write them: {e.Message}", e);
                }
                foreach (var written in result.Files)
                {
                    if (written.Error is null)
                        made++;
                    else
                    {
                        failed++;
                        why ??= written.Error;
                    }
                }
            }
            done += batch.Length;
        }
        progress.Report($"Made the visualiser's bars for {LibraryMatcher.Plural(made, "song", "songs")}"
                        + (failed > 0 ? $"; {failed} couldn't be made, to try again next scan ({why})" : ""));
    }

    private async Task DeleteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Looking for the visualiser's bars");
        var todo = (await SongsAsync(ct)).Where(s => File.Exists(BarsFile.SidecarOf(s.Full))).ToList();
        int deleted = 0, failed = 0;
        string? why = null;
        foreach (var batch in todo.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Deleting the visualiser's bars: {deleted} of {todo.Count}");
            BarsResult result;
            try
            {
                result = await worker.BarsAsync(new BarsRequest(batch.Select(s => new BarsSidecar(s.Path, null)).ToList()), ct);
            }
            catch (Exception e) when (e is HttpRequestException or ImportException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The beets worker couldn't delete them: {e.Message}", e);
            }
            foreach (var file in result.Files)
            {
                if (file.Error is null)
                    deleted++;
                else
                {
                    failed++;
                    why ??= file.Error;
                }
            }
        }
        progress.Report($"Deleted the visualiser's bars beside {LibraryMatcher.Plural(deleted, "song", "songs")}"
                        + (failed > 0 ? $"; {failed} couldn't be deleted ({why})" : ""));
    }
}
