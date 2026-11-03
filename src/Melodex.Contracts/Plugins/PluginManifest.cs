using System.Text.Json;
using System.Text.Json.Serialization;

namespace Melodex.Contracts.Plugins;

/// <summary>The kinds of plugin.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginType>))]
public enum PluginType
{
    /// <summary>Catalogue data: what exists.</summary>
    Index,

    /// <summary>Fetches audio files into staging. Runs out of process behind the HTTP contract.</summary>
    Retrieval,

    /// <summary>Tells a media server about new music.</summary>
    Connector,

    /// <summary>Pictures of artists and albums. Part of the catalogue, alongside index plugins.</summary>
    Artwork,

    /// <summary>The words of songs, written beside or into the files in the library.</summary>
    Lyrics,
}

/// <summary>
/// Describes a plugin to the core and the web app. Every plugin ships one: .NET plugins return it from
/// <see cref="IPluginFactory.Manifest"/>, retrieval plugins serve it at <c>GET /v1/manifest</c>.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Stable, globally unique ID in reverse-DNS style, for example <c>melodex.index.musicbrainz</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>The plugin's own version (semver).</summary>
    public required string Version { get; init; }

    /// <summary>Which contract the plugin implements.</summary>
    public required PluginType Type { get; init; }

    /// <summary>The contract version the plugin was built against, as <c>major.minor</c>.</summary>
    public required string ContractVersion { get; init; }

    /// <summary>Short description shown in the plugin catalogue.</summary>
    public string? Description { get; init; }

    /// <summary>Project homepage or repository.</summary>
    public string? Homepage { get; init; }

    /// <summary>
    /// Capability names from <see cref="Index.IndexCapabilities"/>, <see cref="Retrieval.RetrievalCapabilities"/>,
    /// <see cref="Connectors.ConnectorCapabilities"/>, <see cref="Artwork.ArtworkCapabilities"/> or <see cref="Lyrics.LyricsCapabilities"/>. The core and API only offer what is declared here.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>
    /// JSON Schema (draft 2020-12) for the plugin's settings. The web app renders the setup form from it.
    /// Mark secrets with <c>"writeOnly": true</c> so the API never returns them.
    /// </summary>
    public JsonElement? SettingsSchema { get; init; }

    /// <summary>What the plugin touches: hosts it calls and paths it reads or writes. Shown before install.</summary>
    public PluginAccess Access { get; init; } = new();

    /// <summary>
    /// The ID of another plugin whose settings this one uses, for example a media server's connection. The plugin has
    /// no settings of its own, can only be set up once that plugin is, and gets the first such instance's settings.
    /// </summary>
    public string? UsesSettingsOf { get; init; }

    /// <summary>The highest request rate the core may send through this plugin, if the upstream service has one.</summary>
    public double? MaxRequestsPerSecond { get; init; }

    /// <summary>
    /// Index plugins only: the provider scheme of the IDs the plugin hands out and takes, for example <c>musicbrainz</c>
    /// (see <see cref="Catalogue.Providers"/>). With several catalogues, the core asks each about its own IDs.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>Retrieval plugins only: the formats and quality the plugin can produce.</summary>
    public RetrievalOutput? Retrieval { get; init; }
}

/// <summary>What a retrieval plugin can produce. The user picks within this in the plugin's settings.</summary>
public sealed record RetrievalOutput
{
    /// <summary>Output formats, for example <c>opus</c>, <c>m4a</c>, <c>mp3</c> or <c>flac</c>.</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>Lowest bitrate the plugin will deliver, for lossy formats.</summary>
    public int? MinBitrateKbps { get; init; }

    /// <summary>Highest bitrate the plugin can deliver, for lossy formats.</summary>
    public int? MaxBitrateKbps { get; init; }

    /// <summary>Whether any of the formats is lossless.</summary>
    public bool Lossless { get; init; }
}

/// <summary>The network and file access a plugin needs, declared up front.</summary>
public sealed record PluginAccess
{
    /// <summary>Hosts the plugin contacts, for example <c>musicbrainz.org</c>. Use <c>*</c> for user-configured hosts.</summary>
    public IReadOnlyList<string> Network { get; init; } = [];

    /// <summary>Paths or volumes the plugin reads or writes, for example <c>staging (write)</c>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}
