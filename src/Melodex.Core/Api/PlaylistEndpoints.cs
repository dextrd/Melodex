using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Playlists;
using Melodex.Core.Plugins;

namespace Melodex.Core.Api;

public static class PlaylistEndpoints
{
    public static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder api)
    {
        var playlists = api.MapGroup("/playlists").WithTags("Playlists").RequireScope(Scopes.Listen);

        playlists.MapGet("", async (HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.ListAsync(user, ct)) : NoOwner())
            .WithName("ListPlaylists")
            .WithSummary("Your playlists, by name. Folders are part of the name, split by the delimiter you chose.")
            .Produces<IReadOnlyList<PlaylistSummary>>();

        playlists.MapPost("", async (CreatePlaylist body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => { var made = await service.CreateAsync(user, body, ct); return Results.Created($"/api/v1/playlists/{made.Playlist.Id}", made); })
                    : NoOwner())
            .WithName("CreatePlaylist")
            .WithSummary("Make a playlist, empty or with songs from your library.")
            .Produces<PlaylistDetail>(StatusCodes.Status201Created).ProducesValidationProblem();

        playlists.MapGet("/sync", async (HttpContext http, MelodexDbContext db, PlaylistSync sync, CancellationToken ct) =>
                await db.Users.FindAsync([Caller.Of(http).UserId], ct) is { } user ? Results.Ok(await sync.StatusAsync(user, ct)) : NoOwner())
            .WithName("GetPlaylistSync")
            .WithSummary("Whether your playlists are synced with the media server, and how the last sync went.")
            .Produces<PlaylistSyncStatus>();

        playlists.MapPost("/sync", async (HttpContext http, MelodexDbContext db, PlaylistSync sync, CancellationToken ct) =>
                await db.Users.FindAsync([Caller.Of(http).UserId], ct) is { } user ? Results.Ok(await sync.SyncAsync(user, ct)) : NoOwner())
            .WithName("SyncPlaylists")
            .WithSummary("Sync your playlists with the media server now, rather than waiting. Does nothing unless they're synced.")
            .Produces<PlaylistSyncStatus>();

        playlists.MapPut("/folders", async (RenamePlaylistFolder body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await Refusable(async () => { await service.RenameFolderAsync(user, body, ct); return Results.NoContent(); }) : NoOwner())
            .WithName("RenamePlaylistFolder")
            .WithSummary("Rename a folder of playlists, or with an empty new name take what's in it out of the folder.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        playlists.MapGet("/{id:guid}", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.GetAsync(user, id, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound() : NoOwner())
            .WithName("GetPlaylist")
            .WithSummary("A playlist and its songs in order, with whether each is in your library, a file of your own, or only on the media server.")
            .Produces<PlaylistDetail>().Produces(StatusCodes.Status404NotFound);

        playlists.MapPut("/{id:guid}", async (Guid id, UpdatePlaylist body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => await service.UpdateAsync(user, id, body, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound())
                    : NoOwner())
            .WithName("UpdatePlaylist")
            .WithSummary("Rename a playlist, which moves it between folders, or change its comment.")
            .Produces<PlaylistSummary>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        playlists.MapDelete("/{id:guid}", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.DeleteAsync(user, id, ct) ? Results.NoContent() : Results.NotFound() : NoOwner())
            .WithName("DeletePlaylist")
            .WithSummary("Delete a playlist, from the media server too when playlists are synced.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        playlists.MapPost("/{id:guid}/songs", async (Guid id, AddToPlaylist body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.AddAsync(user, id, body, ct) is { } added ? Results.Ok(added) : Results.NotFound() : NoOwner())
            .WithName("AddToPlaylist")
            .WithSummary("Add songs from your library to the end of a playlist. Songs already in it are left out unless duplicates=true.")
            .Produces<PlaylistAdded>().Produces(StatusCodes.Status404NotFound);

        playlists.MapPut("/{id:guid}/songs", async (Guid id, ArrangePlaylist body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => await service.ArrangeAsync(user, id, body, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound())
                    : NoOwner())
            .WithName("ArrangePlaylist")
            .WithSummary("Reorder a playlist's songs, or remove some: give the entries to keep, in their new order.")
            .Produces<PlaylistDetail>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        playlists.MapPut("/{id:guid}/pin", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.PinAsync(user, id, true, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound() : NoOwner())
            .WithName("PinPlaylist").WithSummary("Pin a playlist, to show it first. Pins are yours alone: the media server doesn't hear of them.")
            .Produces<PlaylistSummary>().Produces(StatusCodes.Status404NotFound);
        playlists.MapDelete("/{id:guid}/pin", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.PinAsync(user, id, false, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound() : NoOwner())
            .WithName("UnpinPlaylist").Produces<PlaylistSummary>().Produces(StatusCodes.Status404NotFound);

        playlists.MapGet("/{id:guid}/cover", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await service.CoverAsync(user, id, ct) is { } cover ? Picture(http, cover) : Results.NotFound()
                    : NoOwner())
            .WithName("GetPlaylistCover").WithSummary("A playlist's own picture, if it has one; without, its albums' covers stand in for it.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status404NotFound);
        playlists.MapPut("/{id:guid}/cover", async (Guid id, SetPlaylistCover body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => await service.SetCoverAsync(user, id, body.Upload, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound())
                    : NoOwner())
            .WithName("SetPlaylistCover")
            .WithSummary("Give a playlist a picture of its own: base64, up to 10 MB, JPEG, PNG, WebP or GIF. When playlists are synced, the media "
                         + "server gets it too, if it takes playlist pictures; one given there comes here the same way.")
            .Produces<PlaylistSummary>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);
        playlists.MapDelete("/{id:guid}/cover", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.SetCoverAsync(user, id, null, ct) is { } playlist ? Results.Ok(playlist) : Results.NotFound() : NoOwner())
            .WithName("RemovePlaylistCover").WithSummary("Take a playlist's own picture away, here and on the media server, so its albums' covers stand in again.")
            .Produces<PlaylistSummary>().Produces(StatusCodes.Status404NotFound);

        playlists.MapGet("/folders", async (HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.FoldersAsync(user, ct)) : NoOwner())
            .WithName("ListPlaylistFolders").WithSummary("Your folders of playlists that have pictures of their own. Folders are parts of playlists' names.")
            .Produces<IReadOnlyList<PlaylistFolderInfo>>();
        playlists.MapGet("/folders/{id:guid}/cover", async (Guid id, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await service.FolderCoverAsync(user, id, ct) is { } cover ? Picture(http, cover) : Results.NotFound()
                    : NoOwner())
            .WithName("GetPlaylistFolderCover").WithSummary("A folder's picture.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status404NotFound);
        playlists.MapPut("/folders/cover", async (SetPlaylistFolderCover body, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await Refusable(async () => Results.Ok(await service.SetFolderCoverAsync(user, body.Path, body.Upload, ct))) : NoOwner())
            .WithName("SetPlaylistFolderCover")
            .WithSummary("Give a folder of playlists a picture of its own: base64, up to 10 MB, JPEG, PNG, WebP or GIF. It stays here, as media servers "
                         + "have no folders.")
            .Produces<PlaylistFolderInfo>().ProducesValidationProblem();
        playlists.MapDelete("/folders/cover", async (string path, HttpContext http, PlaylistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await Refusable(async () => Results.Ok(await service.SetFolderCoverAsync(user, path, null, ct))) : NoOwner())
            .WithName("RemovePlaylistFolderCover").WithSummary("Take a folder's picture away, so the folder icon stands for it again.")
            .Produces<PlaylistFolderInfo>().ProducesValidationProblem();

        MapLogins(api);
        return api;
    }

    // A picture's address changes with it, so the browser keeps it as long as it likes.
    private static IResult Picture(HttpContext http, (string Path, string ContentType) cover)
    {
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(cover.Path, cover.ContentType);
    }

    // Your own media server login, and the admin's way of setting someone else's.
    private static void MapLogins(RouteGroupBuilder api)
    {
        api.MapGet("/auth/media-servers", (HttpContext http, MelodexDbContext db, ConnectorPlugins connectors, CancellationToken ct) =>
                AccountsAsync(Caller.Of(http).UserId, db, connectors, ct))
            .RequireScope(Scopes.SignedIn).WithTags("Access")
            .WithName("ListMediaServerAccounts")
            .WithSummary("Your login on each media server that syncs playlists, the one in use or not: each keeps its own, for switching between them.")
            .Produces<List<MediaServerAccount>>();

        api.MapPut("/auth/media-servers/{serverId:guid}", (Guid serverId, LinkMediaServer body, HttpContext http, MelodexDbContext db, ConnectorPlugins connectors,
                PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct) => LinkAccountAsync(Caller.Of(http).UserId, serverId, body, db, connectors, sync, syncer, ct))
            .RequireScope(Scopes.SignedIn).WithTags("Access")
            .WithName("LinkMediaServerAccount")
            .WithSummary("Link your login on one media server, in use or not. It's checked there first, and kept encrypted.")
            .Produces<MediaServerAccount>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        api.MapDelete("/auth/media-servers/{serverId:guid}", (Guid serverId, HttpContext http, MelodexDbContext db, CancellationToken ct) =>
                UnlinkAccountAsync(Caller.Of(http).UserId, serverId, db, ct))
            .RequireScope(Scopes.SignedIn).WithTags("Access")
            .WithName("UnlinkMediaServerAccount")
            .WithSummary("Forget your login on one media server. Your playlists stay in both places, but no longer change together while it's in use.")
            .Produces(StatusCodes.Status204NoContent);

        api.MapGet("/users/{id:guid}/media-servers", (Guid id, MelodexDbContext db, ConnectorPlugins connectors, CancellationToken ct) => AccountsAsync(id, db, connectors, ct))
            .RequireScope(Scopes.Users).WithTags("Access")
            .WithName("ListUserMediaServerAccounts")
            .WithSummary("Someone's login on each media server that syncs playlists.")
            .Produces<List<MediaServerAccount>>().Produces(StatusCodes.Status404NotFound);

        api.MapPut("/users/{id:guid}/media-servers/{serverId:guid}", (Guid id, Guid serverId, LinkMediaServer body, MelodexDbContext db, ConnectorPlugins connectors,
                PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct) => LinkAccountAsync(id, serverId, body, db, connectors, sync, syncer, ct))
            .RequireScope(Scopes.Users).WithTags("Access")
            .WithName("LinkUserMediaServerAccount")
            .WithSummary("Link someone's login on one media server for them, in use or not.")
            .Produces<MediaServerAccount>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        api.MapDelete("/users/{id:guid}/media-servers/{serverId:guid}", (Guid id, Guid serverId, MelodexDbContext db, CancellationToken ct) =>
                UnlinkAccountAsync(id, serverId, db, ct))
            .RequireScope(Scopes.Users).WithTags("Access")
            .WithName("UnlinkUserMediaServerAccount")
            .WithSummary("Forget someone's login on one media server.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        api.MapPut("/auth/media-server", (LinkMediaServer body, HttpContext http, MelodexDbContext db, PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct) =>
                LinkAsync(Caller.Of(http).UserId, body, db, sync, syncer, ct))
            .RequireScope(Scopes.SignedIn).WithTags("Access")
            .WithName("LinkMediaServer")
            .WithSummary("Link your login on the media server, so your playlists are synced with it. It's checked first, and kept encrypted.")
            .Produces<PlaylistSyncStatus>().ProducesValidationProblem();

        api.MapDelete("/auth/media-server", (HttpContext http, MelodexDbContext db, CancellationToken ct) => UnlinkAsync(Caller.Of(http).UserId, db, ct))
            .RequireScope(Scopes.SignedIn).WithTags("Access")
            .WithName("UnlinkMediaServer")
            .WithSummary("Stop syncing your playlists. They stay in Melodex and on the media server, but no longer change together.")
            .Produces(StatusCodes.Status204NoContent);

        api.MapPut("/users/{id:guid}/media-server", (Guid id, LinkMediaServer body, MelodexDbContext db, PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct) =>
                LinkAsync(id, body, db, sync, syncer, ct))
            .RequireScope(Scopes.Users).WithTags("Access")
            .WithName("LinkUserMediaServer")
            .WithSummary("Link someone's login on the media server for them, so their playlists are synced.")
            .Produces<PlaylistSyncStatus>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        api.MapDelete("/users/{id:guid}/media-server", (Guid id, MelodexDbContext db, CancellationToken ct) => UnlinkAsync(id, db, ct))
            .RequireScope(Scopes.Users).WithTags("Access")
            .WithName("UnlinkUserMediaServer")
            .WithSummary("Stop syncing someone's playlists.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> LinkAsync(Guid? userId, LinkMediaServer body, MelodexDbContext db, PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct)
    {
        if (await db.Users.FindAsync([userId], ct) is not { } user)
            return Results.NotFound();
        if (await sync.ServerAsync(ct) is not { } server)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["server"] = ["The media server doesn't sync playlists: tick Sync playlists in its plugin's settings first."],
            });
        return await KeepAsync(user, server.Instance, server.Playlists, body, db, sync, syncer, ct) ?? Results.Ok(await sync.StatusAsync(user, ct));
    }

    // Checks a login on the media server and keeps it, or says why not.
    private static async Task<IResult?> KeepAsync(User user, PluginInstance server, IPlaylistSync playlists, LinkMediaServer body, MelodexDbContext db,
        PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = [$"Give the username and password you sign in to {server.Name} with."] });

        var login = new ServerLogin(body.Username.Trim(), body.Password);
        HealthReport check;
        try
        {
            check = await playlists.CheckLoginAsync(login, ct);
        }
        catch (HttpRequestException e)
        {
            check = new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
        if (check.Status == HealthStatus.Unhealthy)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [check.Message ?? "That login doesn't work."] });

        // A different login is a different set of playlists: what was linked to the old one starts again.
        var linked = await sync.LinkedAsync(user.Id, server.Id, ct);
        if (!string.Equals(linked?.Username, login.Username, StringComparison.OrdinalIgnoreCase))
            await ForgetAsync(db, user.Id, server.Id, ct);
        if (linked is null)
            db.MediaServerLogins.Add(linked = new MediaServerLogin { UserId = user.Id, PluginInstanceId = server.Id, Username = login.Username, Password = "" });
        linked.Username = login.Username;
        linked.Password = sync.Protect(login.Password);
        if (server.Enabled)
            user.PlaylistSyncError = null;
        await db.SaveChangesAsync(ct);
        if (server.Enabled)
            syncer.Changed(user.Id);
        return null;
    }

    // Their login on each media server that syncs playlists.
    private static async Task<IResult> AccountsAsync(Guid? userId, MelodexDbContext db, ConnectorPlugins connectors, CancellationToken ct)
    {
        if (await db.Users.FindAsync([userId], ct) is not { } user)
            return Results.NotFound();
        var logins = await db.MediaServerLogins.Where(l => l.UserId == user.Id).ToDictionaryAsync(l => l.PluginInstanceId, l => l.Username, ct);
        return Results.Ok((await connectors.SyncingPlaylistsAsync(ct))
            .Select(s => new MediaServerAccount(s.Instance.Id, s.Instance.Name, s.Instance.Enabled, logins.GetValueOrDefault(s.Instance.Id))).ToList());
    }

    private static async Task<IResult> LinkAccountAsync(Guid? userId, Guid serverId, LinkMediaServer body, MelodexDbContext db, ConnectorPlugins connectors,
        PlaylistSync sync, PlaylistSyncer syncer, CancellationToken ct)
    {
        if (await db.Users.FindAsync([userId], ct) is not { } user)
            return Results.NotFound();
        if ((await connectors.SyncingPlaylistsAsync(ct)).FirstOrDefault(s => s.Instance.Id == serverId) is not ({ } server, { } playlists))
            return await db.PluginInstances.AnyAsync(p => p.Id == serverId && p.Type == PluginType.Connector, ct)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["server"] = ["That media server doesn't sync playlists: tick Sync playlists in its plugin's settings first."] })
                : Results.NotFound();
        return await KeepAsync(user, server, playlists, body, db, sync, syncer, ct)
               ?? Results.Ok(new MediaServerAccount(server.Id, server.Name, server.Enabled, body.Username.Trim()));
    }

    private static async Task<IResult> UnlinkAccountAsync(Guid? userId, Guid serverId, MelodexDbContext db, CancellationToken ct)
    {
        if (await db.Users.FindAsync([userId], ct) is not { } user)
            return Results.NotFound();
        await db.MediaServerLogins.Where(l => l.UserId == user.Id && l.PluginInstanceId == serverId).ExecuteDeleteAsync(ct);
        if (await db.PluginInstances.AnyAsync(p => p.Id == serverId && p.Enabled, ct))
        {
            user.PlaylistSyncError = null;
            user.PlaylistsSyncedAt = null;
        }
        await ForgetAsync(db, user.Id, serverId, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // Unlinks the login on the media server in use; logins on others are kept for when they're used again.
    private static async Task<IResult> UnlinkAsync(Guid? userId, MelodexDbContext db, CancellationToken ct)
    {
        if (await db.Users.FindAsync([userId], ct) is not { } user)
            return Results.NotFound();
        var active = await db.PluginInstances.Where(p => p.Type == PluginType.Connector && p.Enabled).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        await db.MediaServerLogins.Where(l => l.UserId == user.Id && (active == null || l.PluginInstanceId == active)).ExecuteDeleteAsync(ct);
        user.PlaylistSyncError = null;
        user.PlaylistsSyncedAt = null;
        await ForgetAsync(db, user.Id, active, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // Unlinks the user's playlists from the server's copies (any server's, without one), and drops those deleted here but
    // not yet there.
    private static async Task ForgetAsync(MelodexDbContext db, Guid userId, Guid? instanceId, CancellationToken ct)
    {
        var playlists = db.Playlists.Where(p => p.UserId == userId && (instanceId == null || p.ServerInstanceId == instanceId || p.ServerInstanceId == null));
        await playlists.Where(p => p.DeletedAt != null).ExecuteDeleteAsync(ct);
        await playlists.ExecuteUpdateAsync(p => p
            .SetProperty(x => x.ServerId, (string?)null).SetProperty(x => x.ServerInstanceId, (Guid?)null)
            .SetProperty(x => x.ServerChangedAt, (DateTime?)null).SetProperty(x => x.SyncedAt, (DateTime?)null).SetProperty(x => x.NeedsPush, false), ct);
    }

    // Playlists belong to people: an app's key acts for whoever made it.
    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("Playlists belong to a user, and this key doesn't: make a new one.", statusCode: StatusCodes.Status403Forbidden, title: "No user");

    private static async Task<IResult> Refusable(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PlaylistRefusedException e)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
        }
    }
}
