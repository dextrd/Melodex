using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Artwork;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Plugins;

namespace Melodex.Core.Api;

public static class PluginEndpoints
{
    public static RouteGroupBuilder MapPluginEndpoints(this RouteGroupBuilder api)
    {
        var plugins = api.MapGroup("/plugins").WithTags("Plugins").RequireScope(Scopes.Settings);

        plugins.MapGet("", async (MelodexDbContext db, PluginCatalog catalog, CancellationToken ct) =>
                (await db.PluginInstances.OrderBy(p => p.Type).ThenBy(p => p.Priority).ThenBy(p => p.CreatedAt).ToListAsync(ct))
                .Select(p => ToInfo(p, catalog)).ToList())
            .WithName("ListPlugins").WithSummary("Configured plugins by type, in the order they're used. Secrets are left out.");

        plugins.MapGet("/available", (PluginCatalog catalog) => catalog.All
                .Where(p => p.Manifest.Type != PluginType.Retrieval)
                .Select(p => new AvailablePlugin(p.Manifest, p.Source, p.Rejection)).ToList())
            .WithName("ListAvailablePlugins")
            .WithSummary("Index, artwork and media-server plugins loaded into the core. Retrieval plugins run in their own containers and are added by URL.");

        plugins.MapPost("", async (AddPlugin body, MelodexDbContext db, PluginCatalog catalog, RetrievalPlugins retrieval, ArtworkCache artwork, CancellationToken ct) =>
            {
                PluginManifest manifest;
                string? endpoint = null;
                if (body.Endpoint is { Length: > 0 } url)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        return Problem("endpoint", "Must be an http or https URL, for example http://retriever-ytdlp:8080.");
                    try
                    {
                        manifest = await RetrievalClient.GetManifestAsync(retrieval.Client(url), ct);
                    }
                    catch (Exception e) when (e is HttpRequestException or RetrievalException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        return Problem("endpoint", $"Couldn't read the plugin's manifest from {url}: {e.Message}");
                    }
                    if (manifest.Type != PluginType.Retrieval)
                        return Problem("endpoint", $"{manifest.Name} is a {manifest.Type.ToString().ToLowerInvariant()} plugin, not a retrieval plugin.");
                    if (!ContractVersions.Retrieval.Supports(ContractVersion.Parse(manifest.ContractVersion)))
                        return Problem("endpoint", $"{manifest.Name} is built for retrieval contract {manifest.ContractVersion}; this Melodex supports {ContractVersions.Retrieval}.");
                    endpoint = url.TrimEnd('/');
                }
                else if (body.PluginId is { } pluginId && catalog.Find(pluginId) is { } found)
                {
                    if (found.Factory is null)
                        return Problem("pluginId", found.Rejection ?? "This plugin can't be loaded.");
                    if (found.Manifest.UsesSettingsOf is { } lender && !await db.PluginInstances.AnyAsync(p => p.PluginId == lender, ct))
                        return Problem("pluginId", $"{found.Manifest.Name} uses the connection of {catalog.Find(lender)?.Manifest.Name ?? lender}; set that up first.");
                    manifest = found.Manifest;
                }
                else
                {
                    return Problem("pluginId", "Give a retrieval plugin's endpoint, or the ID of a plugin from /plugins/available.");
                }

                var settings = SettingsSchema.Defaults(manifest.SettingsSchema);
                if (body.Settings is { ValueKind: JsonValueKind.Object } given)
                    foreach (var (name, value) in JsonNode.Parse(given.GetRawText())!.AsObject())
                        settings[name] = value?.DeepClone();
                var errors = SettingsSchema.Validate(manifest.SettingsSchema, settings);
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors.ToDictionary(e => $"settings.{e.Key}", e => e.Value));

                var last = await db.PluginInstances.Where(p => p.Type == manifest.Type).MaxAsync(p => (int?)p.Priority, ct) ?? -1;
                var instance = new PluginInstance
                {
                    PluginId = FromNavidex.PluginId(manifest.Id),
                    Type = manifest.Type,
                    Name = string.IsNullOrWhiteSpace(body.Name) ? manifest.Name : body.Name.Trim(),
                    ContractVersion = manifest.ContractVersion,
                    Endpoint = endpoint,
                    ManifestJson = endpoint is null ? null : JsonSerializer.Serialize(manifest, RetrievalJson.Options),
                    SettingsJson = settings.ToJsonString(),
                    Priority = last + 1,
                };
                db.PluginInstances.Add(instance);
                await db.SaveChangesAsync(ct);
                await ArtworkChangedAsync(db, instance, catalog, artwork, ct);
                return Results.Created($"/api/v1/plugins/{instance.Id}", ToInfo(instance, catalog));
            })
            .WithName("AddPlugin").WithSummary("Set up a plugin: a retrieval plugin by its container URL, or a loaded one by ID. It goes last in the order.")
            .Produces<PluginInfo>(StatusCodes.Status201Created).ProducesValidationProblem();

        plugins.MapPut("/{id:guid}", async (Guid id, UpdatePlugin body, MelodexDbContext db, PluginCatalog catalog, ArtworkCache artwork, CancellationToken ct) =>
            {
                if (await db.PluginInstances.FindAsync([id], ct) is not { } instance)
                    return Results.NotFound();
                var manifest = Manifest(instance, catalog);
                if (body.Settings is { ValueKind: JsonValueKind.Object } given)
                {
                    var current = JsonNode.Parse(instance.SettingsJson)!.AsObject();
                    var merged = SettingsSchema.Merge(manifest?.SettingsSchema, current, JsonNode.Parse(given.GetRawText())!.AsObject());
                    var errors = SettingsSchema.Validate(manifest?.SettingsSchema, merged);
                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors.ToDictionary(e => $"settings.{e.Key}", e => e.Value));
                    instance.SettingsJson = merged.ToJsonString();
                }
                if (body.Name is { } name && !string.IsNullOrWhiteSpace(name))
                    instance.Name = name.Trim();
                if (body.Enabled is { } enabled)
                    instance.Enabled = enabled;
                if (body.Endpoint is { } endpoint && instance.Type == PluginType.Retrieval)
                {
                    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        return Problem("endpoint", "Must be an http or https URL.");
                    instance.Endpoint = endpoint.TrimEnd('/');
                }
                await db.SaveChangesAsync(ct);
                await ArtworkChangedAsync(db, instance, catalog, artwork, ct);
                return Results.Ok(ToInfo(instance, catalog));
            })
            .WithName("UpdatePlugin").WithSummary("Rename, reconfigure, enable or disable a plugin.")
            .Produces<PluginInfo>().ProducesValidationProblem();

        plugins.MapPut("/order", async (PluginOrder body, MelodexDbContext db, PluginCatalog catalog, ArtworkCache artwork, CancellationToken ct) =>
            {
                var instances = await db.PluginInstances.Where(p => body.Ids.Contains(p.Id)).ToListAsync(ct);
                if (instances.Count != body.Ids.Count || instances.Select(p => p.Type).Distinct().Count() > 1)
                    return Problem("ids", "List every ID once, all of one plugin type.");
                for (var i = 0; i < body.Ids.Count; i++)
                    instances.First(p => p.Id == body.Ids[i]).Priority = i;
                await db.SaveChangesAsync(ct);
                await ArtworkChangedAsync(db, instances[0], catalog, artwork, ct);
                return Results.NoContent();
            })
            .WithName("OrderPlugins").WithSummary("Set the order plugins of one type are used in: the fallback chain, for retrieval plugins; which picture wins, for artwork plugins.");

        plugins.MapPost("/{id:guid}/test", async (Guid id, MelodexDbContext db, PluginCatalog catalog, PluginHost host, RetrievalPlugins retrieval, CancellationToken ct) =>
            {
                if (await db.PluginInstances.FindAsync([id], ct) is not { } instance)
                    return Results.NotFound();
                HealthReport report;
                try
                {
                    if (instance.Type == PluginType.Retrieval)
                    {
                        // Testing also picks up a newer manifest, for example after the container was updated.
                        var manifest = await RetrievalClient.GetManifestAsync(retrieval.Client(instance.Endpoint!), ct);
                        instance.ManifestJson = JsonSerializer.Serialize(manifest, RetrievalJson.Options);
                        report = await retrieval.Create(instance).CheckHealthAsync(ct);
                    }
                    else if (await host.ResolveAsync(db, instance, ct) is { } running)
                    {
                        report = await host.Get<IPlugin>(running).CheckHealthAsync(ct);
                    }
                    else
                    {
                        var lender = catalog.Find(instance.PluginId)?.Manifest.UsesSettingsOf;
                        report = new HealthReport(HealthStatus.Unhealthy,
                            $"This uses the connection of {(lender is null ? "another plugin" : catalog.Find(lender)?.Manifest.Name ?? lender)}, which isn't set up.");
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    report = new HealthReport(HealthStatus.Unhealthy, e.Message);
                }
                instance.Health = report.Status;
                instance.HealthMessage = report.Message;
                instance.HealthCheckedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Ok(ToInfo(instance, catalog));
            })
            .WithName("TestPlugin").WithSummary("Check the plugin works with its settings, and record the result.")
            .Produces<PluginInfo>();

        plugins.MapPost("/{id:guid}/checks/{check}", async (Guid id, string check, RunPluginCheck body, MelodexDbContext db, PluginCatalog catalog,
                RetrievalPlugins retrieval, IImportWorker worker, CancellationToken ct) =>
            {
                if (await db.PluginInstances.FindAsync([id], ct) is not { } instance)
                    return Results.NotFound();
                var manifest = Manifest(instance, catalog);
                if (instance.Type != PluginType.Retrieval || manifest?.Checks.Any(c => c.Id == check) != true)
                    return Problem("check", "This plugin has no such check.");
                // Checked with the settings as they are in the form, saved or not.
                var settings = JsonNode.Parse(instance.SettingsJson)!.AsObject();
                if (body.Settings is { ValueKind: JsonValueKind.Object } given)
                    settings = SettingsSchema.Merge(manifest.SettingsSchema, settings, JsonNode.Parse(given.GetRawText())!.AsObject());
                var client = new RetrievalClient(retrieval.Client(instance.Endpoint!), JsonSerializer.SerializeToElement(settings));
                PluginCheckResult result;
                try
                {
                    var found = await client.RunCheckAsync(check, ct);
                    result = new PluginCheckResult(found.Passed, found.Message);
                    if (check == "reflinks")
                    {
                        // The plugin can only say about its side: the import worker copies on into the library.
                        var library = await worker.CheckReflinksAsync(ct);
                        result = new PluginCheckResult(found.Passed && library.Works, library.Works
                            ? $"{found.Message} From staging to your music library, they can too."
                            : $"{found.Message} From staging to your music library, files can't share their disk space: {library.Reason}.");
                    }
                }
                catch (Exception e) when (e is RetrievalException or ImportException or HttpRequestException)
                {
                    result = new PluginCheckResult(false, $"Couldn't check: {e.Message}");
                }
                return Results.Ok(result);
            })
            .WithName("RunPluginCheck").WithSummary("Run one of the checks a plugin's settings offer, with the settings given or the saved ones.")
            .Produces<PluginCheckResult>().ProducesValidationProblem();

        plugins.MapDelete("/{id:guid}", async (Guid id, MelodexDbContext db, PluginCatalog catalog, ArtworkCache artwork, CancellationToken ct) =>
            {
                if (await db.PluginInstances.FindAsync([id], ct) is not { } instance)
                    return Results.NotFound();
                db.PluginInstances.Remove(instance);
                await db.SaveChangesAsync(ct);
                await ArtworkChangedAsync(db, instance, catalog, artwork, ct);
                return Results.NoContent();
            })
            .WithName("RemovePlugin");

        return api;
    }

    /// <summary>
    /// Pictures depend on which artwork plugins run, in what order, with what settings, including settings an artwork
    /// plugin borrows. After a change to any of those, the cached ones are forgotten.
    /// </summary>
    private static async Task ArtworkChangedAsync(MelodexDbContext db, PluginInstance changed, PluginCatalog catalog, ArtworkCache artwork, CancellationToken ct)
    {
        var lent = changed.Type != PluginType.Artwork && (await db.PluginInstances.Where(p => p.Type == PluginType.Artwork).Select(p => p.PluginId).ToListAsync(ct))
            .Any(id => catalog.Find(id)?.Manifest.UsesSettingsOf == changed.PluginId);
        // A catalogue changing changes which IDs albums have, and so which covers can be found for them.
        if (changed.Type is PluginType.Artwork or PluginType.Index || lent)
            await artwork.ResetAsync(ct);
    }

    private static PluginManifest? Manifest(PluginInstance instance, PluginCatalog catalog) =>
        instance.ManifestJson is not null
            ? JsonSerializer.Deserialize<PluginManifest>(instance.ManifestJson, RetrievalJson.Options)
            : catalog.Find(instance.PluginId)?.Manifest;

    public static PluginInfo ToInfo(PluginInstance p, PluginCatalog catalog)
    {
        var manifest = Manifest(p, catalog);
        var settings = SettingsSchema.Redact(manifest?.SettingsSchema, JsonNode.Parse(p.SettingsJson)!.AsObject());
        return new PluginInfo(p.Id, p.PluginId, p.Type, p.Name, p.Endpoint, p.Priority, p.Enabled, p.Health, p.HealthMessage, p.HealthCheckedAt,
            JsonDocument.Parse(settings.ToJsonString()).RootElement.Clone(), manifest);
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
