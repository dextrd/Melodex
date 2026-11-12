using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Jellyfin;

/// <summary>Entry point for the Jellyfin connector.</summary>
public sealed class JellyfinConnectorFactory : IPluginFactory
{
    public const string PluginId = "melodex.connector.jellyfin";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Jellyfin",
        Version = "0.1.0",
        Type = PluginType.Connector,
        ContractVersion = ContractVersions.Connector.ToString(),
        Description = "Tells Jellyfin to scan the folders new music is filed in, finds it there, and can keep each user's playlists the same in both.",
        Homepage = "https://jellyfin.org",
        Capabilities = [ConnectorCapabilities.SelectiveScan, ConnectorCapabilities.FullScan, ConnectorCapabilities.ServerIdentity,
            ConnectorCapabilities.UserPlaylists],
        Access = new PluginAccess { Network = ["Your Jellyfin server"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["url", "apiKey"],
              "properties": {
                "url": {
                  "type": "string",
                  "format": "uri",
                  "title": "Server URL",
                  "description": "Where Melodex reaches Jellyfin, for example http://jellyfin:8096.",
                  "default": "http://jellyfin:8096"
                },
                "apiKey": {
                  "type": "string",
                  "title": "API key",
                  "description": "An API key made for Melodex in Jellyfin's Dashboard, under API Keys.",
                  "writeOnly": true
                },
                "library": {
                  "type": "string",
                  "title": "Music library",
                  "description": "The name of the Jellyfin library that holds the music Melodex files. Leave empty for the first music library."
                },
                "musicFolder": {
                  "type": "string",
                  "title": "Music folder on the Jellyfin server",
                  "description": "Where Jellyfin sees the music folder Melodex files into, for example /music. Leave empty for the library's first folder."
                },
                "syncPlaylists": {
                  "type": "boolean",
                  "title": "Sync playlists",
                  "description": "Keep each user's playlists the same in Melodex and Jellyfin, both ways. Each user links their own Jellyfin login on their account page.",
                  "default": false
                },
                "playlistFolders": {
                  "type": "string",
                  "title": "Playlist folder separator",
                  "description": "Jellyfin has no folders, so a folder is part of a playlist's name: with /, Moods/Calm is a playlist Calm in a folder Moods. Up to 3 characters, or empty for no folders. Used while Jellyfin is the media server in use.",
                  "default": "/",
                  "maxLength": 3,
                  "pattern": "^\\S*$",
                  "patternMessage": "Use up to 3 characters that aren't spaces, or none."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new JellyfinConnector(context);
}
