using System.Text.Json;
using MetaBrainz.MusicBrainz;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Index;

namespace Melodex.Plugins.MusicBrainz;

/// <summary>Entry point for the MusicBrainz index plugin.</summary>
public sealed class MusicBrainzIndexFactory : IPluginFactory
{
    public const string PluginId = "melodex.index.musicbrainz";

    static MusicBrainzIndexFactory()
    {
        // The core rate-limits every plugin's HttpClient from the manifest, so the library mustn't add its own delay.
        Query.DelayBetweenRequests = 0;
    }

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "MusicBrainz",
        Version = "0.1.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Description = "Catalogue data from MusicBrainz, the open music encyclopedia.",
        Homepage = "https://musicbrainz.org",
        Capabilities = [IndexCapabilities.IsrcLookup],
        Provider = Providers.MusicBrainz,
        Access = new PluginAccess { Network = ["musicbrainz.org (or the server set in its settings)"] },
        MaxRequestsPerSecond = 1,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "server": {
                  "type": "string",
                  "format": "uri",
                  "title": "Server",
                  "description": "Use your own MusicBrainz mirror instead of musicbrainz.org.",
                  "default": "https://musicbrainz.org"
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new MusicBrainzIndex(context);
}
