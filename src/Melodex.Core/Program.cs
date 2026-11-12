using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core;
using Melodex.Core.Access;
using Melodex.Core.Api;
using Melodex.Core.Artwork;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Diagnostics;
using Melodex.Core.Events;
using Melodex.Core.Import;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Playlists;
using Melodex.Core.Plugins;
using Melodex.Core.Requests;
using Melodex.Core.Tasks;

var builder = WebApplication.CreateBuilder(args);
FromNavidex.AddOldSettings(builder.Configuration);

var options = builder.Configuration.GetSection(MelodexOptions.Section).Get<MelodexOptions>() ?? new MelodexOptions();
builder.Services.Configure<MelodexOptions>(builder.Configuration.GetSection(MelodexOptions.Section));
builder.Services.AddOptions<PluginHttpOptions>();

Directory.CreateDirectory(options.DataPath);
var database = Path.Combine(options.DataPath, "melodex.db");
var reset = FactoryReset.ApplyIfRequested(options, database);
var movedDatabase = FromNavidex.MoveDatabase(options.DataPath, database);
builder.Services.AddSingleton<LibraryCache>();
builder.Services.AddSingleton<EventFeed>();
builder.Services.AddDbContext<MelodexDbContext>((services, db) => db
    .UseSqlite(builder.Configuration.GetConnectionString("Melodex") ?? $"Data Source={database}",
        MelodexDbContext.Sqlite)
    .AddInterceptors(services.GetRequiredService<LibraryCache>(), services.GetRequiredService<EventFeed>()));

// The activity log the web app shows. Registered as a provider so everything logged reaches it.
var activity = new ActivityLog();
builder.Services.AddSingleton(activity);
builder.Logging.AddProvider(new ActivityLogProvider(activity));
builder.Services.AddHostedService<LogArchive>();

// Who may call the API: users' sessions and apps' keys. The keys that sign
// sessions are kept with the data, so a restart doesn't sign everyone out.
builder.Services.AddMelodexAccess();
builder.Services.AddDataProtection()
    .SetApplicationName("Melodex")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataPath, "keys")));

builder.Services.ConfigureHttpJsonOptions(json => MelodexJson.Configure(json.SerializerOptions));

// Plugins
builder.Services.AddSingleton<PluginCatalog>();
builder.Services.AddSingleton<PluginHost>();
builder.Services.AddScoped<IndexPlugins>();
builder.Services.AddScoped<ConnectorPlugins>();
builder.Services.AddScoped<ArtworkPlugins>();
builder.Services.AddScoped<LyricsPlugins>();
builder.Services.AddSingleton<ArtworkCache>();
builder.Services.AddHostedService<ArtworkWarmer>();
builder.Services.AddScoped<RetrievalPlugins>();
builder.Services.AddHttpClient(RetrievalPlugins.HttpClientName, http => http.Timeout = TimeSpan.FromMinutes(2));

// Catalogue and library
builder.Services.AddScoped<CatalogueService>();
builder.Services.AddSingleton<OnDemandCatalogue>();
builder.Services.AddScoped<CatalogueRefresher>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<QualitySettings>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<LibraryMatcher>();
builder.Services.AddScoped<LibraryView>();
builder.Services.AddScoped<LocalMusic>();
builder.Services.AddScoped<LibraryRemover>();
builder.Services.AddScoped<LibraryFormatter>();
builder.Services.AddScoped<CoverChooser>();
builder.Services.AddScoped<LyricsFinder>();
builder.Services.AddScoped<PlaylistService>();
builder.Services.AddSingleton<PlaylistCovers>();
builder.Services.AddScoped<PlaylistSync>();
builder.Services.AddSingleton<PlaylistSyncer>();
builder.Services.AddHostedService(services => services.GetRequiredService<PlaylistSyncer>());
builder.Services.AddScoped<CatalogueSearch>();

// Background work
builder.Services.AddSingleton<TaskSignal>();
builder.Services.AddSingleton<TaskCancellations>();
builder.Services.AddScoped<TaskQueue>();
builder.Services.AddScoped<ITaskHandler, LibraryScanTask>();
builder.Services.AddScoped<ITaskHandler, ArtistRefreshTask>();
builder.Services.AddScoped<ITaskHandler, FormatTask>();
builder.Services.AddScoped<ITaskHandler, LyricsTask>();
builder.Services.AddScoped<ITaskHandler, SpectrumTask>();
builder.Services.AddHostedService<TaskWorker>();

