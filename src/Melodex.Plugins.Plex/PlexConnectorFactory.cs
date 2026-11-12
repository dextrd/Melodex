using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Plex;

/// <summary>Entry point for the Plex connector.</summary>
public sealed class PlexConnectorFactory : IPluginFactory
{
    public const string PluginId = "melodex.connector.plex";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Plex",
        Version = "0.1.0",
        Type = PluginType.Connector,
        ContractVersion = ContractVersions.Connector.ToString(),
        Description = "Tells Plex Media Server to scan the folders new music is filed in, and finds it there.",
        Homepage = "https://www.plex.tv",
        Capabilities = [ConnectorCapabilities.SelectiveScan, ConnectorCapabilities.FullScan, ConnectorCapabilities.ServerIdentity],
        Access = new PluginAccess { Network = ["Your Plex server"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["url", "token"],
              "properties": {
                "url": {
                  "type": "string",
                  "format": "uri",
                  "title": "Server URL",
                  "description": "Where Melodex reaches Plex, for example http://plex:32400.",
                  "default": "http://plex:32400"
                },
                "token": {
                  "type": "string",
                  "title": "Plex token",
                  "description": "The X-Plex-Token of the server's owner or an admin. Plex's support article \"Finding an authentication token\" shows where to find it.",
                  "writeOnly": true
                },
                "library": {
                  "type": "string",
                  "title": "Music library",
                  "description": "The name of the Plex library that holds the music Melodex files. Leave empty for the first music library."
                },
                "musicFolder": {
                  "type": "string",
                  "title": "Music folder on the Plex server",
                  "description": "Where Plex sees the music folder Melodex files into, for example /music. Leave empty for the library's first folder."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new PlexConnector(context);
}
