using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Api;

/// <summary>The steps after making the admin account: the music folder, the media server, fetching and the first scan.</summary>
public static class SetupEndpoints
{
    private const string DoneKey = "setup.done";

    public static RouteGroupBuilder MapSetupEndpoints(this RouteGroupBuilder api)
    {
        var setup = api.MapGroup("/setup").WithTags("System").RequireScope(Scopes.Settings);

        setup.MapGet("", async (MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct) =>
            {
                var path = options.Value.LibraryPath;
                var (readable, files) = Count(path);
                var scanned = await db.BackgroundTasks.AnyAsync(t => t.Kind == LibraryScanTask.TaskKind && t.Status == BackgroundTaskStatus.Succeeded, ct);
                return new SetupStatus(await IsDoneAsync(db, ct), Path.GetFullPath(path), readable, files,
                    await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Connector, ct),
                    await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Retrieval, ct),
                    scanned);
            })
            .WithName("GetSetup")
            .WithSummary("How far setup has got: whether the music folder can be read and has music in it, and what's been added.");

        setup.MapPost("/done", async (MelodexDbContext db, CancellationToken ct) =>
            {
                await MarkDoneAsync(db, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Settings)
            .WithName("FinishSetup")
            .WithSummary("Setup is finished, or skipped: the web app stops showing its steps.");

        setup.MapPost("/reset", (FactoryResetRequest body, HttpContext http, IOptions<MelodexOptions> options, IHostApplicationLifetime lifetime) =>
            {
                if (Caller.Of(http) is not { Kind: CallerKind.User, IsAdmin: true })
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the admin, signed in, can reset Melodex.");
                if (!string.Equals(body.Confirm?.Trim(), FactoryReset.Confirmation, StringComparison.OrdinalIgnoreCase))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = [$"Type \"{FactoryReset.Confirmation}\" to reset Melodex."] });
                FactoryReset.Request(options.Value);
                // Once the answer is sent, Melodex stops, and wipes everything as it starts again.
                http.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Results.Accepted();
            })
            .WithName("FactoryReset")
            .WithSummary("Resets Melodex as it was first installed, for the admin only, who must send confirm: \"reset\". Users, settings, plugins, "
                         + "the library's matches, playlists, requests, the cache, pictures, logs and Listen Now files all go; the music folder and "
                         + "plugins' own data stay. Melodex restarts, so everyone is signed out, and the web app starts setup again.")
            .Produces(StatusCodes.Status202Accepted).ProducesValidationProblem();

        return api;
    }

    /// <summary>
    /// Whether setup is finished. Until it is, the web app shows only its steps. An install that had music before there
    /// were setup steps doesn't need them.
    /// </summary>
    public static async Task<bool> IsDoneAsync(MelodexDbContext db, CancellationToken ct)
    {
        if (await db.Settings.AnyAsync(s => s.Key == DoneKey, ct))
            return true;
        if (!await db.LibraryFiles.AnyAsync(ct))
            return false;
        await MarkDoneAsync(db, ct);
        return true;
    }

    private static async Task MarkDoneAsync(MelodexDbContext db, CancellationToken ct)
    {
        if (await db.Settings.FindAsync([DoneKey], ct) is null)
        {
            db.Settings.Add(new Setting { Key = DoneKey, ValueJson = "true" });
            await db.SaveChangesAsync(ct);
        }
    }

    // A quick look, not a scan: enough to say the folder is right, without walking a whole big library.
    private static (bool Readable, int Files) Count(string path)
    {
        try
        {
            var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => LibraryScanner.AudioExtensions.Contains(Path.GetExtension(f)))
                .Take(SetupStatus.CountLimit)
                .Count();
            return (true, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, 0);
        }
    }
}
