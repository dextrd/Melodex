using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Events;
using Melodex.Core.Library;
using Melodex.Core.Listening;

namespace Melodex.Core.Api;

public static class ListenEndpoints
{
    public static RouteGroupBuilder MapListenEndpoints(this RouteGroupBuilder api)
    {
        var listen = api.MapGroup("/listen").WithTags("Listen now");

        listen.MapPost("", async (StartListen body, HttpContext http, ListenNowService service, CancellationToken ct) =>
            {
                try
                {
                    var items = await service.StartAsync(body, Caller.Of(http).Requester, ct);
                    return items is null ? Results.NotFound() : Results.Accepted(null, await service.ToInfoAsync(items, ct));
                }
                catch (ListenNowException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to listen to");
                }
            })
            .RequireScope(Scopes.Listen)
            .WithName("StartListening")
            .WithSummary("Fetch a track to listen to without adding it to the library, or an album's missing tracks in order. "
                         + "A track already on its way or playable is returned as it is. Refused (409) for tracks you have.")
            .Produces<IReadOnlyList<ListenNowInfo>>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        listen.MapGet("", (ListenNowService service, bool? all, CancellationToken ct) => service.ListAsync(all == true, ct))
            .WithName("ListListening")
            .WithSummary("Listen-now tracks still to play, fetch or add, newest first. With all=true, the latest 200 of every kind, "
                         + "including those discarded, expired or added to the library.");

        listen.MapGet("/{id:guid}", async (Guid id, MelodexDbContext db, ListenNowService service, CancellationToken ct) =>
                await db.ListenNowItems.FindAsync([id], ct) is { } item ? Results.Ok((await service.ToInfoAsync([item], ct))[0]) : Results.NotFound())
            .WithName("GetListening").WithSummary("One listen-now track, with a fresh stream URL while it's playable.")
            .Produces<ListenNowInfo>().Produces(StatusCodes.Status404NotFound);

        listen.MapGet("/{id:guid}/stream", async (Guid id, long? exp, string? sig, HttpContext http, MelodexDbContext db, ListenNowService service,
                StreamSigner signer, CancellationToken ct) =>
            {
                if (!signer.Verify(ListenNowService.StreamPath(id), exp ?? 0, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item || service.FullPath(item) is not { } file || !File.Exists(file))
                    return Results.NotFound();
                await service.TouchAsync(item, ct);
                return Results.File(file, MediaTypes.Bare(MediaTypes.For(file, item.Codec)), enableRangeProcessing: true);
            })
            .AllowAnyone()
            .WithName("StreamListening")
            .WithSummary("The file, with range requests for seeking. Needs the signature from its stream URL. Each play keeps it for the expiry setting from then.")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status206PartialContent).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

        listen.MapGet("/{id:guid}/bars", async (Guid id, long? exp, string? sig, bool? warm, HttpContext http, MelodexDbContext db, ListenNowService service,
                StreamSigner signer, BarsMaker maker, SettingsService settings, CancellationToken ct) =>
            {
                if (!signer.Verify(ListenNowService.StreamPath(id), exp ?? 0, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item || service.FullPath(item) is not { } file || !File.Exists(file))
                    return Results.NotFound();
                return await BarsAsync(file, null, warm == true, http, maker, settings, ct);
            })
            .AllowAnyone()
            .WithName("GetListeningBars")
            .WithSummary(BarsSummary + " Needs the signature from its stream URL.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream").Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        listen.MapPost("/{id:guid}/add", async (Guid id, MelodexDbContext db, ListenNowService service, CancellationToken ct) =>
            {
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item)
                    return Results.NotFound();
                try
                {
                    await service.AddAsync(item, ct);
                    return Results.Accepted(null, (await service.ToInfoAsync([item], ct))[0]);
                }
                catch (LibraryBusyException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "The library is being scanned");
                }
                catch (ListenNowException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Can't add it");
                }
            })
            .RequireScope(Scopes.Listen).WithName("AddListening")
            .WithSummary("Add a playable track to the library: it's filed by beets and announced to the media server, without fetching it again. "
                         + "Follow it until it's in the library, then use its media server track ID.")
            .Produces<ListenNowInfo>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        listen.MapDelete("/{id:guid}", async (Guid id, MelodexDbContext db, ListenNowService service, CancellationToken ct) =>
            {
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item)
                    return Results.NotFound();
                try
                {
                    await service.DiscardAsync(item, ct);
                    return Results.NoContent();
                }
                catch (ListenNowException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Can't discard it");
                }
            })
            .RequireScope(Scopes.Listen).WithName("DiscardListening").WithSummary("Stop fetching a track, or delete its file now instead of when it expires.");

        // ---- Tracks you have ----

        api.MapGet("/tracks/{id:guid}/play", async (Guid id, Guid? edition, MelodexDbContext db, StreamSigner signer, CancellationToken ct) =>
            {
                var file = await db.LibraryFiles.Where(f => f.TrackId == id)
                    .OrderByDescending(f => edition != null && f.EditionId == edition).ThenBy(f => f.Path)
                    .Select(f => new
                    {
                        f.Id, f.Path, f.Duration, f.EditionId, f.Track!.Title, f.Track.ArtistCredit, f.Track.ArtistId,
                        AlbumId = (Guid?)f.Edition!.AlbumId, AlbumTitle = f.Edition.Album.Title,
                    })
                    .FirstOrDefaultAsync(ct);
                if (file is null)
                    return Results.NotFound();
                var codec = MediaTypes.CodecFor(file.Path);
                return Results.Ok(new PlayInfo(id, file.Id, file.Title, file.ArtistCredit, file.ArtistId, file.AlbumId, file.AlbumTitle,
                    file.AlbumId is { } album ? LibraryView.CoverUrl(album, file.EditionId) : null, file.Duration?.TotalSeconds,
                    codec, MediaTypes.For(file.Path, codec), signer.Sign(LibraryStreamPath(file.Id))));
            })
            .WithTags("Library")
            .RequireScope(Scopes.Listen).WithName("PlayTrack").WithSummary("How to play a track you have: a signed stream URL for its file in your music folder. 404 if you don't have it.")
            .Produces<PlayInfo>().Produces(StatusCodes.Status404NotFound);

        api.MapGet("/library/files/{id:guid}/play", async (Guid id, MelodexDbContext db, StreamSigner signer, CancellationToken ct) =>
            {
                var file = await db.LibraryFiles.Include(f => f.Track).Include(f => f.Edition).ThenInclude(e => e!.Album)
                    .FirstOrDefaultAsync(f => f.Id == id && f.HiddenAt == null, ct);
                if (file is null)
                    return Results.NotFound();
                var tags = FileTags.Parse(file.TagsJson);
                var codec = MediaTypes.CodecFor(file.Path);
                var album = file.Edition?.Album;
                return Results.Ok(new PlayInfo(file.TrackId ?? file.Id, file.Id, file.Track?.Title ?? tags.Title ?? Path.GetFileNameWithoutExtension(file.Path),
                    file.Track?.ArtistCredit ?? tags.Artist ?? tags.AlbumArtist ?? "", file.Track?.ArtistId, album?.Id, album?.Title ?? tags.Album,
                    album is not null ? LibraryView.CoverUrl(album.Id, file.EditionId) : LocalMusic.CoverUrl(file.Id),
                    (file.Duration ?? file.Track?.Duration)?.TotalSeconds, codec, MediaTypes.For(file.Path, codec), signer.Sign(LibraryStreamPath(file.Id))));
            })
            .WithTags("Library")
            .RequireScope(Scopes.Listen).WithName("PlayFile")
            .WithSummary("How to play one file from your music folder, matched or not, such as your own music: a signed stream URL. "
                         + "Its track ID is the file's own when it isn't matched.")
            .Produces<PlayInfo>().Produces(StatusCodes.Status404NotFound);

        api.MapGet("/library/files/{id:guid}/bars", async (Guid id, long? exp, string? sig, bool? warm, HttpContext http, MelodexDbContext db,
                StreamSigner signer, BarsMaker maker, SettingsService settings, Microsoft.Extensions.Options.IOptions<MelodexOptions> options, CancellationToken ct) =>
            {
                if (!signer.Verify(LibraryStreamPath(id), exp ?? 0, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } row)
                    return Results.NotFound();
                var root = Path.GetFullPath(options.Value.LibraryPath);
                var file = Path.GetFullPath(Path.Combine(root, row.Path));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
                    return Results.NotFound();
                return await BarsAsync(file, BarsFile.SidecarOf(file), warm == true, http, maker, settings, ct);
            })
            .WithTags("Library")
            .AllowAnyone()
            .WithName("GetLibraryFileBars")
            .WithSummary(BarsSummary + " A .bars file beside it is used when there is one. Needs the signature from its stream URL.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream").Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/playback", (SettingsService s, CancellationToken ct) => s.GetPlaybackAsync(ct))
            .WithTags("Listen now").RequireScope(Scopes.Listen).WithName("GetPlayback")
            .WithSummary("How music is played: whether there's a visualiser, and when its bars are made.");

        api.MapGet("/library/files/{id:guid}/stream", async (Guid id, long? exp, string? sig, MelodexDbContext db, StreamSigner signer,
                Microsoft.Extensions.Options.IOptions<MelodexOptions> options, CancellationToken ct) =>
            {
                if (!signer.Verify(LibraryStreamPath(id), exp ?? 0, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } row)
                    return Results.NotFound();
                var root = Path.GetFullPath(options.Value.LibraryPath);
                var file = Path.GetFullPath(Path.Combine(root, row.Path));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
                    return Results.NotFound();
                return Results.File(file, MediaTypes.Bare(MediaTypes.For(file)), enableRangeProcessing: true);
            })
            .WithTags("Library")
            .AllowAnyone()
            .WithName("StreamLibraryFile").WithSummary("A file from your music folder, with range requests for seeking. Needs the signature from its stream URL.")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status206PartialContent).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

        // ---- Events ----

        api.MapGet("/events", async (HttpContext http, EventFeed feed, CancellationToken ct) =>
            {
                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                // Tells nginx and similar proxies not to hold events back.
                http.Response.Headers["X-Accel-Buffering"] = "no";
                using var subscription = feed.Subscribe();
                try
                {
                    await http.Response.WriteAsync(": connected\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    while (!ct.IsCancellationRequested)
                    {
                        using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        beat.CancelAfter(KeepAlive);
                        try
                        {
                            await subscription.Reader.WaitToReadAsync(beat.Token);
                            while (subscription.Reader.TryRead(out var message))
                                await http.Response.WriteAsync(
                                    (message.Id is null ? "" : $"id: {message.Id}\n") + $"event: {message.Type}\ndata: {message.Data}\n\n", ct);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Nothing for a while: a comment keeps proxies from closing the connection.
                            await http.Response.WriteAsync(": keep-alive\n\n", ct);
                        }
                        await http.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The client went away.
                }
            })
            .WithTags("System")
            .WithName("Events")
            .WithSummary("Server-sent events. Unnamed kinds say what changed (requests, listen, library, tasks), so you reload what you show; "
                         + "named events such as request.completed and listen.ready carry what happened, as webhooks do.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        return api;
    }

    private const string BarsSummary = "The visualiser's bars for the song, made now if they need to be: a gzipped .bars file (see the user guide). "
                                       + "With warm=true they're made without being sent, ahead of being played (202). 404 while the visualiser is off; "
                                       + "503 if they can't be made.";

    /// <summary>The song's bars, or with <paramref name="warm"/>, their making started for later.</summary>
    private static async Task<IResult> BarsAsync(string file, string? sidecar, bool warm, HttpContext http, BarsMaker maker, SettingsService settings,
        CancellationToken ct)
    {
        if ((await settings.GetPlaybackAsync(ct)).Visualiser == VisualiserBars.Off)
            return Results.NotFound();
        if (warm)
        {
            _ = maker.GetAsync(file, sidecar, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Results.Accepted();
        }
        byte[] bars;
        try
        {
            bars = await maker.GetAsync(file, sidecar, ct);
        }
        catch (BarsException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "The visualiser's bars couldn't be made");
        }
        http.Response.Headers.ContentEncoding = "gzip";
        http.Response.Headers.CacheControl = "private, max-age=43200";
        return Results.Bytes(bars, "application/octet-stream");
    }

    /// <summary>How often an idle event stream sends a comment, so proxies keep it open.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static string LibraryStreamPath(Guid fileId) => $"api/v1/library/files/{fileId}/stream";
}
