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

## The inbox

The inbox plugin fetches nothing itself: it offers whatever you drop into its folder (`INBOX_PATH`). Drop in the files
for a request, then press **Retry** on it.

## Your responsibility

Melodex ships no media sources. You're responsible for the plugins you run, and for complying with each source's
terms.

Next: [Configuration](configuration.md).
