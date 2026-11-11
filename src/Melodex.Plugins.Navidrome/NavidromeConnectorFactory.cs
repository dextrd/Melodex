using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Navidrome;

/// <summary>Entry point for the Navidrome connector.</summary>
public sealed class NavidromeConnectorFactory : IPluginFactory
{
    public const string PluginId = "melodex.connector.navidrome";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Navidrome",
        Version = "0.1.0",
        Type = PluginType.Connector,
        ContractVersion = ContractVersions.Connector.ToString(),
        Description = "Tells Navidrome to scan when new music is filed, finds it there, and can keep each user's playlists the same in both.",
        Homepage = "https://www.navidrome.org",
        Capabilities = [ConnectorCapabilities.FullScan, ConnectorCapabilities.Playlists, ConnectorCapabilities.Favourites, ConnectorCapabilities.LibraryRead,
            ConnectorCapabilities.UserPlaylists, ConnectorCapabilities.ServerIdentity],
        Access = new PluginAccess { Network = ["Your Navidrome server"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["url", "username", "password"],
              "properties": {
                "url": {
                  "type": "string",
                  "format": "uri",
                  "title": "Server URL",
                  "description": "Where Melodex reaches Navidrome, for example http://navidrome:4533.",
                  "default": "http://navidrome:4533"
                },
                "username": {
                  "type": "string",
                  "title": "Admin username",
                  "description": "The username of a Navidrome ADMIN, not an ordinary user: Melodex starts scans with it, and only admins can. Each user's own login, for their playlists, is linked on their account page."
                },
                "password": {
                  "type": "string",
                  "title": "Admin password",
                  "description": "That admin's Navidrome password.",
                  "writeOnly": true
                },
                "syncPlaylists": {
                  "type": "boolean",
                  "title": "Sync playlists",
                  "description": "Keep each user's playlists the same in Melodex and Navidrome, both ways. Each user links their own Navidrome login on their account page.",
                  "default": false
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new NavidromeConnector(context);
}
