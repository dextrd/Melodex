using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Contracts.Plugins.Index;
using Melodex.Contracts.Plugins.Lyrics;
using Melodex.Contracts.Plugins.Remote;
using Melodex.Core.Data;
using Melodex.Plugins.Deezer;
using Melodex.Plugins.Lrclib;
using Melodex.Plugins.MusicBrainz;
using Melodex.Plugins.Wikimedia;

namespace Melodex.Core.Plugins;

/// <summary>
/// Creates and caches live instances of configured catalogue, artwork, lyrics and media-server plugins: loaded in
/// process, or reached at an address (<see cref="RemotePlugin"/>). An instance is rebuilt when its settings change;
/// its HTTP client (and so its rate limit) lives as long as the instance.
/// </summary>
public sealed class PluginHost(
    PluginCatalog catalog,
    IOptions<MelodexOptions> options,
    IOptions<PluginHttpOptions> httpOptions,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<Guid, (string Settings, IPlugin Plugin)> _instances = new();
    private readonly ConcurrentDictionary<string, PluginManifest?> _remoteManifests = new();

    public T Get<T>(PluginInstance instance) where T : class, IPlugin
    {
        // A plugin at an address is rebuilt when it moves or says something new about itself, as well.
        var key = instance.Endpoint is null ? instance.SettingsJson : $"{instance.SettingsJson}\n{instance.Endpoint}\n{instance.ManifestJson}";
        var cached = _instances.AddOrUpdate(instance.Id,
            _ => (key, Create(instance)),
            (_, existing) => existing.Settings == key ? existing : (key, Create(instance)));
        return cached.Plugin as T
               ?? throw new InvalidOperationException($"Plugin {instance.PluginId} doesn't implement {typeof(T).Name}.");
    }

    /// <summary>
    /// The instance as it runs: with the settings of the plugin it borrows them from, if it does. Null when that
    /// plugin isn't set up, so this one can't run.
    /// </summary>
    public async Task<PluginInstance?> ResolveAsync(MelodexDbContext db, PluginInstance instance, CancellationToken ct)
    {
        if (instance.Endpoint is not null || catalog.Find(instance.PluginId)?.Manifest.UsesSettingsOf is not { } lender)
            return instance;
        var settings = await db.PluginInstances.Where(p => p.PluginId == lender).OrderBy(p => p.Priority)
            .Select(p => p.SettingsJson).FirstOrDefaultAsync(ct);
        return settings is null
            ? null
            : new PluginInstance
            {
                Id = instance.Id, PluginId = instance.PluginId, Type = instance.Type, Name = instance.Name,
                ContractVersion = instance.ContractVersion, SettingsJson = settings, Priority = instance.Priority, Enabled = instance.Enabled,
            };
    }

    /// <summary>
    /// The manifest of a plugin that can run: as it said at its address when added or last tested, or as loaded. Null
    /// when it can't, such as after its plugin was taken out of the plugins folder.
    /// </summary>
    public PluginManifest? ManifestOf(PluginInstance instance) => instance.Endpoint is not null
        ? instance.ManifestJson is { } json
            ? _remoteManifests.GetOrAdd(json, j => JsonSerializer.Deserialize<PluginManifest>(j, RemoteJson.Options))
            : null
        : catalog.Find(instance.PluginId) is { Factory: not null } found ? found.Manifest : null;

    private IPlugin Create(PluginInstance instance)
    {
        if (instance.Endpoint is { } endpoint)
        {
            var manifest = ManifestOf(instance) ?? throw new InvalidOperationException($"{instance.Name} hasn't said what it is yet.");
            var http = PluginHttp.CreateClient(httpOptions.Value, manifest.MaxRequestsPerSecond);
            http.BaseAddress = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
            return RemotePlugin.Create(instance.Type, http, JsonDocument.Parse(instance.SettingsJson).RootElement.Clone(), manifest);
        }
        var plugin = catalog.Find(instance.PluginId)
                     ?? throw new InvalidOperationException($"Plugin {instance.PluginId} isn't installed.");
        if (plugin.Factory is null)
            throw new InvalidOperationException($"Plugin {instance.PluginId} can't be loaded: {plugin.Rejection}");

        var dataDirectory = Path.Combine(options.Value.DataPath, "plugins", instance.Id.ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        return plugin.Factory.Create(new PluginContext
        {
            Settings = JsonDocument.Parse(instance.SettingsJson).RootElement.Clone(),
            HttpClient = PluginHttp.CreateClient(httpOptions.Value, plugin.Manifest.MaxRequestsPerSecond),
            LoggerFactory = loggerFactory,
            DataDirectory = dataDirectory,
        });
    }
}

/// <summary>A catalogue that's on, with the provider scheme of its IDs.</summary>
public sealed record CatalogueIndex(PluginInstance Instance, IIndexPlugin Plugin, string Provider);

/// <summary>
/// The catalogues that are on, in the user's order. The first is the core catalogue; the ones after it fill in what
/// it doesn't have, and each is asked about entities by its own IDs. There may be none: Melodex is then a plain music
/// library, showing files as their tags say, without missing music or fetching.
/// </summary>
public sealed class IndexPlugins(MelodexDbContext db, PluginHost host)
{
    public const string DefaultsSetting = "index.defaults";

    public async Task<IReadOnlyList<CatalogueIndex>> GetAllAsync(CancellationToken cancellationToken)
    {
        var instances = await db.PluginInstances
            .Where(p => p.Type == PluginType.Index && p.Enabled)
            .OrderBy(p => p.Priority)
            .ToListAsync(cancellationToken);
        // A plugin that doesn't say is taken to use MusicBrainz IDs, as every index plugin did before the field existed.
        return instances
            .Select(i => new CatalogueIndex(i, host.Get<IIndexPlugin>(i), host.ManifestOf(i)?.Provider ?? Melodex.Contracts.Catalogue.Providers.MusicBrainz))
            .ToList();
    }

    /// <summary>
    /// On first run, sets up MusicBrainz as the catalogue, so it works out of the box. Only once: removed, it stays
    /// removed. An install from before this was offered once already has a catalogue, so nothing is added to it.
    /// </summary>
    public static async Task EnsureDefaultAsync(MelodexDbContext db, PluginCatalog catalog)
    {
        if (await db.Settings.FindAsync(DefaultsSetting) is not null)
            return;
        db.Settings.Add(new Setting { Key = DefaultsSetting, ValueJson = "true" });
        if (await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Index))
        {
            await db.SaveChangesAsync();
            return;
        }
        var manifest = catalog.Find(MusicBrainzIndexFactory.PluginId)!.Manifest;
        db.PluginInstances.Add(new PluginInstance
        {
            PluginId = manifest.Id,
            Type = PluginType.Index,
            Name = manifest.Name,
            ContractVersion = manifest.ContractVersion,
            SettingsJson = """{"server":"https://musicbrainz.org"}""",
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// The active media-server connector, if one is set up: the enabled instance with the lowest priority. Each file's ID
/// on the media server is the active one's, so they're forgotten when another becomes active, to be found again there.
/// </summary>
public sealed class ConnectorPlugins(MelodexDbContext db, PluginHost host)
{
    public const string ActiveSetting = "mediaServer.active";

    public async Task<(PluginInstance Instance, IMediaServerConnector Connector)?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var instance = await db.PluginInstances
            .Where(p => p.Type == PluginType.Connector && p.Enabled)
            .OrderBy(p => p.Priority)
            .FirstOrDefaultAsync(cancellationToken);
        if (instance is null)
            return null;
        await NoteActiveAsync(instance, cancellationToken);
        return (instance, host.Get<IMediaServerConnector>(instance));
    }

    private async Task NoteActiveAsync(PluginInstance instance, CancellationToken ct)
    {
        var value = JsonSerializer.Serialize(instance.Id);
        var setting = await db.Settings.FindAsync([ActiveSetting], ct);
        if (setting?.ValueJson == value)
            return;
        // The first time, on an install from before this was noted, the IDs are taken to be this server's if it's the only one.
        if (setting is not null || await db.PluginInstances.CountAsync(p => p.Type == PluginType.Connector, ct) > 1)
        {
            await db.LibraryFiles.Where(f => f.MediaServerTrackId != null).ExecuteUpdateAsync(f => f.SetProperty(x => x.MediaServerTrackId, (string?)null), ct);
            await db.PlaylistEntries.Where(e => e.ServerTrackId != null && e.Playlist.ServerInstanceId != instance.Id)
                .ExecuteUpdateAsync(e => e.SetProperty(x => x.ServerTrackId, (string?)null), ct);
        }
        if (setting is null)
            db.Settings.Add(new Setting { Key = ActiveSetting, ValueJson = value });
        else
            setting.ValueJson = value;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The enabled artwork plugins that can picture a subject, in the user's order.</summary>
public sealed class ArtworkPlugins(MelodexDbContext db, PluginHost host)
{
    public const string DefaultsSetting = "artwork.defaults";

    public async Task<IReadOnlyList<(PluginInstance Instance, IArtworkPlugin Plugin)>> ForAsync(ArtworkSubject subject, CancellationToken ct)
    {
        var capability = ArtworkCapabilities.For(subject);
        var instances = await db.PluginInstances.Where(p => p.Type == PluginType.Artwork && p.Enabled).OrderBy(p => p.Priority).ToListAsync(ct);
        var plugins = new List<(PluginInstance, IArtworkPlugin)>();
        foreach (var instance in instances)
        {
            if (host.ManifestOf(instance) is not { } manifest || !manifest.Capabilities.Contains(capability))
                continue;
            if (await host.ResolveAsync(db, instance, ct) is { } running)
                plugins.Add((instance, host.Get<IArtworkPlugin>(running)));
        }
        return plugins;
    }

    /// <summary>The artwork plugins set up out of the box, in order: none needs an account.</summary>
    public static readonly string[] Defaults = [CoverArtArchiveFactory.PluginId, WikimediaCommonsFactory.PluginId, DeezerFactory.PluginId];

    /// <summary>
    /// Sets up each default artwork plugin once, after any already there, so covers and artist pictures work out of the
    /// box. The setting remembers which were offered, so removing one sticks and a new default reaches existing installs.
    /// </summary>
    public static async Task EnsureDefaultAsync(MelodexDbContext db, PluginCatalog catalog)
    {
        var setting = await db.Settings.FindAsync(DefaultsSetting);
        // Before the setting listed plugins, it was "true" once the archive had been offered.
        var offered = setting?.ValueJson switch
        {
            null => [],
            "true" => [CoverArtArchiveFactory.PluginId],
            var json => JsonSerializer.Deserialize<List<string>>(json) ?? [],
        };
        var missing = Defaults.Except(offered).ToList();
        if (missing.Count == 0)
            return;

        var existing = await db.PluginInstances.Where(p => p.Type == PluginType.Artwork).ToListAsync();
        var priority = existing.Count == 0 ? 0 : existing.Max(p => p.Priority) + 1;
        foreach (var id in missing)
        {
            if (existing.Any(p => p.PluginId == id) || catalog.Find(id) is not { } found)
                continue;
            db.PluginInstances.Add(new PluginInstance
            {
                PluginId = found.Manifest.Id,
                Type = PluginType.Artwork,
                Name = found.Manifest.Name,
                Priority = priority++,
                ContractVersion = found.Manifest.ContractVersion,
                SettingsJson = SettingsSchema.Defaults(found.Manifest.SettingsSchema).ToJsonString(),
            });
        }
        var value = JsonSerializer.Serialize(offered.Concat(missing).ToList());
        if (setting is null)
            db.Settings.Add(new Setting { Key = DefaultsSetting, ValueJson = value });
        else
            setting.ValueJson = value;
        await db.SaveChangesAsync();
    }
}

/// <summary>The enabled lyrics plugins, in the user's order.</summary>
public sealed class LyricsPlugins(MelodexDbContext db, PluginHost host)
{
    public const string DefaultsSetting = "lyrics.defaults";

    public async Task<IReadOnlyList<(PluginInstance Instance, ILyricsPlugin Plugin)>> GetAsync(CancellationToken ct)
    {
        var instances = await db.PluginInstances.Where(p => p.Type == PluginType.Lyrics && p.Enabled).OrderBy(p => p.Priority).ToListAsync(ct);
        var plugins = new List<(PluginInstance, ILyricsPlugin)>();
        foreach (var instance in instances)
        {
            if (host.ManifestOf(instance) is null)
                continue;
            if (await host.ResolveAsync(db, instance, ct) is { } running)
                plugins.Add((instance, host.Get<ILyricsPlugin>(running)));
        }
        return plugins;
    }

    /// <summary>Sets up LRCLIB once, as it needs no account, so lyrics work out of the box. Removing it sticks.</summary>
    public static async Task EnsureDefaultAsync(MelodexDbContext db, PluginCatalog catalog)
    {
        if (await db.Settings.FindAsync(DefaultsSetting) is not null)
            return;
        if (catalog.Find(LrclibFactory.PluginId) is { } found && !await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Lyrics))
        {
            db.PluginInstances.Add(new PluginInstance
            {
                PluginId = found.Manifest.Id,
                Type = PluginType.Lyrics,
                Name = found.Manifest.Name,
                Priority = 0,
                ContractVersion = found.Manifest.ContractVersion,
                SettingsJson = SettingsSchema.Defaults(found.Manifest.SettingsSchema).ToJsonString(),
            });
        }
        db.Settings.Add(new Setting { Key = DefaultsSetting, ValueJson = JsonSerializer.Serialize(new[] { LrclibFactory.PluginId }) });
        await db.SaveChangesAsync();
    }
}
