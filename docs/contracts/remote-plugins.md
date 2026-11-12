# Plugins at an address

Catalogue, artwork, lyrics and media-server plugins are usually .NET assemblies loaded into Melodex. Each can also run
on its own, in any language, and be reached at an address, as [retrieval plugins](retrieval-plugins.md) always are.
They're added under **Settings → Plugins → Add → Add your own plugin…** in the section for their kind. Melodex then
uses one exactly as it would one loaded in process: the same contract version, capabilities and rules as on the page
for its kind.

| Kind | Interface it stands for | Contract |
| --- | --- | --- |
| Catalogue | [`IIndexPlugin`](index-plugins.md) | Index v1.0 |
| Artwork | [`IArtworkPlugin`](artwork-plugins.md) | Artwork v1.0 |
| Lyrics | [`ILyricsPlugin`](lyrics-plugins.md) | Lyrics v1.1 |
| Media server | [`IMediaServerConnector`](media-server-connectors.md) | Connector v1.3 |

The models are the ones in `Melodex.Contracts`, serialized as the retrieval contract's are: camelCase names, enums as
camelCase strings, nulls left out, `TimeSpan` as `"hh:mm:ss"`, `byte[]` as base64.

## Every plugin

- `GET /v1/manifest` answers the [manifest](README.md#manifest). Its `type` says the kind, and must match the section
  it's added to. Its `contractVersion` must be one this Melodex supports for that kind. Melodex reads it when the plugin
  is added and again on each **Test**, so a plugin that's updated can declare more.
- Every other call is a `POST` with a JSON body of the instance's settings and the call's input:

  ```json
  { "settings": { "token": "…" }, "input": { "id": "a1" } }
  ```

  Settings are sent each time, so the plugin keeps no setup of its own. Secrets in them (`"writeOnly": true`) are sent
  as they are, so serve the plugin where only Melodex can reach it.
- `POST /v1/health` (no input) answers a `HealthReport`: `{ "status": "healthy" }`, `"degraded"` or `"unhealthy"`, with
  an optional `message`.
- **404** means nothing: no such artist, no lyrics, no picture. A list call answered with 404 is an empty list.
- Any other status from 400 up is a refusal, with a body of `{ "error": "Why, for the person using Melodex", "code": "…" }`.
  A code of `unavailable`, or a 503 or 429, means the source can't answer right now: Melodex tries again later and
  doesn't take it as "not found". The core retries a 503 or 429 itself first.
- The manifest's `maxRequestsPerSecond` is kept to, as it is for plugins loaded in process.

Optional features are asked for only when the manifest declares their capability. A call that isn't declared isn't
made.

## Catalogue

| Call | Input | Answers |
| --- | --- | --- |
| `POST /v1/search` | `IndexSearchQuery`: `{ "text", "kinds": ["artist", "album", "track"], "limit" }` | `IndexSearchResults` |
| `POST /v1/artist` | `{ "id" }` | `IndexArtist`, or 404 |
| `POST /v1/artist-albums` | `{ "id" }` of an artist | `IndexAlbum[]` |
| `POST /v1/album` | `{ "id" }` | `IndexAlbumDetail`, or 404 |
| `POST /v1/isrc` | `{ "isrc" }` | `IndexTrack[]`. Only with `isrc-lookup`. |
| `POST /v1/recording-albums` | `{ "id" }` of a recording | `IndexAlbum[]`. Only with `recording-albums`. |

IDs are the plugin's own; set the manifest's `provider` to their scheme, as on [index plugins](index-plugins.md).

## Artwork

| Call | Input | Answers |
| --- | --- | --- |
| `POST /v1/find` | `ArtworkQuery` | `ArtworkCandidate[]`, best first |
| `POST /v1/download` | `ArtworkCandidate` | The image itself, with its `Content-Type`, or 404 |

Declare `artists`, `albums` and `editions` for what the plugin can picture; it's only asked about those.

## Lyrics

| Call | Input | Answers |
| --- | --- | --- |
| `POST /v1/find` | `LyricsQuery` | `FoundLyrics`, or 404 |
| `POST /v1/search` | `LyricsSearch` | `LyricsResult[]`. Only with `search`. |

## Media server

| Call | Input | Answers |
| --- | --- | --- |
| `POST /v1/scan` | `{ "paths": [...] }`, library-relative | Any 2xx |
| `POST /v1/scan-status` | `{}` | `ScanStatus` |
| `POST /v1/resolve` | `TrackLookup` | The server's ID for the song, as a JSON string, or 404 |
| `POST /v1/server` | `{}` | `ServerIdentity`, or 404. Only with `server-identity`. |

With `user-playlists`, users' playlists sync with the server, each user signing in with their own login
(`{ "username", "password" }`):

| Call | Input | Answers |
| --- | --- | --- |
| `POST /v1/playlists/login` | `{ "login" }` | `HealthReport` for that login |
| `POST /v1/playlists/list` | `{ "login" }` | `UserPlaylist[]` |
| `POST /v1/playlists/get` | `{ "login", "playlistId" }` | `UserPlaylistSongs`, or 404 |
| `POST /v1/playlists/create` | `{ "login", "content" }` | The new playlist's ID, as a JSON string |
| `POST /v1/playlists/update` | `{ "login", "playlistId", "content" }` | Any 2xx |
| `POST /v1/playlists/delete` | `{ "login", "playlistId" }` | Any 2xx |
| `POST /v1/playlists/cover` | `{ "login", "playlistId" }` | The cover image, with its `Content-Type`, or 404 |
| `POST /v1/playlists/set-cover` | `{ "login", "playlistId", "cover": { "data", "contentType" } }`, no cover to remove it | Any 2xx. A server without playlist covers can ignore it. |

The connector's other optional features (`full-scan`, `playlists`, `favourites`, `library-read`) aren't used by
Melodex today, so they aren't part of this contract yet.