// Requests: fetch, verify, import with beets, tell the media server
builder.Services.Configure<ImportWorkerOptions>(builder.Configuration.GetSection("Melodex:ImportWorker"));
builder.Services.AddOptions<RequestPipelineOptions>();
builder.Services.AddHttpClient<IImportWorker, HttpImportWorker>(HttpImportWorker.Configure);
builder.Services.AddSingleton<RequestSignal>();
builder.Services.AddSingleton<LibraryReading>();
builder.Services.AddSingleton<ScanFeed>();
builder.Services.AddSingleton<RequestCancellations>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<RequestPlanner>();
builder.Services.AddScoped<RequestPipeline>();
builder.Services.AddScoped<Upgrades>();
builder.Services.AddHostedService<UpgradeScheduler>();
builder.Services.AddSingleton<StreamSigner>();
builder.Services.AddScoped<ListenNowService>();
builder.Services.AddHostedService<ListenNowSweeper>();
builder.Services.AddHostedService<RequestWorker>();

// Events: announced on the event stream and sent to webhooks
builder.Services.AddHttpClient(WebhookSender.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddHostedService(services => services.GetRequiredService<WebhookSender>());
builder.Services.AddHostedService<EventAnnouncer>();

builder.Services.AddMelodexOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
    await db.Database.MigrateAsync();
    if (movedDatabase)
        app.Logger.LogInformation("Moved the database from Navidex to {Database}", database);
    if (reset)
        app.Logger.LogWarning("Melodex was reset, as the admin asked: open the web app to set it up again");
    await FromNavidex.RenamePluginsAsync(db);
    // Write-ahead logging, so pages keep reading while a scan writes. It's stored in the file, so this is once.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await IndexPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
    await ArtworkPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
    await LyricsPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
    await app.Services.GetRequiredService<StreamSigner>().InitialiseAsync(db);
}

// The way back in when the admin's password is lost: start once with Melodex__ResetLogin=true, then set a new one.
// Other users, and everyone's keys, are kept.
if (options.ResetLogin)
{
    await app.Services.GetRequiredService<AccessService>().ResetAdminAsync(CancellationToken.None);
    app.Logger.LogWarning("The admin's password was cleared, as ResetLogin is set. Open the web app to set a new one, then unset ResetLogin");
}

// The query planner's statistics, worked out in the background where they're missing or stale.
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    try
    {
        await scope.ServiceProvider.GetRequiredService<MelodexDbContext>().OptimiseAsync();
    }
    catch (Exception e)
    {
        app.Logger.LogWarning("Couldn't update the database's statistics: {Message}", e.Message);
    }
});

// Work out the library's views in the background, so the first visit after starting is quick too.
_ = app.Services.GetRequiredService<LibraryCache>().WarmAsync(CancellationToken.None);

// A catalogue source that's busy or down is the client's cue to try again later, not a server error.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (IndexUnavailableException e) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new { title = "The catalogue source is busy", detail = e.Message, status = 503 });
    }
});

app.UseAuthentication();
app.UseApiAccess();

// The spec is part of the public contract, so it's served in every environment.
app.MapOpenApi();
app.MapMelodexApiReference();

var api = app.MapGroup("/api/v1");
api.MapSystemEndpoints();
api.MapSetupEndpoints();
api.MapLibraryEndpoints();
api.MapCatalogueEndpoints();
api.MapRequestEndpoints();
api.MapListenEndpoints();
api.MapPluginEndpoints();
api.MapAccessEndpoints();
api.MapWebhookEndpoints();
api.MapPlaylistEndpoints();
api.MapUpgradeEndpoints();

// The web app: Blazor WebAssembly, served from the same origin and talking only to the public API.
app.MapStaticAssets();
// The page itself is checked on every visit, as /index.html is: kept by the browser, an old one would load the new
// app without the scripts it now needs.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
});

// Artists and albums a gap-filling catalogue added twice, before imports looked for the ones the core catalogue has,
// are merged: in the background, as there may be many to look at.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var catalogue = scope.ServiceProvider.GetRequiredService<CatalogueService>();
        if (await catalogue.MergeDuplicateArtistsAsync(app.Lifetime.ApplicationStopping) + await catalogue.MergeDuplicateAlbumsAsync(app.Lifetime.ApplicationStopping) > 0)
            app.Services.GetRequiredService<LibraryCache>().Invalidate();
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        app.Logger.LogWarning(e, "Couldn't merge artists and albums added twice");
    }
}));

app.Run();

public partial class Program;
