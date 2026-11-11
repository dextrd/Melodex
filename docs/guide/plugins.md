# Plugins

Melodex is pluggable in five ways. Manage them under **Settings → Plugins**, where **Add** offers what's available and
**Test** checks each one.

| Kind | Does | Comes with Melodex |
| --- | --- | --- |
| **Catalogue** | Where artists, albums and songs come from | MusicBrainz (its public server, or your own mirror), Deezer |
| **Media server** | Told to scan when music arrives; also lends its covers, and can sync playlists | Navidrome |
| **Retrieval** | Fetches music | YouTube Music (yt-dlp) and the inbox folder; Internet Archive, Prowlarr, Qobuz, Tidal and Bandcamp when you turn them on |
| **Artwork** | Covers and artist pictures | Cover Art Archive, Wikimedia Commons, Deezer, fanart.tv (with your own key) |
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
