using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Listening;

namespace Melodex.Core.Api;

/// <summary>Radio (#4): play that never runs out, started from a song, album, artist or playlist, and each person's radio settings.</summary>
public static class RadioEndpoints
{
    public static RouteGroupBuilder MapRadioEndpoints(this RouteGroupBuilder api)
    {
        var radio = api.MapGroup("/radio").WithTags("Radio");

        radio.MapPost("", async (RadioNext body, HttpContext http, RadioService service, CancellationToken ct) =>
                Owner(http) is not { } user ? NoOwner()
                : await service.NextAsync(user, body, ct) is { } batch ? Results.Ok(batch)
                : Results.NotFound())
            .RequireScope(Scopes.Listen)
            .WithName("NextRadioSongs")
            .WithSummary($"The next songs for a radio started from a song, album, artist or one of your playlists (at most {RadioService.MaxTake}). "
                         + "Nothing is kept: send what it was started from and what it's queued so far each time. Songs you don't have, to fetch to "
                         + "listen to, make up your share of it, unless only those you have are asked for.")
            .Produces<RadioBatch>().Produces(StatusCodes.Status404NotFound);

        radio.MapPost("/skips", async (RadioSkipped body, HttpContext http, RadioService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                await service.SkipAsync(user, body.TrackId, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Listen)
            .WithName("SkipRadioSong")
            .WithSummary("A song a radio played was skipped within its first half minute: radios leave it out, and play its artist less, for 90 days.")
            .Produces(StatusCodes.Status204NoContent);

        radio.MapGet("/settings", async (HttpContext http, RadioService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.SettingsAsync(user, ct)) : NoOwner())
            .RequireScope(Scopes.Listen)
            .WithName("GetRadioSettings")
            .WithSummary("Your radio settings: whether the player carries on with a radio when the queue runs out. How much of a radio is songs you don't have is the server's, in GET /settings/fetch.")
            .Produces<RadioSettings>();

        radio.MapPut("/settings", async (RadioSettings body, HttpContext http, RadioService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                await service.SaveSettingsAsync(user, body, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Listen)
            .WithName("SaveRadioSettings")
            .WithSummary("Changes your radio settings.")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("Radio is for a user, and this key doesn't belong to one: make a new one.",
            statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
