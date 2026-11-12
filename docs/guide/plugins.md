# Plugins

Melodex is pluggable in five ways. Manage them under **Settings → Plugins**, where **Add** offers what's available and
**Test** checks each one.

| Kind | Does | Comes with Melodex |
| --- | --- | --- |
| **Catalogue** | Where artists, albums and songs come from | MusicBrainz (its public server, or your own mirror), Deezer |
| **Media server** | Told to scan when music arrives; can sync playlists, and Navidrome also lends its covers | Navidrome, Jellyfin, Plex |
| **Retrieval** | Fetches music | YouTube Music (yt-dlp) and the inbox folder; Internet Archive, Prowlarr, Qobuz, Tidal and Bandcamp when you turn them on |
| **Artwork** | Covers and artist pictures | Cover Art Archive, Deezer, Wikimedia Commons, fanart.tv (with your own key) |
| **Lyrics** | The words of songs, synced where they can be | LRCLIB |

## Without a catalogue

The catalogue can be removed, MusicBrainz included, and Melodex is then a plain music library: your files are listed
and played as their tags say, as [your own music](library.md#your-own-music) is. There's nothing missing, nothing to
fetch, and search finds only what you have. Add a catalogue whenever you like, and the next scan matches your files
against it. Files matched before it was removed stay as they are. Beets doesn't need MusicBrainz either: music fetched
through a catalogue that isn't MusicBrainz, such as Deezer, is filed as it's tagged.

Each retrieval plugin runs as a container of its own, from the same compose file: yt-dlp and the inbox always, the
others when named in `COMPOSE_PROFILES` (see [installing](installing.md#retrieval-plugins)). Add one under **Add**,
where each is a template with its address filled in, and fill in its settings.

## One media server at a time

Melodex uses one media server at a time. The first you add is turned on; any other is added turned off, ready to
switch to. Turning another on asks first, then turns the one in use off. Switching:

- finds your songs again on the new server, by their MusicBrainz IDs, files or tags;
- tells the new server about new music from then on;
- syncs playlists with it once each person links their login for it. Logins are kept for each server, so switching
  back uses the ones already linked, and a playlist that's on both is matched to its copy by name rather than made
  twice. Playlists already on the old server stay there.

Turning the only media server off asks first too: music is still filed, but no server is told about it and playlists
stop syncing. Removing a media server forgets everyone's login for it.

Retrieval plugins are all searched at once, and the [quality profile](requests-and-listening.md#quality) chooses
between what they find. Artwork and lyrics plugins are tried in order; move them up and down to change it. A retrieval
plugin is a separate service that speaks a small HTTP contract, so anyone can write one: see
[the plugin contracts](../contracts/README.md).

Qobuz and Tidal offer each quality they have up to their **Best quality** setting, and Prowlarr reads a release's name
("FLAC 24bit", "MP3 V0") for what its files don't say yet, so the profile can choose.

Lossless that comes with reason to think it's real is tried first, of candidates otherwise alike: Qobuz's, Tidal's and
Bandcamp's, straight from the store, and Prowlarr's with a rip's log and cue, or from a tracker you name under its
**Trackers known for lossless** setting (by their names in Prowlarr, separated by commas). Whatever it's said to be, a
lossless download is checked by its spectrum before it's kept; see
[lossless that seems lossy](requests-and-listening.md#lossless-that-seems-lossy).

## Jellyfin

Melodex can tell Jellyfin about new music instead of Navidrome. In Jellyfin's **Dashboard → API Keys**, make a key
for Melodex, then add **Jellyfin** under **Media server**, with:

- **Server URL**, where Melodex reaches Jellyfin, such as `http://jellyfin:8096`.
- **API key**, the one you made.
- **Music library** and **Music folder on the Jellyfin server**, as for [Plex](#plex), when they aren't the first
  music library and its first folder.
- **Sync playlists**, to keep each user's playlists the same in both, as with Navidrome: each user links their own
  Jellyfin login on **Your account** (see [playlists](playlists.md#syncing-with-your-media-server)).
- **Playlist folder separator**: what separates [folders](playlists.md#folders) in playlists' names, as Navidrome
  has too.

Jellyfin is told which folders new music went into, and finds each song by its MusicBrainz recording ID, its file or
its tags. A playlist's description goes both ways too. Jellyfin doesn't keep pictures for playlists as Navidrome
does, so those stay in Melodex.

## Plex

Melodex can tell Plex Media Server about new music instead of Navidrome. Add **Plex** under **Media server**, with:

- **Server URL**, where Melodex reaches Plex, such as `http://plex:32400`.
- **Plex token**, the `X-Plex-Token` of the server's owner or an admin. Plex's support article *Finding an
  authentication token* shows where to find it.
- **Music library**, the name of the Plex library that holds Melodex's music folder, if it isn't the first music
  library.
- **Music folder on the Plex server**, if Plex sees that folder somewhere other than the library's first folder.
  Melodex's paths are relative to its music folder, so this is how they become Plex's.

Plex is told to scan only the folders new music went into, and finds each song by its file. Some things are
Navidrome's alone for now:

- Plex's accounts are plex.tv's rather than logins to the server, so playlists don't sync with Plex.
- Covers from your own files come through Navidrome only.
- An upgraded song with a new file name may be a new song to Plex, without its plays and ratings (see
  [upgrades](requests-and-listening.md#upgrades)).

## The inbox

The inbox plugin fetches nothing itself: it offers whatever you drop into its folder (`INBOX_PATH`). Drop in the files
for a request, then press **Retry** on it.

## Your own plugins

Plugins don't have to come with Melodex. Anyone can write one, in any language, as a small web service that speaks the
contract for its kind (see [plugins at an address](../contracts/remote-plugins.md), and
[retrieval plugins](../contracts/retrieval-plugins.md) for sources). Run it where Melodex can reach it, such as in a
container in the same Compose project. Then, in its section, choose **Add**, then **Add your own plugin…**, and give
its address, for example `http://my-plugin:8080`. Melodex asks it what it is, refuses it if it's another kind or built
for a contract this version of Melodex doesn't have, and shows its settings. From then on it's used just like a plugin
that came with Melodex: in its place in the order, with **Test** to check it.

Only add plugins you trust. A plugin is sent its settings, keys and passwords included, and what it answers goes into
your library.

## Your responsibility

Melodex ships no media sources. You're responsible for the plugins you run, and for complying with each source's
terms.

Next: [Configuration](configuration.md).
