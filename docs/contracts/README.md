# Plugin contracts

Melodex is pluggable on three axes (what exists, how music is fetched, and where it ends up), plus the pictures that
go with the catalogue and the words of the songs. Each has its own contract:

| Type | Answers | Runs | Contract | Reference plugin |
| --- | --- | --- | --- | --- |
| [Index plugin](index-plugins.md) | What exists | In process (.NET assembly) | `IIndexPlugin` v1.0 | MusicBrainz |
| [Retrieval plugin](retrieval-plugins.md) | How music is fetched | Own container, over HTTP | [`retrieval-v1.openapi.yaml`](retrieval-v1.openapi.yaml) v1.0 | yt-dlp |
| [Media-server connector](media-server-connectors.md) | Where music ends up | In process (.NET assembly) | `IMediaServerConnector` v1.3 | Navidrome (Subsonic API) |
| [Artwork plugin](artwork-plugins.md) | What it looks like | In process (.NET assembly) | `IArtworkPlugin` v1.0 | Cover Art Archive, Wikimedia Commons, Deezer |
| [Lyrics plugin](lyrics-plugins.md) | What the words are | In process (.NET assembly) | `ILyricsPlugin` v1.1 | LRCLIB |

The .NET types live in the `Melodex.Contracts` package (`src/Melodex.Contracts`). The XML docs on those types
are normative; these pages explain the intent and the rules around them.

## Rules for every plugin

- **IDs are your own.** Plugins take and return IDs in their own scheme (`ProviderId`). The core maps them onto its
  internal IDs through the crosswalk, so swapping a plugin never invalidates the library.
- **The core owns state.** Caching, rate limiting, retries, priorities and settings storage all live in the core.
  Plugins should be as stateless as their upstream service allows.
- **Declare, don't assume.** Optional features are switched on by capability names in the manifest. The core and
  the API only offer what the active plugin declares.
- **State what you touch.** Plugins get real network and file access. The manifest's `access` block lists the hosts
  and paths a plugin uses, and the web app shows it before install.
- **No UI code.** The setup form is rendered from the settings schema.

## Manifest

Every plugin ships a manifest (`PluginManifest`). .NET plugins return it from `IPluginFactory.Manifest`; retrieval
plugins serve it at `GET /v1/manifest`. Over JSON, names are camelCase and enums are lowercase strings.

```json
{
  "id": "melodex.retrieval.ytdlp",
  "name": "yt-dlp",
  "version": "0.1.0",
  "type": "retrieval",
  "contractVersion": "1.0",
  "description": "Fetches tracks and albums from YouTube with yt-dlp.",
  "homepage": "https://github.com/example/melodex-retriever-ytdlp",
  "capabilities": ["fetch-track", "fetch-album", "cancel"],
  "settingsSchema": {
    "type": "object",
    "properties": {
      "format": { "type": "string", "enum": ["opus", "m4a", "mp3"], "default": "opus", "title": "Output format" },
      "cookies": { "type": "string", "writeOnly": true, "title": "Cookies file contents" }
    },
    "required": ["format"]
  },
  "access": { "network": ["youtube.com", "googlevideo.com"], "files": ["staging (write)"] },
  "retrieval": { "formats": ["opus", "m4a", "mp3"], "maxBitrateKbps": 256, "lossless": false }
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Stable, globally unique, reverse-DNS style. Never change it between versions. |
| `name`, `version` | yes | Display name and the plugin's own semver. |
| `type` | yes | `index`, `retrieval`, `connector`, `artwork` or `lyrics`. |
| `contractVersion` | yes | The contract version the plugin was built against, `major.minor`. |
| `capabilities` | no | Optional features, from the capability list of the plugin's type. |
| `settingsSchema` | no | JSON Schema (draft 2020-12) for settings. Mark secrets `"writeOnly": true`; the API never returns them. |
| `access` | no | Hosts (`network`) and paths or volumes (`files`) the plugin uses. |
| `usesSettingsOf` | no | Another plugin's ID whose settings this one uses instead of its own; see [artwork plugins](artwork-plugins.md#borrowed-settings). |
| `maxRequestsPerSecond` | no | The upstream's rate limit; the core throttles calls to it. MusicBrainz is `1`. |
| `retrieval` | retrieval only | Output formats and bitrate range the user can choose within. |

## Contract versions

Each type is versioned on its own as `major.minor`:

- A **minor** bump only adds optional members (new optional fields, capabilities or interfaces). A core supporting
  `1.3` loads plugins built for `1.0` up to `1.3`.
- A **major** bump is breaking. The core refuses plugins whose major version differs from its own, or whose minor
  version is newer than its own, and shows why in Settings → Plugins.
- `GET /api/v1/version` reports the versions the running core supports.

## Lifecycle

1. **Install.** The user picks a plugin from the in-app catalogue. .NET plugins are dropped into the plugins folder
   and loaded at startup; retrieval plugins are containers the user adds to the Compose stack and registers by URL.
2. **Configure.** The web app renders the settings form from `settingsSchema`. The core validates settings against
   the schema before saving them.
3. **Test.** The Test button runs the plugin's health check with the entered settings, before they're saved.
4. **Run.** The core creates one instance per configuration and orders instances by priority: field priority for
   index plugins, fallback order for retrieval, artwork and lyrics plugins. Only one connector is active at a time in v1.
5. **Monitor.** The core repeats the health check periodically and shows the result next to the plugin.

## Writing a .NET plugin (index, artwork, lyrics and media-server plugins)

Reference `Melodex.Contracts`, then add one public `IPluginFactory` per plugin:

```csharp
public sealed class MusicBrainzFactory : IPluginFactory
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "melodex.index.musicbrainz",
        Name = "MusicBrainz",
        Version = "0.1.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Capabilities = [IndexCapabilities.IsrcLookup, IndexCapabilities.UpcLookup, IndexCapabilities.ExternalIds],
        Access = new() { Network = ["musicbrainz.org"] },
        MaxRequestsPerSecond = 1,
    };

    public IPlugin Create(PluginContext context) => new MusicBrainzIndex(context);
}
```

`Create` must not do network I/O. The `PluginContext` gives the instance its validated settings, an `HttpClient`
owned by the core (with a descriptive user agent), a logger factory and a private data directory.
