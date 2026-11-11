using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Requests;

namespace Melodex.Core.Api;

public static class RequestEndpoints
{
    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder api)
    {
        var requests = api.MapGroup("/requests").WithTags("Requests");

        requests.MapPost("", async (CreateRequest body, HttpContext http, RequestService service, CancellationToken ct) =>
            {
                try
                {
                    var (request, created) = await service.CreateAsync(body, Caller.Of(http).Requester, ct);
                    if (request is null)
                        return Results.NotFound();
                    var info = await service.GetAsync(request.Id, ct);
                    return created ? Results.Accepted($"/api/v1/requests/{request.Id}", info) : Results.Ok(info);
                }
                catch (ArgumentException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["editionId"] = [e.Message] });
                }
                catch (LibraryBusyException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "The library is being scanned");
                }
            })
            .RequireScope(Scopes.Request).WithName("CreateRequest")
            .WithSummary("Fetch an artist, album or track into the library. Asking for something already queued returns that request (200) instead of a new one (202). Refused (409) while a scan is reading the library.")
            .Produces<RequestInfo>(StatusCodes.Status202Accepted).Produces<RequestInfo>().Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        requests.MapGet("", (RequestService service, bool? active, int? limit, CancellationToken ct) =>
                service.ListAsync(active ?? false, Math.Clamp(limit ?? 100, 1, 500), ct))
            .WithName("ListRequests").WithSummary("Requests asked for directly, newest first; parts of artist and album requests are under each one.");

        requests.MapGet("/{id:guid}", async (Guid id, RequestService service, CancellationToken ct) =>
                await service.GetAsync(id, ct) is { } info ? Results.Ok(info) : Results.NotFound())
            .WithName("GetRequest").WithSummary("A request with its parts and its fetch attempts.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status404NotFound);

        requests.MapDelete("/{id:guid}", async (Guid id, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                await service.CancelAsync(request, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Request).WithName("CancelRequest").WithSummary("Cancel a request and its unfinished parts. Files already in the library stay.");

        requests.MapPost("/{id:guid}/pause", async (Guid id, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.PauseAsync(request, ct) ? Results.Ok(await service.GetAsync(id, ct)) : Results.Conflict();
            })
            .RequireScope(Scopes.Request).WithName("PauseRequest")
            .WithSummary("Hold a request and its unfinished parts back until resumed. Parts already fetching stop, and fetch again from the start when resumed. 409 if it's finished.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status409Conflict);

        requests.MapPost("/{id:guid}/resume", async (Guid id, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                await service.ResumeAsync(request, ct);
                return Results.Ok(await service.GetAsync(id, ct));
            })
            .RequireScope(Scopes.Request).WithName("ResumeRequest").WithSummary("Let a paused request go again, in its place in the queue.")
            .Produces<RequestInfo>();

        requests.MapPost("/{id:guid}/move", async (Guid id, MoveInQueue body, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.MoveAsync(request, body.Before, ct) ? Results.NoContent() : Results.Conflict();
            })
            .RequireScope(Scopes.Request).WithName("MoveRequest")
            .WithSummary("Move an unfinished request, with its parts, to just before another in the queue, or to the end. Library and listen-now requests "
                         + "queue separately. 409 for a finished request, a part, or a before that isn't in the same queue.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status409Conflict);

        requests.MapPost("/{id:guid}/retry", async (Guid id, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.RetryAsync(request, ct) ? Results.Ok(await service.GetAsync(id, ct)) : Results.Conflict();
            })
            .RequireScope(Scopes.Request).WithName("RetryRequest").WithSummary("Queue a failed or reviewed request again, with the parts of it that aren't in the library.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status409Conflict);

        requests.MapPost("/{id:guid}/choose", async (Guid id, ChooseCandidate body, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.ChooseAsync(request, body.JobId, body.CandidateId, ct) ? Results.Ok(await service.GetAsync(id, ct)) : Results.Conflict();
            })
            .RequireScope(Scopes.Request).WithName("ChooseCandidate").WithSummary("Fetch one of the candidates an earlier search found, instead of searching again. For finished track or whole-album requests.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status409Conflict);

        requests.MapPost("/{id:guid}/keep", async (Guid id, KeepCopy body, MelodexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.KeepAsync(request, body.JobId, ct) ? Results.Ok(await service.GetAsync(id, ct)) : Results.Conflict();
            })
            .RequireScope(Scopes.Request).WithName("KeepCopy")
            .WithSummary("Keep the copy an attempt fetched but didn't keep for its quality (turned off, or lossless that seemed lossy), as it is. "
                         + "For finished track requests whose copy is still in staging.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status409Conflict);

        var settings = api.MapGroup("/settings").WithTags("Settings").RequireScope(Scopes.Settings);
        settings.MapGet("/fetch", (SettingsService s, CancellationToken ct) => s.GetFetchAsync(ct)).WithName("GetFetchSettings");
        settings.MapPut("/fetch", async (FetchSettings value, SettingsService s, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (string.IsNullOrWhiteSpace(value.PathTemplate))
                    errors["pathTemplate"] = ["Can't be empty."];
                if (value.ParallelFetches is < 1 or > 8)
                    errors["parallelFetches"] = ["Must be between 1 and 8."];
                if (value.DurationToleranceSeconds is < 0 or > 120)
                    errors["durationToleranceSeconds"] = ["Must be between 0 and 120."];
                if (value.DurationTolerancePercent is < 0 or > 50)
                    errors["durationTolerancePercent"] = ["Must be between 0 and 50."];
                if (value.CandidatesPerPlugin is < 1 or > 5)
                    errors["candidatesPerPlugin"] = ["Must be between 1 and 5."];
                if (value.ListenNowExpiryMinutes is < 5 or > 7 * 24 * 60)
                    errors["listenNowExpiryMinutes"] = ["Must be between 5 minutes and a week."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                await s.SaveFetchAsync(value, ct);
                return Results.NoContent();
            })
            .WithName("SaveFetchSettings").WithSummary("Applies to requests from their next step.");

        settings.MapGet("/quality", (SettingsService s, CancellationToken ct) => s.GetQualityAsync(ct)).WithName("GetQualityProfile");
        settings.MapPut("/quality", async (QualityProfile value, SettingsService s, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                var ordered = value.Ordered();
                if (QualityStep.Parse(value.Cutoff) is not { } cutoff)
                    errors["cutoff"] = ["Must be one of the steps."];
                else if (!ordered.First(s => s.Step == cutoff).Allowed)
                    errors["cutoff"] = ["Must be a step that's turned on."];
                if (QualityStep.Parse(value.ListenTarget) is null)
                    errors["listenTarget"] = ["Must be one of the steps."];
                if (!ordered.Any(s => s.Allowed))
                    errors["steps"] = ["At least one quality must be turned on."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                await s.SaveQualityAsync(value, ct);
                return Results.NoContent();
            })
            .WithName("SaveQualityProfile").WithSummary("The qualities wanted, in order, and the cutoff that's good enough. Applies to searches from their next step.");

        return api;
    }
}
