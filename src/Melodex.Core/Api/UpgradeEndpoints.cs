using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Import;
using Melodex.Core.Library;

namespace Melodex.Core.Api;

public static class UpgradeEndpoints
{
    public static RouteGroupBuilder MapUpgradeEndpoints(this RouteGroupBuilder api)
    {
        var upgrades = api.MapGroup("/upgrades").WithTags("Upgrades");

        upgrades.MapGet("", (Upgrades service, CancellationToken ct) => service.StatusAsync(ct))
            .WithName("GetUpgrades")
            .WithSummary("Upgrades' settings, how many songs are below their cutoff, files replaced by better copies that can be put back, and "
                         + "why the media server may lose stars, plays and playlists across an upgrade, if it may.");

        upgrades.MapPut("/settings", async (UpgradeSettings value, SettingsService s, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (value.PerRun is < 1 or > 500)
                    errors["perRun"] = ["Must be between 1 and 500, or none for no limit."];
                if (value.Scheduled && value.Days.Count == 0)
                    errors["days"] = ["Pick at least one day."];
                if (!UpgradeWindows.Known(value.TimeZone))
                    errors["timeZone"] = ["Must be a time zone this server knows, such as Europe/London."];
                if (value.HoldDays is < 1 or > 365)
                    errors["holdDays"] = ["Must be between 1 and 365."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                await s.SaveUpgradesAsync(value with { Days = value.Days.Distinct().Order().ToList() }, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Settings).WithName("SaveUpgradeSettings")
            .WithSummary("Whether the whole library is looked through on a schedule, on which days and between which times, for how many, and "
                         + "whether replaced files are held or deleted.");

        upgrades.MapPost("/search", async (FormatScope scope, HttpContext http, Upgrades service, SettingsService s, CancellationToken ct) =>
            {
                if (scope.Kind != FormatScopeKind.Library && scope.Id is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["An artist, album or song needs its ID."] });
                // The whole library at once is as many as a scheduled look asks for (500 at most), longest unlooked-for first.
                int? limit = scope.Kind == FormatScopeKind.Library ? Math.Clamp((await s.GetUpgradesAsync(ct)).PerRun ?? Upgrades.MostByHand, 1, Upgrades.MostByHand) : null;
                return Results.Ok(await service.SearchAsync(scope, limit, Caller.Of(http).Requester, ct));
            })
            .RequireScope(Scopes.Request).WithName("SearchUpgrades")
            .WithSummary("Ask for upgrades of a song's, an album's or an artist's files below their cutoff, or the whole library's, as many as a "
                         + "scheduled look asks for (500 at most). Runs now, whatever the schedule's window. Each is a request on the Activity page.")
            .Produces<UpgradeSearch>();

        upgrades.MapGet("/held", (Guid? trackId, Upgrades service, CancellationToken ct) => service.HeldAsync(trackId, ct))
            .WithName("GetHeldFiles").WithSummary("Files replaced by better copies that can be put back, of one song or all, newest first.");

        upgrades.MapPost("/held/{id:guid}/restore", async (Guid id, bool? never, Upgrades service, CancellationToken ct) =>
            {
                try
                {
                    return await service.RestoreAsync(id, never ?? false, ct) ? Results.NoContent() : Results.NotFound();
                }
                catch (Exception e) when (e is ImportException or HttpRequestException)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Couldn't put it back");
                }
            })
            .RequireScope(Scopes.Request).WithName("RestoreHeldFile")
            .WithSummary("Put a replaced file back in place of the copy that replaced it, which is deleted; with never, set its song never to be upgraded.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        upgrades.MapDelete("/held/{id:guid}", async (Guid id, Upgrades service, CancellationToken ct) =>
                await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireScope(Scopes.Request).WithName("DeleteHeldFile").WithSummary("Delete a replaced file now, instead of when its time is up.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        return api;
    }
}
