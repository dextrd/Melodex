using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Plugins;
using Melodex.Core.Requests;

namespace Melodex.Core.Library;

/// <summary>
/// Upgrades (#20): owned music below its cutoff is replaced by a better copy of the same song. Finds what's upgradable
/// and asks for upgrades, puts the better copy in the old file's place, tagged as it was, holds the old file in staging
/// so it can be put back, and says when the media server may not keep stars, plays and playlists across an upgrade.
/// </summary>
public sealed class Upgrades(
    MelodexDbContext db,
    SettingsService settings,
    RequestSignal signal,
    ConnectorPlugins connectors,
    IImportWorker worker,
    QualitySettings quality,
    IOptions<MelodexOptions> options,
    ILogger<Upgrades> logger)
{
    /// <summary>The oldest Navidrome that gives a song the same ID once its file is replaced, as long as its tags are the same.</summary>
    public static readonly Version KeepingNavidrome = new(0, 55, 0);

    /// <summary>Who scheduled upgrades are asked for by, on the Activity page.</summary>
    public const string ScheduledRequester = "Scheduled upgrades";

    /// <summary>The most a look through the whole library by hand asks for.</summary>
    public const int MostByHand = 500;

    // Upgrades go after anything asked for by hand, made before or after them: they're never in a hurry.
    private const double Later = 1e10;

    /// <summary>A matched file that's below its cutoff, and where its quality stands in the profile (higher is worse).</summary>
    public sealed record Upgradable(Guid FileId, Guid TrackId, Guid? EditionId, FileQuality Quality, int Position);

    /// <summary>
    /// The files in a set that can be upgraded: matched (not deferred, excluded or hidden), of a quality that's known,
    /// and below the cutoff that applies to them, by their song's, album's or artist's own setting or else the main
    /// profile's. Songs set never to be upgraded aren't. A lossless file that seems lossy is the quality it seems.
    /// </summary>
    public async Task<List<Upgradable>> UpgradableAsync(IQueryable<LibraryFile> files, CancellationToken ct)
    {
        var profile = await settings.GetQualityAsync(ct);
        var rows = await files
            .Where(f => f.TrackId != null && f.ExcludedAt == null && f.DeferredAt == null && f.HiddenAt == null && f.ReviewReason == null
                        && f.Codec != null && f.Codec != LibraryFile.UnknownCodec)
            .Select(f => new
            {
                f.Id, TrackId = f.TrackId!.Value, f.EditionId, f.Codec, f.BitrateKbps, f.BitDepth, f.SampleRate, f.CutoffHz,
                Track = f.Track!.QualityOverride, TrackArtist = f.Track.ArtistId,
                Album = f.EditionId == null ? null : f.Edition!.Album.QualityOverride,
                AlbumArtist = f.EditionId == null ? (Guid?)null : f.Edition!.Album.ArtistId,
            })
            .ToListAsync(ct);
        var artists = await db.Artists.Where(a => a.QualityOverride != null).ToDictionaryAsync(a => a.Id, a => a.QualityOverride!, ct);
        var upgradable = new List<Upgradable>();
        foreach (var row in rows)
        {
            var artist = (row.AlbumArtist ?? row.TrackArtist) is { } id ? artists.GetValueOrDefault(id) : null;
            var setting = new[] { row.Track, row.Album, artist }.FirstOrDefault(s => s == QualitySettings.Never || QualityStep.Parse(s) is not null);
            if (setting == QualitySettings.Never)
                continue;
            var fileQuality = LibraryFile.QualityOf(row.Codec, row.BitrateKbps, row.BitDepth, row.SampleRate, row.CutoffHz);
            if (profile.Position(fileQuality) is not { } position || profile.Meets(fileQuality, setting))
                continue;
            upgradable.Add(new Upgradable(row.Id, row.TrackId, row.EditionId, fileQuality, position));
        }
        return upgradable;
    }

    /// <summary>
    /// Asks for an upgrade of each upgradable file in a set that isn't being upgraded or fetched already: those looked
    /// for longest ago (or never) first, and the worst of those first, up to a limit. Files looked for since
    /// <paramref name="lookedBefore"/> are left for another time.
    /// </summary>
    public async Task<UpgradeSearch> SearchAsync(FormatScope scope, int? limit, string requester, CancellationToken ct, DateTime? lookedBefore = null)
    {
        var upgradable = await UpgradableAsync(FormatTask.Files(db, scope), ct);
        var ids = upgradable.Select(u => u.FileId).ToList();
        var tracks = upgradable.Select(u => u.TrackId).ToList();
        var busy = (await db.Requests.Where(r => !RequestService.Finished.Contains(r.Status) && r.Mode == RequestMode.Permanent
                                                 && (r.UpgradesFileId != null && ids.Contains(r.UpgradesFileId.Value)
                                                     || r.TargetKind == EntityKind.Track && tracks.Contains(r.TargetId)))
            .Select(r => new { r.UpgradesFileId, r.TargetId }).ToListAsync(ct));
        var looked = await db.Requests.Where(r => r.UpgradesFileId != null && ids.Contains(r.UpgradesFileId.Value))
            .GroupBy(r => r.UpgradesFileId!.Value).Select(g => new { g.Key, Last = g.Max(r => r.CreatedAt) })
            .ToDictionaryAsync(g => g.Key, g => g.Last, ct);
        var wanted = upgradable
            .Where(u => !busy.Any(b => b.UpgradesFileId == u.FileId || b.TargetId == u.TrackId))
            .Where(u => lookedBefore is null || looked.GetValueOrDefault(u.FileId) < lookedBefore)
            .OrderBy(u => looked.GetValueOrDefault(u.FileId))
            .ThenByDescending(u => u.Position)
            .Take(limit ?? int.MaxValue)
            .ToList();

        var titles = await db.Tracks.Where(t => tracks.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title + " · " + t.ArtistCredit, ct);
        var order = QueueOrders.Now() + Later;
        foreach (var file in wanted)
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track,
                TargetId = file.TrackId,
                EditionId = file.EditionId,
                UpgradesFileId = file.FileId,
                Title = titles.GetValueOrDefault(file.TrackId, "A song"),
                Requester = requester,
                Mode = RequestMode.Permanent,
                QueueOrder = order,
            });
        await db.SaveChangesAsync(ct);
        if (wanted.Count > 0)
        {
            signal.Wake();
            logger.LogInformation("Looking for upgrades of {Count} of {Upgradable} songs below their cutoff", wanted.Count, upgradable.Count);
        }
        return new UpgradeSearch(upgradable.Count, wanted.Count);
    }

    /// <summary>
    /// The scheduled look through the whole library, run every minute. While a window is open it keeps as many upgrades
    /// going as fetches run at once, asking for more as they finish, until it's asked for as many as a look may, there
    /// are none left, or the window closes; then those not started yet are cancelled. Returns how many it asked for.
    /// </summary>
    public async Task<int> RunScheduledAsync(DateTime utcNow, CancellationToken ct)
    {
        var upgrades = await settings.GetUpgradesAsync(ct);
        var run = await settings.GetUpgradeRunAsync(ct);
        var window = upgrades.Scheduled ? UpgradeWindows.Current(upgrades, utcNow) : null;
        if (window is not { } open)
        {
            if (run.Window is not null && !run.Closed)
            {
                var cancelled = await db.Requests.Where(r => r.Requester == ScheduledRequester && r.Status == RequestStatus.Queued)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RequestStatus.Cancelled)
                        .SetProperty(r => r.Detail, "Not started before the upgrade window closed").SetProperty(r => r.UpdatedAt, utcNow), ct);
                await settings.SaveUpgradeRunAsync(run with { Closed = true }, ct);
                logger.LogInformation("The upgrade window closed, having asked for {Asked} upgrades ({Cancelled} not started)", run.Asked, cancelled);
            }
            return 0;
        }
        if (run.Window != open.Start)
            run = new UpgradeRun { Window = open.Start };
        if (run.Done)
            return 0;

        var active = await db.Requests.CountAsync(r => r.Requester == ScheduledRequester && !RequestService.Finished.Contains(r.Status), ct);
        var slots = Math.Clamp((await settings.GetFetchAsync(ct)).ParallelFetches, 1, 8) - active;
        var take = upgrades.PerRun is { } most ? Math.Min(slots, Math.Clamp(most, 1, 500) - run.Asked) : slots;
        var queued = 0;
        if (upgrades.PerRun is { } limit && run.Asked >= Math.Clamp(limit, 1, 500))
            run = run with { Done = true };
        else if (take > 0)
        {
            queued = (await SearchAsync(new FormatScope(FormatScopeKind.Library), take, ScheduledRequester, ct, lookedBefore: open.Start)).Queued;
            run = run with { Asked = run.Asked + queued, Done = queued == 0 && active == 0 };
        }
        await settings.SaveUpgradeRunAsync(run, ct);
        return queued;
    }

    public async Task<UpgradeStatus> StatusAsync(CancellationToken ct)
    {
        var upgrades = await settings.GetUpgradesAsync(ct);
        var now = DateTime.UtcNow;
        var run = await settings.GetUpgradeRunAsync(ct);
        var running = upgrades.Scheduled && UpgradeWindows.Current(upgrades, now) is { } open && !(run.Window == open.Start && run.Done);
        DateTime? next = upgrades.Scheduled ? UpgradeWindows.Next(upgrades, now) : null;
        var upgradable = await UpgradableAsync(db.LibraryFiles, ct);
        return new UpgradeStatus(upgrades, await WarningAsync(ct), next, upgradable.Count, await HeldAsync(null, ct), running);
    }

    /// <summary>Files held after an upgrade, newest first, of one song or all.</summary>
    public async Task<IReadOnlyList<HeldFileInfo>> HeldAsync(Guid? trackId, CancellationToken ct)
    {
        var held = await db.HeldFiles.Where(h => trackId == null || h.TrackId == trackId).OrderByDescending(h => h.HeldAt).ToListAsync(ct);
        var fileIds = held.Select(h => h.LibraryFileId).OfType<Guid>().ToList();
        var now = await db.LibraryFiles.Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        return held.Select(h =>
        {
            var better = h.LibraryFileId is { } id ? now.GetValueOrDefault(id) : null;
            // The better copy's cover, which is the old one's: it took its tags and art.
            return new HeldFileInfo(h.Id, h.TrackId, h.Title, h.Path, h.ReplacedBy, h.Quality, better?.Quality, h.HeldAt, h.DeleteAt,
                better is null ? null : LocalMusic.CoverUrl(better.Id), (better?.Duration ?? h.Duration)?.TotalSeconds);
        }).ToList();
    }

    /// <summary>
    /// Why an upgrade may lose a song's stars, ratings, play counts and playlists in the media server, or null when it
    /// keeps them: Navidrome 0.55 and later gives a song the same ID when its file is replaced by one tagged the same.
    /// </summary>
    public async Task<string?> WarningAsync(CancellationToken ct)
    {
        const string keeps = "Navidrome 0.55 or later keeps a song's stars, ratings, play counts and playlists when it's upgraded";
        if (await connectors.GetActiveAsync(ct) is not { } active)
            return $"No media server is connected, so Melodex can't tell whether the app you listen with keeps what you've done with a song when it's upgraded. {keeps}; other apps may treat it as a new song.";
        ServerIdentity? server = null;
        try
        {
            if (active.Connector is IServerIdentity identity)
                server = await identity.GetServerAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug("Couldn't ask {Server} what it is: {Reason}", active.Instance.Name, e.Message);
        }
        if (server?.Kind != "navidrome")
            return server is null && active.Connector is IServerIdentity
                ? $"Couldn't reach {active.Instance.Name} to tell which version it is. {keeps}."
                : $"{active.Instance.Name} isn't Navidrome, so an upgraded song may be a new song to it, without its stars, ratings, play counts and playlists. {keeps}.";
        if (NavidromeVersion(server.Version) is not { } version)
            return $"Couldn't tell which version of Navidrome {active.Instance.Name} is. {keeps}.";
        return version < KeepingNavidrome
            ? $"{active.Instance.Name} is Navidrome {version.ToString(3)}, which makes an upgraded song a new one: its stars, ratings, play counts and playlists are lost. {keeps}."
            : null;
    }

    /// <summary>Navidrome's version from what it reports, such as <c>0.64.2 (4d1e8c42)</c>.</summary>
    public static Version? NavidromeVersion(string? reported)
    {
        var digits = new string((reported ?? "").TrimStart('v').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (!Version.TryParse(digits.Count(c => c == '.') == 0 ? digits + ".0" : digits, out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>
    /// Puts a checked better copy, in staging, in place of the file it upgrades, and records it: the library file keeps
    /// its song and media server ID, with the copy's path and quality. The old file is held, or deleted, as set.
    /// </summary>
    /// <param name="cutoff">Where the copy's highs stop, when its spectrum was checked: <c>(true, hz)</c>.</param>
    public async Task<LibraryFile> ReplaceAsync(Request request, LibraryFile old, string staged, (bool Checked, int? Hz) cutoff, CancellationToken ct)
    {
        var upgrades = await settings.GetUpgradesAsync(ct);
        var held = new HeldFile
        {
            LibraryFileId = old.Id, TrackId = old.TrackId, RequestId = request.Id, Title = request.Title, Path = old.Path, HeldPath = "", ReplacedBy = "",
            Codec = old.Codec, BitrateKbps = old.BitrateKbps, BitDepth = old.BitDepth, SampleRate = old.SampleRate, CutoffHz = old.CutoffHz,
            TagsJson = old.TagsJson, Size = old.Size, Duration = old.Duration,
            DeleteAt = DateTime.UtcNow.AddDays(Math.Clamp(upgrades.HoldDays, 1, 365)),
        };
        var keep = upgrades.OldFiles == OldFiles.Hold;
        var replaced = await worker.ReplaceAsync(new ReplaceRequest(staged, old.Path, keep ? $"held/{held.Id:N}" : null), ct);

        var path = replaced.Path.Replace('\\', '/');
        var full = Path.Combine(Path.GetFullPath(options.Value.LibraryPath), path);
        old.Path = path;
        old.Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        old.Size = replaced.Size;
        old.ScannedAt = DateTime.UtcNow;
        if (File.Exists(full))
        {
            old.ModifiedAt = new FileInfo(full).LastWriteTimeUtc;
            try
            {
                var (tags, duration, lyrics, read) = LibraryScanner.ReadTags(full);
                old.TagsJson = tags.ToJson();
                old.SetQuality(read);
                old.Duration = duration ?? old.Duration;
                old.Lyrics = lyrics;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't read the upgraded file {Path}: {Reason}", path, e.Message);
            }
        }
        if (cutoff.Checked)
        {
            old.CutoffHz = cutoff.Hz;
            old.SpectrumCheckedAt = DateTime.UtcNow;
        }
        if (replaced.Held is { } heldPath)
        {
            held.HeldPath = heldPath;
            held.ReplacedBy = path;
            db.HeldFiles.Add(held);
        }
        await db.SaveChangesAsync(ct);
        return old;
    }

    /// <summary>
    /// Puts a held file back in place of the copy that replaced it, which is deleted, and sets its song never to be
    /// upgraded, so it isn't replaced again. False when it isn't held.
    /// </summary>
    public async Task<bool> RestoreAsync(Guid id, CancellationToken ct)
    {
        if (await db.HeldFiles.FindAsync([id], ct) is not { } held)
            return false;
        var file = held.LibraryFileId is { } fileId ? await db.LibraryFiles.FindAsync([fileId], ct) : null;
        await worker.RestoreAsync(new RestoreRequest(held.HeldPath, held.Path, file?.Path ?? held.ReplacedBy), ct);
        if (file is not null)
        {
            file.Path = held.Path;
            file.Format = Path.GetExtension(held.Path).TrimStart('.').ToLowerInvariant();
            file.SetQuality(held.Quality);
            if (held.CutoffHz is not null)
                file.SpectrumCheckedAt = DateTime.UtcNow;
            file.TagsJson = held.TagsJson;
            file.Size = held.Size;
            file.Duration = held.Duration;
            var full = Path.Combine(Path.GetFullPath(options.Value.LibraryPath), held.Path);
            if (File.Exists(full))
                file.ModifiedAt = new FileInfo(full).LastWriteTimeUtc;
            file.ScannedAt = DateTime.UtcNow;
        }
        db.HeldFiles.Remove(held);
        await db.SaveChangesAsync(ct);
        if (held.TrackId is { } trackId)
            await quality.SaveAsync(EntityKind.Track, trackId, new QualityOverride(QualityOverrideKind.NeverUpgrade), ct);
        logger.LogInformation("Put back {Path}, which an upgrade had replaced", held.Path);

        if (await connectors.GetActiveAsync(ct) is { } active)
            try
            {
                await active.Connector.TriggerScanAsync([held.Path], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't tell {Server} a file was put back: {Reason}", active.Instance.Name, e.Message);
            }
        return true;
    }

    /// <summary>Deletes a held file now. False when it isn't held.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.HeldFiles.FindAsync([id], ct) is not { } held)
            return false;
        DeleteHeld(held);
        db.HeldFiles.Remove(held);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deletes held files whose time is up.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var due = await db.HeldFiles.Where(h => h.DeleteAt <= DateTime.UtcNow).ToListAsync(ct);
        foreach (var held in due)
            DeleteHeld(held);
        db.HeldFiles.RemoveRange(due);
        await db.SaveChangesAsync(ct);
        if (due.Count > 0)
            logger.LogInformation("Deleted {Count} files upgrades replaced, held long enough", due.Count);
    }

    private void DeleteHeld(HeldFile held)
    {
        var staging = Path.GetFullPath(options.Value.StagingPath);
        var full = Path.GetFullPath(Path.Combine(staging, held.HeldPath));
        if (!full.StartsWith(staging + Path.DirectorySeparatorChar))
            return;
        try
        {
            File.Delete(full);
            var folder = Path.GetDirectoryName(full)!;
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't delete the held file {Path}: {Reason}", held.HeldPath, e.Message);
        }
    }
}

/// <summary>Looks through the library for upgrades in their window, when that's on, and deletes held files whose time is up.</summary>
public sealed class UpgradeScheduler(IServiceScopeFactory scopes, ILogger<UpgradeScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var upgrades = scope.ServiceProvider.GetRequiredService<Upgrades>();
                await upgrades.SweepAsync(stoppingToken);
                await upgrades.RunScheduledAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't look for upgrades: {Message}", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
