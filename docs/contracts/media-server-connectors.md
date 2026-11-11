# Media-server connectors (contract v1.3)

Connectors tell a media server about new music and confirm it's there. They are the last step of the import
pipeline: once beets has filed a track, the connector triggers a scan, the core polls until the server has the
track, and the server's track ID is recorded so apps can switch to it. Code: `Melodex.Contracts.Plugins.Connectors`.

## Required: `IMediaServerConnector`

| Method | Purpose | Notes |
| --- | --- | --- |
| `TriggerScanAsync(paths)` | Ask the server to pick up changes | `paths` are library-relative. Servers that can't scan selectively may ignore them; declare `selective-scan` if you honour them. |
| `GetScanStatusAsync()` | Whether a scan is running, when the last one finished | Used to wait for a scan before resolving. |
| `ResolveTrackAsync(lookup)` | The server's track ID for a file or catalogue ID, or `null` | `null` means "not there yet"; the core keeps polling with backoff until a timeout. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button: check the URL and credentials. |

`TrackLookup` carries everything the core knows about the file. Try the most specific fields first: the path, then
the MusicBrainz recording ID or ISRC, then title, artist, album and duration.

## Optional capabilities

| Capability | Interface | Purpose |
| --- | --- | --- |
| `selective-scan` | — | `TriggerScanAsync` only scans the given paths |
| `full-scan` | `IFullScan` | Rescan everything, including unchanged files |
| `playlists` | `IPlaylistManager` | List, create and append to playlists |
| `favourites` | `IFavouritesManager` | Star and unstar tracks |
| `library-read` | `ILibraryReader` | Stream every track the server knows, to cross-check with the library scan |
| `user-playlists` | `IPlaylistSync` | Each user's own playlists, kept the same in Melodex and on the server (1.1) |
| `server-identity` | `IServerIdentity` | Which server it is and its version, so the core can tell whether upgrades keep what's in it (1.3) |

The core and the API only offer actions the active connector declares. A listen-now track can't be starred or added
to a playlist until it's in the library and has a server track ID, whatever the connector supports.

## Each user's playlists: `IPlaylistSync` (1.1)

`IPlaylistManager` acts as the connector's own login. `IPlaylistSync` acts as the user whose `ServerLogin` it's given,
so each Melodex user syncs only their own playlists. The core keeps each login encrypted and hands it over per call.

| Member | Purpose | Notes |
| --- | --- | --- |
| `SyncsPlaylists` | Whether to sync at all | The connector's own setting: Navidrome's is the **Sync playlists** box. The core syncs nothing while it's false. |
| `CheckLoginAsync(login)` | Whether a login works | Called before a login is linked. |
| `GetPlaylistsAsync(login)` | The user's own playlists, without songs | Leave out other users' public playlists. `Changed` is how the core tells a playlist changed on the server. |
| `GetPlaylistAsync(login, id)` | A playlist and its songs, in order | `null` if it's gone. |
| `CreatePlaylistAsync(login, content)` | Make a playlist; returns its ID | `content` has the name, comment and server track IDs in order. |
| `UpdatePlaylistAsync(login, id, content)` | Replace a playlist's name, comment and songs | The whole list, in order, not a change to it. |
| `DeletePlaylistAsync(login, id)` | Delete a playlist | One already gone isn't an error. |
| `SyncsPlaylistCovers` | Whether playlists' own pictures sync too (1.2) | Defaults to false, so 1.1 connectors load unchanged. |
| `GetPlaylistCoverAsync(login, id)` | The picture someone gave a playlist (1.2) | `null` when it has none of its own: a picture the server makes from its songs doesn't count. |
| `SetPlaylistCoverAsync(login, id, cover)` | Give a playlist a picture, or with `null` take it away (1.2) | |

`UserPlaylist.HasCover` (1.2) may say whether a playlist has a picture of its own without fetching it; `false` spares
the core asking.

The core decides what to sync. Changed on one side since the last sync, a playlist is copied to the other; changed on
both, the later change wins; deleted on one side and unchanged on the other, it's deleted there too. Songs are sent as
the server's track IDs, found with `ResolveTrackAsync`. Songs that come from the server and aren't in the library
are kept, with their server IDs, so they go back unchanged. A playlist's own picture follows the same rule, judged
by the playlist's `Changed` time and the picture's content, so a server that stores a different copy of what it was
sent doesn't send it back. Folders are part of names, split by a delimiter each user
chooses, as servers have no folders.

## Reference connector: Navidrome

Through the Subsonic API: `startScan` and `getScanStatus` for scanning, `search3` to resolve tracks, plus
playlists and stars. Users' playlists use `getPlaylists` (keeping those the user owns), `getPlaylist`, and
`createPlaylist` with a `playlistId` to replace a playlist's songs, as `updatePlaylist` only adds and removes them by
position. Navidrome scans the whole library folder, so it doesn't declare `selective-scan`.

Playlists' own pictures go through Navidrome's own API, signed in as the user: `uploadedImage` on
`GET /api/playlist/{id}` says whether one was uploaded, `POST /api/playlist/{id}/image` uploads one as the `image`
part of a form, and `DELETE` on the same path removes it. The picture itself comes from Subsonic's `getCoverArt` with
the ID `pl-{id}`. Seen on Navidrome 0.64.2 in the sandbox.

Navidrome's Subsonic responses don't report the real file path by default. `path` is synthesised from the tags,
for example `Portishead/Dummy/01-01 - Mysterons.flac` for a file at `Portishead/Dummy (1994)/01 Mysterons.flac`.
So the connector resolves tracks by MusicBrainz recording ID first (`musicBrainzId` on songs), then ISRC (`isrc`),
then title, artist, album and duration. It falls back to the path only when Navidrome is set to report real paths.
Seen on Navidrome 0.64.2 in the sandbox; see [docs/testing.md](../testing.md).

Navidrome can't match files that are retagged and moved in the same scan, so the adopt step for existing libraries
retags in place first, scans, then moves and scans again. The connector is what makes that two-pass flow possible.

Plex and Jellyfin connectors come after v1; nothing in the contract is Subsonic-specific.
