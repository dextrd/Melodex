using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Events;
using Melodex.Core.Library;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Signing in, users and their permissions, API keys and their scopes, and webhooks.</summary>
public sealed class AccessTests : IDisposable
{
    /// <summary>Stands in for the caller's address, which the test server doesn't have.</summary>
    private const string RemoteHeader = "X-Test-Remote";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Receiver _receiver = new();

    public AccessTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RemoteAddress());
                services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver);
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    [Fact]
    public async Task Before_setup_only_open_endpoints_answer()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/version")).StatusCode);
        var status = await http.GetFromJsonAsync<AuthStatus>("api/v1/auth/status", MelodexJson.Options);
        Assert.False(status!.SetUp);
        Assert.False(status.LetIn);

        var refused = await http.GetAsync("api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("setup-required", (await Problem(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Setup_happens_once_and_then_the_admin_signs_in()
    {
        var admin = _factory.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("api/v1/library")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("api/v1/auth/setup", new SetUpLogin("someone", "another password"))).StatusCode);

        var other = _factory.CreateClient();
        var refused = await other.GetAsync("api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("sign-in-required", (await Problem(refused)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await other.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, "wrong password"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await other.PostAsJsonAsync("api/v1/auth/login", new SignIn("ADMIN", SignedIn.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("api/v1/library")).StatusCode);

        await other.PostAsync("api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("api/v1/library")).StatusCode);
    }

    [Fact]
    public async Task Too_many_wrong_passwords_have_to_wait()
    {
        _factory.CreateSignedInClient();
        var http = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await http.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, "wrong password"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await http.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, SignedIn.Password))).StatusCode);
    }

    [Fact]
    public async Task A_new_password_signs_out_other_sessions_but_not_this_one()
    {
        var first = new MelodexClient(_factory.CreateSignedInClient());
        var secondHttp = _factory.CreateSignedInClient();

        await first.UpdateLoginAsync(new UpdateLogin(SignedIn.Password, NewPassword: "a brand new password"));

        Assert.NotNull(await first.GetLibraryStatusAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondHttp.GetAsync("api/v1/library")).StatusCode);
        var wrong = await Assert.ThrowsAsync<MelodexApiException>(() => first.UpdateLoginAsync(new UpdateLogin("not it", "renamed")));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
    }

    [Fact]
    public async Task Keys_do_what_their_scopes_allow()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var reader = await admin.CreateApiKeyAsync(new CreateApiKey("Reader app", [Scopes.Read]));
        var requester = await admin.CreateApiKeyAsync(new CreateApiKey("Requester app", [Scopes.Read, Scopes.Request]));
        Assert.StartsWith("mdx_", reader.Key);
        Assert.StartsWith(reader.Info.Prefix, reader.Key);

        var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Add(AccessService.KeyHeader, reader.Key);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("api/v1/library")).StatusCode);

        var refused = await app.PostAsJsonAsync("api/v1/requests", new CreateRequest(RequestKind.Track, Guid.NewGuid()), MelodexJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await Problem(refused);
        Assert.Equal("scope-required", problem.GetProperty("code").GetString());
        Assert.Equal("request", problem.GetProperty("scope").GetString());

        // Settings, plugins and keys are for the admin, even to read.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("api/v1/settings/fetch")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("api/v1/plugins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("api/v1/keys")).StatusCode);

        // Past the scope check: there's no such track.
        using var other = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsJsonAsync($"api/v1/requests?apikey={requester.Key}", new CreateRequest(RequestKind.Track, Guid.NewGuid()), MelodexJson.Options))
            .StatusCode);

        await admin.RevokeApiKeyAsync(reader.Info.Id);
        var revoked = await app.GetAsync("api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("invalid-key", (await Problem(revoked)).GetProperty("code").GetString());
        Assert.Equal(["Requester app"], (await admin.ListApiKeysAsync()).Select(k => k.Name));
    }

    [Fact]
    public async Task Bad_key_requests_are_refused()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var error = await Assert.ThrowsAsync<MelodexApiException>(() => admin.CreateApiKeyAsync(new CreateApiKey("App", ["everything"])));
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        error = await Assert.ThrowsAsync<MelodexApiException>(() => admin.CreateApiKeyAsync(new CreateApiKey(" ", [Scopes.Read])));
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    [Fact]
    public async Task Everyone_signs_in_even_on_the_local_network()
    {
        _factory.CreateSignedInClient();
        foreach (var address in new[] { "192.168.1.20", "10.0.0.4", "::1" })
        {
            using var http = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/library");
            request.Headers.Add(RemoteHeader, address);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Remember_me_keeps_the_session_after_the_browser_closes()
    {
        _factory.CreateSignedInClient();
        async Task<string> CookieAsync(bool remember)
        {
            using var http = _factory.CreateClient();
            var response = await http.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, SignedIn.Password, remember));
            response.EnsureSuccessStatusCode();
            return Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AccessMiddleware.SessionCookie));
        }

        Assert.Contains("expires=", await CookieAsync(remember: true));
        Assert.DoesNotContain("expires=", await CookieAsync(remember: false));
    }

    [Fact]
    public async Task A_user_may_do_only_what_they_were_given()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var listener = await admin.CreateUserAsync(new CreateUser("Listener", "listening password", [Scopes.Read, Scopes.Listen]));
        Assert.False(listener.IsAdmin);

        var http = await SignInAsync("listener", "listening password");
        var status = await http.GetFromJsonAsync<AuthStatus>("api/v1/auth/status", MelodexJson.Options);
        Assert.Equal("Listener", status!.Username);
        Assert.Equal(listener.Id, status.UserId);
        Assert.True(status.May(UserPermissions.Listen));
        Assert.False(status.May(UserPermissions.Request));

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/library")).StatusCode);
        var refused = await http.PostAsJsonAsync("api/v1/requests", new CreateRequest(RequestKind.Track, Guid.NewGuid()), MelodexJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("request", (await Problem(refused)).GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/v1/settings/fetch")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("api/v1/library/scan", null)).StatusCode);

        // Given more, they may do more at once, without signing in again.
        await admin.UpdateUserAsync(listener.Id, new UpdateUser(Permissions: [Scopes.Read, Scopes.Listen, Scopes.Settings]));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/settings/fetch")).StatusCode);

        // Removed, they're signed out.
        await admin.RemoveUserAsync(listener.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/v1/library")).StatusCode);
    }

    [Fact]
    public async Task Nobody_gives_what_they_dont_have_and_only_the_admin_changes_the_admin()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var adminId = (await admin.ListUsersAsync()).Single(u => u.IsAdmin).Id;
        var managerInfo = await admin.CreateUserAsync(new CreateUser("manager", "managing password", [Scopes.Read, Scopes.Users]));
        var manager = new MelodexClient(await SignInAsync("manager", "managing password"));

        var guest = await manager.CreateUserAsync(new CreateUser("guest", "a guest password", [Scopes.Read]));
        var beyond = await Assert.ThrowsAsync<MelodexApiException>(() => manager.CreateUserAsync(new CreateUser("dj", "a dj password", [Scopes.Read, Scopes.Request])));
        Assert.Equal(HttpStatusCode.Forbidden, beyond.Status);
        beyond = await Assert.ThrowsAsync<MelodexApiException>(() => manager.UpdateUserAsync(guest.Id, new UpdateUser(Permissions: [Scopes.Settings])));
        Assert.Equal(HttpStatusCode.Forbidden, beyond.Status);

        var notAdmin = await Assert.ThrowsAsync<MelodexApiException>(() => manager.UpdateUserAsync(adminId, new UpdateUser(Password: "taken over now")));
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.Status);
        var conflict = await Assert.ThrowsAsync<MelodexApiException>(() => manager.RemoveUserAsync(adminId));
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        conflict = await Assert.ThrowsAsync<MelodexApiException>(() => manager.RemoveUserAsync(managerInfo.Id));
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);

        var taken = await Assert.ThrowsAsync<MelodexApiException>(() => manager.CreateUserAsync(new CreateUser("GUEST", "another password", [Scopes.Read])));
        Assert.Equal(HttpStatusCode.BadRequest, taken.Status);
        await manager.RemoveUserAsync(guest.Id);
        Assert.Equal(["admin", "manager"], (await admin.ListUsersAsync()).Select(u => u.Username).Order());
    }

    [Fact]
    public async Task Keys_can_do_no_more_than_their_owner()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var owner = await admin.CreateUserAsync(new CreateUser("maker", "making password", [Scopes.Read, Scopes.Keys]));
        var maker = new MelodexClient(await SignInAsync("maker", "making password"));

        var beyond = await Assert.ThrowsAsync<MelodexApiException>(() => maker.CreateApiKeyAsync(new CreateApiKey("Requester", [Scopes.Request])));
        Assert.Equal(HttpStatusCode.Forbidden, beyond.Status);
        // Admin on a key is everything its owner may.
        var everything = await maker.CreateApiKeyAsync(new CreateApiKey("Everything", [Scopes.Admin]));
        Assert.Equal("maker", everything.Info.Owner);

        var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Add(AccessService.KeyHeader, everything.Key);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("api/v1/library")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.PostAsJsonAsync("api/v1/requests", new CreateRequest(RequestKind.Track, Guid.NewGuid()), MelodexJson.Options)).StatusCode);

        // The owner given more, the key may do more; the admin sees every key, others only their own.
        await admin.UpdateUserAsync(owner.Id, new UpdateUser(Permissions: [Scopes.Read, Scopes.Keys, Scopes.Request]));
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.PostAsJsonAsync("api/v1/requests", new CreateRequest(RequestKind.Track, Guid.NewGuid()), MelodexJson.Options)).StatusCode);
        await admin.CreateApiKeyAsync(new CreateApiKey("Admin's own", [Scopes.Read]));
        Assert.Equal(["Everything"], (await maker.ListApiKeysAsync()).Select(k => k.Name));
        Assert.Equal(2, (await admin.ListApiKeysAsync()).Count);

        // Their keys go with them.
        await admin.RemoveUserAsync(owner.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("api/v1/library")).StatusCode);
    }

    [Fact]
    public async Task Each_user_keeps_their_own_appearance()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        Assert.Equal(new Appearance(ThemeColor.Pink, ThemeMode.Auto), (await admin.GetAuthStatusAsync()).Appearance);

        await admin.SetAppearanceAsync(new Appearance(ThemeColor.Green, ThemeMode.Black, HighContrast: true));
        Assert.Equal(new Appearance(ThemeColor.Green, ThemeMode.Black, true), (await admin.GetAuthStatusAsync()).Appearance);

        // Someone who may do nothing else may still choose how it looks to them.
        await admin.CreateUserAsync(new CreateUser("nobody", "nobody's password", []));
        var nobody = new MelodexClient(await SignInAsync("nobody", "nobody's password"));
        Assert.Equal(ThemeColor.Pink, (await nobody.GetAuthStatusAsync()).Appearance!.Color);
        await nobody.SetAppearanceAsync(new Appearance(ThemeColor.Blue, ThemeMode.Light));
        Assert.Equal(ThemeColor.Blue, (await nobody.GetAuthStatusAsync()).Appearance!.Color);
        Assert.Equal(ThemeColor.Green, (await admin.GetAuthStatusAsync()).Appearance!.Color);

        // Keys don't have an appearance.
        var key = await admin.CreateApiKeyAsync(new CreateApiKey("App", [Scopes.Admin]));
        var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Add(AccessService.KeyHeader, key.Key);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.PutAsJsonAsync("api/v1/auth/appearance", new Appearance(), MelodexJson.Options)).StatusCode);
    }

    [Fact]
    public async Task The_login_from_before_users_becomes_the_admin()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.Settings.Add(new Setting
            {
                Key = AccessService.LoginKey,
                ValueJson = JsonSerializer.Serialize(new AdminLogin { Username = "joe", PasswordHash = AccessService.HashPassword("an old password"), Stamp = "old" },
                    MelodexJson.Options),
            });
            await db.SaveChangesAsync();
        }

        var http = await SignInAsync("joe", "an old password");
        var status = await http.GetFromJsonAsync<AuthStatus>("api/v1/auth/status", MelodexJson.Options);
        Assert.True(status!.IsAdmin);
        Assert.True(status.SetUp);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Webhooks_get_signed_events_they_asked_for()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var webhook = await admin.CreateWebhookAsync(new SaveWebhook("Scans", "http://receiver.test/hook", [EventTypes.ScanCompleted]));
        Assert.StartsWith("whsec_", webhook.Secret);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.BackgroundTasks.Add(new BackgroundTask { Kind = LibraryScanTask.TaskKind, Status = BackgroundTaskStatus.Succeeded, FinishedAt = DateTime.UtcNow });
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = "Glory Box · Portishead", Requester = "admin", Status = RequestStatus.InLibrary,
            });
            await db.SaveChangesAsync();
        }

        var sent = await _receiver.WaitAsync(1);
        // The request finished too, but this webhook only wants scans.
        await Task.Delay(500);
        Assert.Single(_receiver.Received);
        var (headers, body) = sent[0];
        Assert.Equal(EventTypes.ScanCompleted, headers[WebhookSender.EventHeader]);
        var @event = JsonSerializer.Deserialize<MelodexEvent>(body, MelodexJson.Options)!;
        Assert.Equal(EventTypes.ScanCompleted, @event.Type);
        Assert.Equal(LibraryScanTask.TaskKind, @event.DataAs<ScanEventData>()!.Task.Kind);

        var signature = headers[WebhookSender.SignatureHeader].Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(WebhookSender.Sign(webhook.Secret, long.Parse(signature["t"]), body), signature["v1"]);

        var deliveries = await admin.ListWebhookDeliveriesAsync(webhook.Id);
        Assert.Equal(WebhookDeliveryState.Delivered, Assert.Single(deliveries).State);
    }

    [Fact]
    public async Task A_failed_delivery_is_tried_again_later()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        _receiver.Status = HttpStatusCode.InternalServerError;
        var webhook = await admin.CreateWebhookAsync(new SaveWebhook("Everything", "http://receiver.test/hook"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = "Roads · Portishead", Requester = "Some app", Status = RequestStatus.Failed,
            });
            await db.SaveChangesAsync();
        }

        var sent = await _receiver.WaitAsync(1);
        var @event = JsonSerializer.Deserialize<MelodexEvent>(sent[0].Body, MelodexJson.Options)!;
        Assert.Equal(EventTypes.RequestFailed, @event.Type);
        Assert.Equal("Some app", @event.DataAs<RequestEventData>()!.Requester);

        WebhookDeliveryInfo delivery;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            delivery = Assert.Single(await admin.ListWebhookDeliveriesAsync(webhook.Id));
        } while (delivery.Attempts == 0 && DateTime.UtcNow < deadline && await Delay());
        Assert.Equal(WebhookDeliveryState.Pending, delivery.State);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(500, delivery.ResponseStatus);
        Assert.True(delivery.NextAttemptAt > DateTime.UtcNow.AddSeconds(20));
    }

    [Fact]
    public async Task Testing_a_webhook_sends_a_ping_now()
    {
        var admin = new MelodexClient(_factory.CreateSignedInClient());
        var webhook = await admin.CreateWebhookAsync(new SaveWebhook("Test", "http://receiver.test/hook"));
        var delivery = await admin.TestWebhookAsync(webhook.Id);
        Assert.Equal(WebhookDeliveryState.Delivered, delivery.State);
        Assert.Equal(EventTypes.Ping, Assert.Single(_receiver.Received).Headers[WebhookSender.EventHeader]);

        var invalid = await Assert.ThrowsAsync<MelodexApiException>(() => admin.CreateWebhookAsync(new SaveWebhook("Bad", "not a url", ["nothing"])));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
    }

    [Fact]
    public async Task The_api_reference_is_open_and_says_what_each_operation_needs()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/docs")).StatusCode);
        var spec = await http.GetStringAsync("openapi/v1.json");
        Assert.Contains("\"X-Api-Key\"", spec);
        Assert.Contains("Needs the `request` scope.", spec);
        Assert.Contains("Open to anyone: no key needed.", spec);
    }

    private async Task<HttpClient> SignInAsync(string username, string password)
    {
        var http = _factory.CreateClient();
        (await http.PostAsJsonAsync("api/v1/auth/login", new SignIn(username, password))).EnsureSuccessStatusCode();
        return http;
    }

    private static async Task<bool> Delay()
    {
        await Task.Delay(100);
        return true;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private sealed class RemoteAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, then) =>
            {
                if (http.Request.Headers.TryGetValue(RemoteHeader, out var address))
                {
                    http.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                    http.Request.Headers.Remove(RemoteHeader);
                }
                return then(http);
            });
            next(app);
        };
    }

    /// <summary>Stands in for the app a webhook posts to.</summary>
    private sealed class Receiver : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(Dictionary<string, string> Headers, string Body)> _received = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public IReadOnlyList<(Dictionary<string, string> Headers, string Body)> Received => _received.ToList();

        public async Task<IReadOnlyList<(Dictionary<string, string> Headers, string Body)>> WaitAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (_received.Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.True(_received.Count >= count, $"Expected {count} deliveries, got {_received.Count}");
            return Received;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            _received.Enqueue((headers, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status);
        }
    }
}
