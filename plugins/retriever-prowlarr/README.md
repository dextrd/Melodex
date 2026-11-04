# Prowlarr retrieval plugin

A Melodex retrieval plugin (contract v1.0) that searches the torrent indexers you've set up in
[Prowlarr](https://prowlarr.com) and downloads with your [qBittorrent](https://www.qbittorrent.org). What you can find
is what your indexers offer; it's up to you to use ones whose music you're allowed to download.
[PandaCD](https://pandacd.io), a public tracker for Creative Commons and artist-permitted music, is a good one to start
with.

It lives in the Melodex repository for now and will move to its own repository later.

## What it does

- **Search.** Asks Prowlarr for the artist and album (or the date, for concerts), and for a song also the artist and
  title. The best-named torrents have their .torrent file read, and a torrent is offered only when it has a file for
  every requested track; for a song, the file that's that song. Torrents that only come as a magnet link can't be
  read ahead, so they rank lower and are checked once qBittorrent has their file list. Between equally good matches,
  FLAC comes first, then the better-seeded.
- **Fetch.** Adds the torrent to qBittorrent stopped, with the `melodex` category and tag, turns off every file that
  wasn't asked for (the rest of the album, covers, logs), starts it, and reports progress. When the files are done,
  it copies them to staging, leaving qBittorrent to seed. A torrent that was already in qBittorrent keeps its files;
  the requested ones are added to it.
- **Cancel.** Removes a torrent it added, with what it downloaded. One that was already there stays.

Melodex gives up on a fetch that makes no progress for 30 minutes, and cancels it.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `prowlarrUrl` | `http://prowlarr:9696` | Where this plugin reaches Prowlarr. |
| `prowlarrApiKey` | | In Prowlarr under Settings → General. Needed. |
| `qbittorrentUrl` | `http://qbittorrent:8080` | qBittorrent's Web UI. |
| `qbittorrentUsername`, `qbittorrentPassword` | | Leave empty when qBittorrent lets this plugin in without logging in. |
| `category` | `melodex` | The qBittorrent category torrents are added with. |
| `savePath` | `/downloads/melodex` | Where qBittorrent saves them, as qBittorrent sees it. |
| `downloadsFolder` | | The same folder as this plugin sees it, when it's mounted somewhere else. |
| `format` | `best` | `best` prefers FLAC and takes other formats otherwise; `flac` only lossless; `mp3` only MP3s. |
| `minSeeders` | `1` | Torrents with fewer seeders aren't offered. |
| `minScore` | `0.6` | Torrents that match less well than this aren't offered. |
| `afterwards` | `keep` | `keep` leaves the torrent seeding; `remove` takes it out, with its files, once Melodex has a copy. |

## Running it

The plugin has to read what qBittorrent downloads, so both mount the same downloads volume:

```yaml
retriever-prowlarr:
  build: plugins/retriever-prowlarr
  volumes:
    - staging:/staging
    - downloads:/downloads:ro

qbittorrent:
  image: lscr.io/linuxserver/qbittorrent
  volumes:
    - downloads:/downloads
```

Then add `http://retriever-prowlarr:8080` under Settings → Plugins in Melodex (it's one of the templates under Add),
and fill in Prowlarr's API key in its settings.

## Tests

```bash
pip install -r requirements.txt -r requirements-dev.txt
pytest
```

The tests answer for Prowlarr and qBittorrent with fakes, so they don't need either.
