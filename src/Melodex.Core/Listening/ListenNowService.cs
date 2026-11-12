using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Requests;
using ListenNowState = Melodex.Core.Data.ListenNowState;

namespace Melodex.Core.Listening;

/// <summary>
/// Listen now: fetching tracks to play without adding them to the library. Each track is a request in
/// <see cref="RequestMode.ListenNow"/> mode, so it goes through the same retrieval plugins and checks as any other,
/// but ends with its file in the listen-now store instead of with beets. Adding it later turns the same request into
/// an ordinary one, which files the file it already has.
/// </summary>
/// <remarks>
/// While a track's request is in flight, its state follows the request (<see cref="StateOf"/>), so cancelling,
/// retrying or picking a candidate on the Activity page needs nothing extra. Only becoming playable, being added,
/// landing in the library and expiring are stored on the item itself.
/// </remarks>
public sealed class ListenNowService(
    MelodexDbContext db,
    RequestPlanner planner,
    RequestSignal signal,
    RequestService requests,
    CatalogueService catalogue,
    LibraryView view,
    LibraryReading reading,
    SettingsService settings,
    StreamSigner signer,
    IOptions<MelodexOptions> options,
    ILogger<ListenNowService> logger)
{
    private static readonly ListenNowState[] Gone = [ListenNowState.Expired, ListenNowState.InLibrary];

    public string Store => Path.GetFullPath(options.Value.ListenNowPath);

    /// <summary>
    /// Starts listening to a track, or to an album's missing tracks in order. A track already on its way or playable
    /// is returned as it is rather than fetched again.
    /// </summary>
    /// <exception cref="ListenNowException">You have the track, or nothing of the album is missing.</exception>
    public async Task<IReadOnlyList<ListenNowItem>?> StartAsync(StartListen start, string requester, CancellationToken ct)
    {
        switch (start.Kind)
        {
            case RequestKind.Track:
                if (await db.Tracks.FindAsync([start.Id], ct) is not { } track)
                    return null;
                if (await db.LibraryFiles.AnyAsync(f => f.TrackId == track.Id, ct))
                    throw new ListenNowException("You already have this track, so play it from your library.");
                var edition = start.EditionId ?? await planner.ChooseEditionForTrackAsync(track.Id, ct)
                    ?? throw new ListenNowException("This track isn't on any edition in the catalogue, so there's nothing to fetch it as.");
                return [await StartTrackAsync(track, edition, requester, ct)];

            case RequestKind.Album:
                if (await db.Albums.FindAsync([start.Id], ct) is not { } album)
                    return null;
                await catalogue.RefreshEditionsAsync(album, false, ct);
                var shown = (await view.GetAlbumAsync(album.Id, start.EditionId, ct))!;
                var editionId = start.EditionId ?? shown.OwnedEditionId ?? await planner.ChooseEditionAsync(album.Id, ct)
                    ?? throw new ListenNowException("The catalogue lists no editions of this album, so there's nothing to fetch.");
                if (editionId != shown.ShownEditionId)
                    shown = (await view.GetAlbumAsync(album.Id, editionId, ct))!;
                var missing = shown.Tracks.Where(t => t.Status == TrackStatus.Missing).ToList();
                // Played from a track: those from it on are wanted first.
                if (shown.Tracks.ToList().FindIndex(t => t.TrackId == start.FromTrackId) is var from and > 0)
                {
                    var later = shown.Tracks.Skip(from).Select(t => t.TrackId).ToHashSet();
                    missing = [.. missing.Where(t => later.Contains(t.TrackId)), .. missing.Where(t => !later.Contains(t.TrackId))];
                }
                if (missing.Count == 0)
                    throw new ListenNowException("You have every track of this album, so play it from your library.");
                var items = new List<ListenNowItem>();
                foreach (var listing in missing)
                    items.Add(await StartTrackAsync((await db.Tracks.FindAsync([listing.TrackId], ct))!, editionId, requester, ct));
                return items;

            default:
                throw new ListenNowException("Only tracks and albums can be listened to.");
        }
    }

    private async Task<ListenNowItem> StartTrackAsync(Track track, Guid editionId, string requester, CancellationToken ct)
    {
        var live = await db.ListenNowItems.Include(i => i.Request)
            .Where(i => i.TrackId == track.Id && !Gone.Contains(i.State)).OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
        if (live is not null && StateOf(live, live.Request) is not ListenNowState.Failed)
            return live;
        if (live is not null)
        {
            // Failed before: try again rather than keep a dead item around.
            await requests.RetryAsync(live.Request, ct);
            return live;
        }

        var request = new Request
        {
            TargetKind = EntityKind.Track,
            TargetId = track.Id,
            EditionId = editionId,
            Title = $"{track.Title} · {track.ArtistCredit}",
            Requester = requester,
            Mode = RequestMode.ListenNow,
        };
        var item = new ListenNowItem { Request = request, TrackId = track.Id, EditionId = editionId, State = ListenNowState.Requested };
        db.Requests.Add(request);
        db.ListenNowItems.Add(item);
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return item;
    }

    /// <summary>
    /// Adds a playable track to the library: its request becomes an ordinary one and is queued, and the pipeline
    /// files the file it already has.
    /// </summary>
    /// <exception cref="LibraryBusyException">A scan is reading the library.</exception>
    /// <exception cref="ListenNowException">It isn't playable, so there's no file to add.</exception>
    public async Task AddAsync(ListenNowItem item, CancellationToken ct)
    {
        var request = await db.Requests.FindAsync([item.RequestId], ct);
        if (StateOf(item, request) is ListenNowState.Importing or ListenNowState.InLibrary)
            return;
        if (StateOf(item, request) != ListenNowState.Playable || FullPath(item) is not { } file || !File.Exists(file))
            throw new ListenNowException("Only a track that's ready to play can be added.");
        if (await reading.IsBusyAsync(db, ct))
            throw new LibraryBusyException();

        request!.Mode = RequestMode.Permanent;
        request.Status = RequestStatus.Queued;
        request.Detail = "Adding to the library";
        request.UpdatedAt = DateTime.UtcNow;
        item.State = ListenNowState.Importing;
        await db.SaveChangesAsync(ct);
        signal.Wake();
    }

    /// <summary>Stops a fetch on its way, or deletes a playable file. Gone for good, as if it had expired.</summary>
    public async Task DiscardAsync(ListenNowItem item, CancellationToken ct)
    {
        var request = await db.Requests.FindAsync([item.RequestId], ct);
        var state = StateOf(item, request);
        if (state is ListenNowState.Importing or ListenNowState.InLibrary)
            throw new ListenNowException("It's being added to the library, so there's nothing to discard.");
        if (request is not null && !RequestService.Finished.Contains(request.Status))
            await requests.CancelAsync(request, ct);
        Expire(item);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A play: the file is kept for the expiry setting from now.</summary>
    public async Task TouchAsync(ListenNowItem item, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Players fetch a file in several ranges; once a minute is plenty.
        if (item.LastPlayedAt is { } last && now - last < TimeSpan.FromMinutes(1))
            return;
        item.LastPlayedAt = now;
        item.ExpiresAt = now + await ExpiryAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TimeSpan> ExpiryAsync(CancellationToken ct) =>
        TimeSpan.FromMinutes(Math.Clamp((await settings.GetFetchAsync(ct)).ListenNowExpiryMinutes, 5, 7 * 24 * 60));

    /// <summary>
    /// Housekeeping, run every minute: deletes files past their expiry, and those of tracks that reached the library
    /// some other way (requested with Get, or found by a scan).
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var live = await db.ListenNowItems.Include(i => i.Request).Where(i => !Gone.Contains(i.State)).ToListAsync(ct);
        var owned = (await db.LibraryFiles.Where(f => f.TrackId != null).Select(f => f.TrackId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var item in live)
        {
            var state = StateOf(item, item.Request);
            if (state == ListenNowState.InLibrary || state is ListenNowState.Playable or ListenNowState.Failed && owned.Contains(item.TrackId))
            {
                DeleteFile(item);
                item.State = ListenNowState.InLibrary;
            }
            else if (state == ListenNowState.Playable && item.ExpiresAt < now)
            {
                logger.LogInformation("[{Track}] Listen now expired; its file is deleted", item.Request.Title);
                Expire(item);
            }
            else if (state == ListenNowState.Failed && item.CreatedAt < now - TimeSpan.FromDays(1))
            {
                Expire(item);
            }
        }
        await db.SaveChangesAsync(ct);
        DeleteStrays();
    }

    /// <summary>Files in the store that no live item points at, such as those left by a crash.</summary>
    private void DeleteStrays()
    {
        if (!Directory.Exists(Store))
            return;
        var known = db.ListenNowItems.Where(i => i.FilePath != null && !Gone.Contains(i.State)).Select(i => i.FilePath!).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(Store))
        {
            // A file being stored is written before its item is saved; leave anything recent alone.
            if (!known.Contains(Path.GetFileName(file)) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - TimeSpan.FromHours(1))
                TryDelete(file);
        }
    }

    private void Expire(ListenNowItem item)
    {
        DeleteFile(item);
        item.State = ListenNowState.Expired;
        item.ExpiresAt = DateTime.UtcNow;
    }

    public void DeleteFile(ListenNowItem item)
    {
        if (FullPath(item) is { } file)
            TryDelete(file);
        item.FilePath = null;
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't delete the listen-now file {Path}: {Reason}", file, e.Message);
        }
    }

    /// <summary>The item's file in the store, if it has one. Never outside the store, whatever the database says.</summary>
    public string? FullPath(ListenNowItem item)
    {
        if (item.FilePath is null)
            return null;
        var full = Path.GetFullPath(Path.Combine(Store, item.FilePath));
        return full.StartsWith(Store + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// The state to report: what's stored once the track is playable or beyond, otherwise what its request is doing.
    /// An added track that couldn't be filed is playable again, so it can be retried.
    /// </summary>
    public static ListenNowState StateOf(ListenNowItem item, Request? request) => item.State switch
    {
        ListenNowState.Requested or ListenNowState.Fetching => request?.Status switch
        {
            null or RequestStatus.Queued => ListenNowState.Requested,
            RequestStatus.Playable => ListenNowState.Playable,
            RequestStatus.InLibrary => ListenNowState.InLibrary,
            RequestStatus.NeedsReview or RequestStatus.Failed or RequestStatus.Cancelled => ListenNowState.Failed,
            _ => ListenNowState.Fetching,
        },
        ListenNowState.Importing => request?.Status switch
        {
            RequestStatus.InLibrary => ListenNowState.InLibrary,
            RequestStatus.NeedsReview or RequestStatus.Failed or RequestStatus.Cancelled => ListenNowState.Playable,
            _ => ListenNowState.Importing,
        },
        var stored => stored,
    };

    /// <summary>
    /// The tracks fetched to listen to, newest first: those still to play, fetch or add, or with <paramref name="all"/>
    /// the latest 200 of every kind, including those discarded, expired or added to the library.
    /// </summary>
    public async Task<IReadOnlyList<ListenNowInfo>> ListAsync(bool all, CancellationToken ct)
    {
        var items = await db.ListenNowItems.Where(i => all || !Gone.Contains(i.State))
            .OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(ct);
        return await ToInfoAsync(items, ct);
    }

    public async Task<IReadOnlyList<ListenNowInfo>> ToInfoAsync(IReadOnlyList<ListenNowItem> items, CancellationToken ct)
    {
        var requestIds = items.Select(i => i.RequestId).ToList();
        var trackIds = items.Select(i => i.TrackId).ToList();
        var editionIds = items.Select(i => i.EditionId).OfType<Guid>().ToList();
        var requestsById = await db.Requests.Where(r => requestIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var tracks = await db.Tracks.Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var editions = await db.Editions.Where(e => editionIds.Contains(e.Id))
            .Select(e => new { e.Id, e.AlbumId, AlbumTitle = e.Album.Title }).ToDictionaryAsync(e => e.Id, ct);
        var listings = await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId) && trackIds.Contains(et.TrackId))
            .Select(et => new { et.EditionId, et.TrackId, et.Title, et.Duration }).ToListAsync(ct);
        var mediaServerIds = await db.LibraryFiles.Where(f => f.TrackId != null && trackIds.Contains(f.TrackId.Value) && f.MediaServerTrackId != null)
            .Select(f => new { TrackId = f.TrackId!.Value, f.MediaServerTrackId }).ToListAsync(ct);

        return items.Select(item =>
        {
            var request = requestsById.GetValueOrDefault(item.RequestId);
            var track = tracks[item.TrackId];
            var edition = item.EditionId is { } e ? editions.GetValueOrDefault(e) : null;
            var listing = listings.FirstOrDefault(l => l.EditionId == item.EditionId && l.TrackId == item.TrackId);
            var state = StateOf(item, request);
            var playable = state is ListenNowState.Playable or ListenNowState.Importing && item.FilePath is not null;
            return new ListenNowInfo(item.Id, item.TrackId, listing?.Title ?? track.Title, track.ArtistCredit, track.ArtistId,
                edition?.AlbumId, edition?.AlbumTitle, item.EditionId,
                edition is null ? null : LibraryView.CoverUrl(edition.AlbumId, item.EditionId),
                (Client.ListenNowState)(int)state, request?.Detail, (listing?.Duration ?? track.Duration)?.TotalSeconds,
                item.Codec, item.Container, item.BitrateKbps,
                item.FilePath is null ? null : MediaTypes.For(item.FilePath, item.Codec),
                playable ? signer.Sign(StreamPath(item.Id)) : null,
                state == ListenNowState.Playable ? item.ExpiresAt : null,
                item.RequestId,
                state == ListenNowState.InLibrary ? mediaServerIds.FirstOrDefault(m => m.TrackId == item.TrackId)?.MediaServerTrackId : null,
                item.CreatedAt,
                request?.Paused ?? false,
                request?.QueueOrder ?? 0);
        }).ToList();
    }

    public static string StreamPath(Guid itemId) => $"api/v1/listen/{itemId}/stream";
}

/// <summary>Listen now can't do what was asked; the message says why.</summary>
public sealed class ListenNowException(string message) : Exception(message);
