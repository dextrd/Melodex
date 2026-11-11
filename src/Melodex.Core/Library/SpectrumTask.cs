using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Requests;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Checks the library's lossless files for lossy music passed off as lossless (#19): the beets worker finds where each
/// one's highs stop, and one that stops where a lossy encoder's do is suspect, so it counts as the quality it seems to
/// be. Files are only read. Each is checked once, and again when it changes; a scan queues the new ones.
/// </summary>
public sealed class SpectrumTask(MelodexDbContext db, IImportWorker worker) : ITaskHandler
{
    public const string TaskKind = "library-spectrum";
    public const string Title = "Checking lossless files";
    private const int BatchSize = 20;

    /// <summary>The codecs the worker can read for it: FLAC, WAV and AIFF. ALAC, APE and WavPack can't be checked.</summary>
    public static readonly string[] Checkable = ["flac", "wav", "aiff", "pcm"];

    public string Kind => TaskKind;

    /// <summary>Lossless files it can check, in the library.</summary>
    public static IQueryable<LibraryFile> Lossless(MelodexDbContext db) =>
        db.LibraryFiles.Where(f => f.Codec != null && Checkable.Contains(f.Codec) && f.HiddenAt == null);

    /// <summary>Those not checked since they last changed.</summary>
    public static IQueryable<LibraryFile> Unchecked(MelodexDbContext db) => Lossless(db).Where(f => f.SpectrumCheckedAt == null && f.TrustedAt == null);

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var todo = await Unchecked(db).OrderBy(f => f.Path).Select(f => f.Id).ToListAsync(ct);
        int done = 0, suspect = 0, unreadable = 0;
        progress.Report($"Checking lossless files: 0 of {todo.Count}");
        foreach (var batch in todo.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var files = await db.LibraryFiles.Where(f => batch.Contains(f.Id)).ToListAsync(ct);
            SpectrumResult found;
            try
            {
                found = await worker.SpectrumAsync(new SpectrumRequest(files.Select(f => new SpectrumFile(SpectrumFile.Library, f.Path)).ToList()), ct);
            }
            catch (Exception e) when (e is HttpRequestException or ImportException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Those checked so far are kept; the rest are checked next time.
                throw new InvalidOperationException($"The beets worker couldn't check them: {e.Message}", e);
            }
            var now = DateTime.UtcNow;
            foreach (var result in found.Files)
            {
                if (files.FirstOrDefault(f => f.Path == result.Path) is not { } file)
                    continue;
                // One that can't be told (too quiet, or unreadable) isn't asked about again until it changes.
                if (file.TrustedAt is not null)
                    continue;
                file.CutoffHz = result.CutoffHz;
                file.SpectrumCheckedAt = now;
                if (result.Error is not null)
                    unreadable++;
                else if (file.Quality.Suspect)
                    suspect++;
            }
            // Saved even if it's cancelled now: they've been checked.
            await db.SaveChangesAsync(CancellationToken.None);
            done += batch.Length;
            progress.Report($"Checking lossless files: {done} of {todo.Count}, {suspect} seem lossy");
        }
        progress.Report($"Checked {LibraryMatcher.Plural(done, "lossless file", "lossless files")}: "
                        + (suspect == 0 ? "none seem lossy" : $"{suspect} seem lossy")
                        + (unreadable > 0 ? $"; {unreadable} couldn't be told (too quiet or unreadable)" : ""));
    }

    /// <summary>How the library's lossless files stand, and those that seem lossy.</summary>
    public static async Task<SpectrumStatus> StatusAsync(MelodexDbContext db, CancellationToken ct)
    {
        var files = await Lossless(db)
            .Select(f => new { f.Id, f.Path, f.TrackId, f.Codec, f.BitrateKbps, f.BitDepth, f.SampleRate, f.CutoffHz, f.SpectrumCheckedAt, f.TrustedAt })
            .ToListAsync(ct);
        var suspectIds = files.Where(f => f.SpectrumCheckedAt != null && LibraryFile.QualityOf(f.Codec, f.BitrateKbps, f.BitDepth, f.SampleRate, f.CutoffHz).Suspect)
            .Select(f => f.Id).ToList();
        var suspects = await db.LibraryFiles.Where(f => suspectIds.Contains(f.Id)).ToListAsync(ct);
        // Those an upgrade is looking for a better copy of.
        var replacing = await db.Requests.Where(r => r.UpgradesFileId != null && suspectIds.Contains(r.UpgradesFileId.Value) && !RequestService.Finished.Contains(r.Status))
            .Select(r => r.UpgradesFileId!.Value).ToListAsync(ct);
        var listed = suspects.OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase).Select(f =>
        {
            var tags = FileTags.Parse(f.TagsJson);
            return new SuspectFile(f.Id, f.Path, f.TrackId, f.Quality, tags.Title, tags.Artist ?? tags.AlbumArtist, LocalMusic.CoverUrl(f.Id),
                f.Duration?.TotalSeconds, f.ModifiedAt, replacing.Contains(f.Id));
        }).ToList();
        return new SpectrumStatus(files.Count, files.Count(f => f.SpectrumCheckedAt != null || f.TrustedAt != null), listed);
    }

    /// <summary>Trusts lossless files that seem lossy to be what they say: they count as lossless, and aren't checked again.</summary>
    public static async Task<int> TrustAsync(MelodexDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        foreach (var file in files)
        {
            file.TrustedAt = DateTime.UtcNow;
            file.CutoffHz = null;
        }
        await db.SaveChangesAsync(ct);
        return files.Count;
    }
}
