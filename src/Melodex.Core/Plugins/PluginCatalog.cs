using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Plugins;
using Melodex.Plugins.Deezer;
using Melodex.Plugins.FanartTv;
using Melodex.Plugins.Jellyfin;
using Melodex.Plugins.Lrclib;
using Melodex.Plugins.Wikimedia;
using Melodex.Plugins.MusicBrainz;
using Melodex.Plugins.Navidrome;
using Melodex.Plugins.Plex;

namespace Melodex.Core.Plugins;

/// <summary>A plugin the core found, and whether it can be used.</summary>
/// <param name="Manifest">The plugin's manifest.</param>
/// <param name="Factory">Creates instances; null when the plugin was rejected.</param>
/// <param name="Source"><c>built-in</c>, or the plugin's folder.</param>
/// <param name="Rejection">Why the plugin can't be loaded, if it can't.</param>
public sealed record DiscoveredPlugin(PluginManifest Manifest, IPluginFactory? Factory, string Source, string? Rejection);

/// <summary>
/// Finds in-process plugins (index, artwork and media-server plugins) at startup: the ones built into the core, plus every
/// assembly in a subfolder of the plugins folder. Plugins built for a contract the core doesn't support are listed
/// with the reason, but never instantiated.
/// </summary>
public sealed class PluginCatalog
{
    private static readonly Assembly[] BuiltIn =
        [typeof(MusicBrainzIndexFactory).Assembly, typeof(NavidromeConnectorFactory).Assembly, typeof(FanartTvFactory).Assembly,
         typeof(WikimediaCommonsFactory).Assembly, typeof(DeezerFactory).Assembly, typeof(LrclibFactory).Assembly,
         typeof(PlexConnectorFactory).Assembly, typeof(JellyfinConnectorFactory).Assembly];

    private readonly Dictionary<string, DiscoveredPlugin> _plugins = new(StringComparer.Ordinal);

    public PluginCatalog(IOptions<MelodexOptions> options, ILogger<PluginCatalog> logger)
    {
        foreach (var assembly in BuiltIn)
            Register(assembly, "built-in", logger);

        var root = options.Value.PluginsPath;
        if (!Directory.Exists(root))
            return;
        foreach (var folder in Directory.EnumerateDirectories(root))
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try
            {
                var assembly = new PluginLoadContext(dll).LoadFromAssemblyPath(Path.GetFullPath(dll));
                Register(assembly, folder, logger);
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                logger.LogDebug(e, "Skipped {Assembly}: not a loadable plugin assembly", dll);
            }
        }
    }

    public IEnumerable<DiscoveredPlugin> All => _plugins.Values;

    public DiscoveredPlugin? Find(string pluginId) => _plugins.GetValueOrDefault(pluginId);

    private void Register(Assembly assembly, string source, ILogger logger)
    {
        var factories = assembly.GetExportedTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IPluginFactory).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null);

        foreach (var type in factories)
        {
            var factory = (IPluginFactory)Activator.CreateInstance(type)!;
            var manifest = factory.Manifest;
            var rejection = Check(manifest);
            if (rejection is not null)
                logger.LogWarning("Plugin {Plugin} ({Source}) can't be loaded: {Reason}", manifest.Id, source, rejection);
            else if (_plugins.ContainsKey(manifest.Id))
                rejection = $"Another plugin with ID {manifest.Id} is already loaded.";

            if (!_plugins.ContainsKey(manifest.Id) || rejection is null)
                _plugins[manifest.Id] = new DiscoveredPlugin(manifest, rejection is null ? factory : null, source, rejection);
        }
    }

    private static string? Check(PluginManifest manifest)
    {
        if (manifest.Type == PluginType.Retrieval)
            return "Retrieval plugins run in their own container and are added by URL, not loaded in process.";
        ContractVersion version;
        try
        {
            version = ContractVersion.Parse(manifest.ContractVersion);
        }
        catch (FormatException e)
        {
            return e.Message;
        }
        var supported = ContractVersions.For(manifest.Type);
        return supported.Supports(version)
            ? null
            : $"Built for {manifest.Type} contract {version}; this version of Melodex supports {supported.Major}.0 to {supported}.";
    }

    /// <summary>Loads a plugin's own dependencies privately, but shares the contracts with the core.</summary>
    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "Melodex.Contracts" || name.Name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true)
                return null;
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }
    }
}
