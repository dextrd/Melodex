using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Access;
using Melodex.Core.Data;

namespace Melodex.Core.Api;

public static class AccessEndpoints
{
    private const int MinPasswordLength = 8;

    public static RouteGroupBuilder MapAccessEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Access");

        auth.MapGet("/status", async (HttpContext http, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                var (caller, _) = await access.IdentifyAsync(http, ct);
                var user = caller is { Kind: CallerKind.User, UserId: { } id } ? (await access.UsersAsync(ct)).GetValueOrDefault(id) : null;
                var own = AppearanceOf(user);
                var setUp = await access.IsSetUpAsync(ct);
                return new AuthStatus(setUp, caller?.Kind == CallerKind.User, caller is not null, caller?.Name, caller?.UserId,
                    caller is { Kind: CallerKind.User, IsAdmin: true }, caller is null ? [] : Scopes.All.Where(caller.Scopes.Contains).ToList(),
                    own ?? await DefaultAppearanceAsync(db, ct), own is not null, setUp && await SetupEndpoints.IsDoneAsync(db, ct),
                    await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Index && p.Enabled, ct));
            })
            .AllowAnyone()
            .WithName("GetAuthStatus")
            .WithSummary("Whether the admin account has been made and setup finished, and who this caller is and what they may do. Open to anyone.");

        auth.MapPost("/setup", async (SetUpLogin body, HttpContext http, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await access.IsSetUpAsync(ct))
                    return Results.Problem("There's already an admin account. Sign in instead.", statusCode: StatusCodes.Status409Conflict, title: "Already set up");
                if (Invalid(body.Username, body.Password) is { } invalid)
                    return invalid;
                // After a login reset the admin is kept, with their keys, and only gets a new name and password.
                var admin = await db.Users.FirstOrDefaultAsync(u => u.IsAdmin, ct);
                if (await TakenAsync(db, body.Username, admin?.Id, ct))
                    return Taken();
                if (admin is null)
                {
                    admin = AccessService.NewUser(body.Username, body.Password, [], isAdmin: true);
                    db.Users.Add(admin);
                }
                else
                {
                    admin.Username = body.Username.Trim();
                    admin.NormalizedUsername = AccessService.Normalize(body.Username);
                    admin.PasswordHash = AccessService.HashPassword(body.Password);
                    admin.Stamp = AccessService.NewStamp();
                }
                admin.LastSignInAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(admin));
                return Results.NoContent();
            })
            .AllowAnyone()
            .WithName("SetUpLogin")
            .WithSummary("Make the admin account, the first time Melodex runs, and sign in with it. The admin may do everything, including "
                         + "adding other users. Refused (409) once there is one.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/login", async (SignIn body, HttpContext http, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (access.TooManyFailures(address))
                    return Results.Problem("Too many wrong passwords. Wait a few minutes, then try again.", statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many tries");
                var user = await access.FindAsync(body.Username, ct);
                if (user is not { PasswordHash.Length: > 0 } || !AccessService.CheckPassword(body.Password, user.PasswordHash))
                {
                    access.Failed(address);
                    // Slows guessing, and doesn't say which of the two was wrong.
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                    return Results.Problem("That username and password don't match.", statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in");
                }
                access.Succeeded(address);
                await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSignInAt, DateTime.UtcNow), ct);
                access.UsersChanged();
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(user),
                    new AuthenticationProperties { IsPersistent = body.RememberMe });
                return Results.NoContent();
            })
            .AllowAnyone()
            .WithName("SignIn")
            .WithSummary("Sign in. The session is a cookie, for browsers: remembered for 30 days from the last visit, or until the browser closes. "
                         + "Apps use an API key instead.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            })
            .AllowAnyone()
            .WithName("SignOut")
            .WithSummary("End this browser's session.");

        auth.MapPut("/account", async (UpdateLogin body, HttpContext http, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                var caller = Caller.Of(http);
                if (caller.Kind != CallerKind.User || await db.Users.FindAsync([caller.UserId], ct) is not { } user)
                    return Results.Problem("Only someone signed in can change their account.", statusCode: StatusCodes.Status403Forbidden, title: "Not allowed");
                if (!AccessService.CheckPassword(body.CurrentPassword, user.PasswordHash))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["That isn't the current password."] });
                var username = string.IsNullOrWhiteSpace(body.Username) ? user.Username : body.Username.Trim();
                if (Invalid(username, body.NewPassword ?? new string('x', MinPasswordLength)) is { } invalid)
                    return invalid;
                if (await TakenAsync(db, username, user.Id, ct))
                    return Taken();
                user.Username = username;
                user.NormalizedUsername = AccessService.Normalize(username);
                // A new password signs out every other session; this one carries on under the new stamp.
                if (body.NewPassword is { Length: > 0 } password)
                {
                    user.PasswordHash = AccessService.HashPassword(password);
                    user.Stamp = AccessService.NewStamp();
                }
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(user),
                    new AuthenticationProperties { IsPersistent = await RememberedAsync(http) });
                return Results.NoContent();
            })
            .RequireScope(Scopes.SignedIn)
            .WithName("UpdateLogin")
            .WithSummary("Change your own username or password. A new password signs out your other sessions.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        auth.MapPut("/appearance", async (Appearance body, HttpContext http, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.Users.FindAsync([Caller.Of(http).UserId], ct) is not { } user)
                    return Results.NotFound();
                if (Invalid(body) is { } problem)
                    return problem;
                user.Appearance = JsonSerializer.Serialize(body, MelodexJson.Options);
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                return Results.Ok(body);
            })
            .RequireScope(Scopes.SignedIn)
            .WithName("SetAppearance")
            .WithSummary("Choose how the web app looks to you (its colour, light or dark, and high contrast) and how long the Search page keeps your last search. Kept with your account, so it follows you.");

        auth.MapDelete("/appearance", async (HttpContext http, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.Users.FindAsync([Caller.Of(http).UserId], ct) is not { } user)
                    return Results.NotFound();
                user.Appearance = null;
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                return Results.Ok(await DefaultAppearanceAsync(db, ct));
            })
            .RequireScope(Scopes.SignedIn)
            .WithName("ClearAppearance")
            .WithSummary("Go back to how Melodex looks for everyone, following it when the admin changes it. Returns that look.");

        auth.MapGet("/appearance/default", (MelodexDbContext db, CancellationToken ct) => DefaultAppearanceAsync(db, ct))
            .RequireScope(Scopes.SignedIn)
            .WithName("GetDefaultAppearance")
            .WithSummary("How Melodex looks for everyone who hasn't chosen their own look.");

        auth.MapPut("/appearance/default", async (Appearance body, AccessService access, MelodexDbContext db, CancellationToken ct) =>
            {
                if (Invalid(body) is { } problem)
                    return problem;
                var json = JsonSerializer.Serialize(body, MelodexJson.Options);
                if (await db.Settings.FindAsync([DefaultAppearanceKey], ct) is { } setting)
                    setting.ValueJson = json;
                else
                    db.Settings.Add(new Setting { Key = DefaultAppearanceKey, ValueJson = json });
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                return Results.Ok(body);
            })
            .RequireScope(Scopes.Settings)
            .WithName("SetDefaultAppearance")
            .WithSummary("Choose how Melodex looks for everyone who hasn't chosen their own look. Each person can choose their own under Settings → Appearance.");

        MapUsers(api);
        MapKeys(api);
        return api;
    }

    private static void MapUsers(RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Access").RequireScope(Scopes.Users);

        users.MapGet("", async (MelodexDbContext db, CancellationToken ct) =>
            {
                var keys = await db.ApiKeys.Where(k => k.UserId != null).GroupBy(k => k.UserId!.Value).Select(g => new { g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
                return (await db.Users.AsNoTracking().ToListAsync(ct))
                    .OrderByDescending(u => u.IsAdmin).ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                    .Select(u => Info(u, keys.GetValueOrDefault(u.Id))).ToList();
            })
            .WithName("ListUsers").WithSummary("Everyone who signs in, the admin first, with what they may do.");

        users.MapPost("", async (CreateUser body, HttpContext http, MelodexDbContext db, AccessService access, CancellationToken ct) =>
            {
                if (Invalid(body.Username, body.Password) is { } invalid)
                    return invalid;
                if (Beyond(Caller.Of(http), body.Permissions) is { } beyond)
                    return beyond;
                if (await TakenAsync(db, body.Username, null, ct))
                    return Taken();
                var user = AccessService.NewUser(body.Username, body.Password, body.Permissions);
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                return Results.Created($"/api/v1/users/{user.Id}", Info(user, 0));
            })
            .WithName("CreateUser")
            .WithSummary("Add someone who can sign in, with what they may do. You can only give permissions you have yourself.")
            .Produces<UserInfo>(StatusCodes.Status201Created).ProducesValidationProblem();

        users.MapPut("/{id:guid}", async (Guid id, UpdateUser body, HttpContext http, MelodexDbContext db, AccessService access, CancellationToken ct) =>
            {
                var caller = Caller.Of(http);
                if (await db.Users.FindAsync([id], ct) is not { } user)
                    return Results.NotFound();
                if (user.IsAdmin && !caller.IsAdmin || user.IsAdmin && caller.Kind != CallerKind.User)
                    return Results.Problem("Only the admin can change the admin's account.", statusCode: StatusCodes.Status403Forbidden, title: "Not allowed");
                if (body.Permissions is { } permissions && !user.IsAdmin && Beyond(caller, permissions) is { } beyond)
                    return beyond;
                var username = string.IsNullOrWhiteSpace(body.Username) ? user.Username : body.Username.Trim();
                if (Invalid(username, string.IsNullOrEmpty(body.Password) ? new string('x', MinPasswordLength) : body.Password) is { } invalid)
                    return invalid;
                if (await TakenAsync(db, username, user.Id, ct))
                    return Taken();
                user.Username = username;
                user.NormalizedUsername = AccessService.Normalize(username);
                if (body.Permissions is not null && !user.IsAdmin)
                    user.Permissions = Scopes.Join(body.Permissions.Where(Scopes.IsPermission));
                if (body.Password is { Length: > 0 } password)
                {
                    user.PasswordHash = AccessService.HashPassword(password);
                    user.Stamp = AccessService.NewStamp();
                }
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                // Changing your own password here keeps you signed in, as changing it under your account does.
                if (user.Id == caller.UserId && caller.Kind == CallerKind.User)
                    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessService.Principal(user),
                        new AuthenticationProperties { IsPersistent = await RememberedAsync(http) });
                return Results.Ok(Info(user, await db.ApiKeys.CountAsync(k => k.UserId == user.Id, ct)));
            })
            .WithName("UpdateUser")
            .WithSummary("Rename someone, set a new password for them (which signs them out everywhere), or change what they may do. Only the admin "
                         + "can change the admin, whose permissions are fixed, and you can only give permissions you have yourself.")
            .Produces<UserInfo>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        users.MapDelete("/{id:guid}", async (Guid id, HttpContext http, MelodexDbContext db, AccessService access, CancellationToken ct) =>
            {
                if (await db.Users.FindAsync([id], ct) is not { } user)
                    return Results.NotFound();
                if (user.IsAdmin)
                    return Results.Problem("The admin can't be removed.", statusCode: StatusCodes.Status409Conflict, title: "Can't remove the admin");
                if (user.Id == Caller.Of(http).UserId)
                    return Results.Problem("You can't remove yourself. Ask the admin.", statusCode: StatusCodes.Status409Conflict, title: "Can't remove yourself");
                db.Users.Remove(user);
                await db.SaveChangesAsync(ct);
                access.UsersChanged();
                return Results.NoContent();
            })
            .WithName("RemoveUser").WithSummary("Remove someone, signing them out and revoking their API keys. The admin can't be removed.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapKeys(RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/keys").WithTags("Access").RequireScope(Scopes.Keys);

        keys.MapGet("", async (HttpContext http, MelodexDbContext db, CancellationToken ct) =>
            {
                var caller = Caller.Of(http);
                var query = db.ApiKeys.AsNoTracking().Include(k => k.User).AsQueryable();
                if (!caller.IsAdmin)
                    query = query.Where(k => k.UserId == caller.UserId);
                return (await query.OrderBy(k => k.CreatedAt).ToListAsync(ct)).Select(Info).ToList();
            })
            .WithName("ListApiKeys").WithSummary("Your API keys, without the keys themselves. The admin sees everyone's.");

        keys.MapPost("", async (CreateApiKey body, HttpContext http, MelodexDbContext db, AccessService access, CancellationToken ct) =>
            {
                var caller = Caller.Of(http);
                var errors = new Dictionary<string, string[]>();
                if (string.IsNullOrWhiteSpace(body.Name))
                    errors["name"] = ["Give the key a name, such as the app it's for."];
                if (body.Scopes.Count == 0 || body.Scopes.Any(s => !Scopes.IsKnown(s)))
                    errors["scopes"] = [$"Choose one or more of {string.Join(", ", Scopes.All)}."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                if (caller.UserId is not { } owner)
                    return Results.Problem("Keys belong to a user: sign in to make one.", statusCode: StatusCodes.Status403Forbidden, title: "Not allowed");
                // Admin on a key means all its owner may, so anyone can ask for it.
                if (Beyond(caller, body.Scopes.Where(s => s != Scopes.Admin)) is { } beyond)
                    return beyond;
                var (key, record) = AccessService.NewKey(body.Name, body.Scopes, owner);
                db.ApiKeys.Add(record);
                await db.SaveChangesAsync(ct);
                access.KeysChanged();
                record.User = (await access.UsersAsync(ct)).GetValueOrDefault(owner);
                return Results.Created($"/api/v1/keys/{record.Id}", new CreatedApiKey(Info(record), key));
            })
            .WithName("CreateApiKey")
            .WithSummary("Make a key for an app. It's yours: it can do only what you may, and goes if you're removed. The key is in this answer "
                         + "only: Melodex keeps just a hash of it.")
            .Produces<CreatedApiKey>(StatusCodes.Status201Created).ProducesValidationProblem();

        keys.MapDelete("/{id:guid}", async (Guid id, HttpContext http, MelodexDbContext db, AccessService access, CancellationToken ct) =>
            {
                var caller = Caller.Of(http);
                var removed = await db.ApiKeys.Where(k => k.Id == id && (caller.IsAdmin || k.UserId == caller.UserId)).ExecuteDeleteAsync(ct);
                access.KeysChanged();
                return removed == 0 ? Results.NotFound() : Results.NoContent();
            })
            .WithName("RevokeApiKey").WithSummary("Revoke one of your keys (the admin can revoke anyone's). Apps using it are refused from then on.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
    }

    private static ApiKeyInfo Info(ApiKey key) =>
        new(key.Id, key.Name, key.Prefix, Scopes.Parse(key.Scopes).ToList(), key.CreatedAt, key.LastUsedAt, key.UserId, key.User?.Username);

    private static UserInfo Info(User user, int keys) =>
        new(user.Id, user.Username, user.IsAdmin, Scopes.All.Where(AccessService.PermissionsOf(user).Contains).ToList(), user.CreatedAt, user.LastSignInAt, keys,
            user.MediaServerUsername);

    private const string DefaultAppearanceKey = "appearance.default";

    // A person's own look, or null where they follow everyone's.
    private static Appearance? AppearanceOf(User? user) => user?.Appearance is { Length: > 0 } json ? Parse(json) : null;

    private static async Task<Appearance> DefaultAppearanceAsync(MelodexDbContext db, CancellationToken ct) =>
        await db.Settings.FindAsync([DefaultAppearanceKey], ct) is { } setting ? Parse(setting.ValueJson) ?? new Appearance() : new Appearance();

    private static Appearance? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Appearance>(json, MelodexJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult? Invalid(Appearance body)
    {
        if (!Enum.IsDefined(body.Color) || !Enum.IsDefined(body.Mode))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["appearance"] = ["That isn't a theme Melodex has."] });
        if (body.PlaylistFolders is null || body.PlaylistFolders.Length > 3 || body.PlaylistFolders.Any(char.IsWhiteSpace))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["playlistFolders"] = ["Separate folders with up to 3 characters, not spaces, or none."] });
        if (body.ListPage is < 20 or > 500)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["listPage"] = ["Show from 20 to 500 at a time."] });
        return null;
    }

    // Nobody can give more than they have.

    private static IResult? Beyond(Caller caller, IEnumerable<string> permissions)
    {
        var unknown = permissions.Where(p => !Scopes.IsPermission(p)).ToList();
        if (unknown.Count > 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = [$"{string.Join(", ", unknown)} isn't a permission. Choose from {string.Join(", ", Scopes.Permissions)}."] });
        var missing = permissions.Where(p => !caller.May(p)).ToList();
        return missing.Count == 0
            ? null
            : Results.Problem($"You can only give permissions you have yourself, and you don't have {string.Join(", ", missing)}.",
                statusCode: StatusCodes.Status403Forbidden, title: "Not allowed");
    }

    private static Task<bool> TakenAsync(MelodexDbContext db, string username, Guid? except, CancellationToken ct)
    {
        var normalized = AccessService.Normalize(username);
        return db.Users.AnyAsync(u => u.NormalizedUsername == normalized && u.Id != except, ct);
    }

    private static IResult Taken() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Someone already has that username."] });

    // Whether this browser's session is remembered, so signing it in again under a new stamp keeps it so.
    private static async Task<bool> RememberedAsync(HttpContext http) =>
        (await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)).Properties?.IsPersistent == true;

    private static IResult? Invalid(string username, string password)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(username))
            errors["username"] = ["Choose a username."];
        if (password.Length < MinPasswordLength)
            errors["password"] = [$"Use at least {MinPasswordLength} characters."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
