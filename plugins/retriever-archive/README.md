# Internet Archive retrieval plugin

A Melodex retrieval plugin (contract v1.0) that finds music in the [Internet Archive](https://archive.org)'s
collections and downloads it. By default it searches the [Live Music Archive](https://archive.org/details/etree)
(concert recordings shared with the bands' permission) and [netlabels](https://archive.org/details/netlabels)
(releases under Creative Commons licences).

It lives in the Melodex repository for now and will move to its own repository later.

## What it does

- **Search.** For a track: items in the collections by that artist that mention the title, scored on each audio
  file's title, artist and length. For an album: when the album's title has a date in it, as live recordings' titles
  usually do, the artist's concerts on that date; otherwise items by the artist with the album's words. An item is
  offered as the album only when it has a track for every requested one. Stream-only items aren't offered.
- **Fetch.** Downloads the chosen files into the staging directory the core gives it, under their names in the
  Archive, reporting progress as it goes.
- **Cancel.** Stops a download between chunks.

Each original file in an item comes with copies the Archive makes from it (VBR MP3 and Ogg Vorbis); the `format`
setting picks which one is fetched.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `format` | `flac` | `flac` fetches only lossless files, so items without any aren't offered. `best` fetches FLAC where there is one and MP3 otherwise. `mp3` and `ogg` fetch the Archive's copies. |
| `collections` | `etree, netlabels` | The collections to search, separated by commas. |
| `minScore` | `0.6` | Items that match less well than this aren't offered. |

## Running it

```yaml
retriever-archive:
  build: plugins/retriever-archive
  volumes:
    - staging:/staging
```

Then add `http://retriever-archive:8080` under Settings → Plugins in Melodex (it's one of the templates under Add).

It contacts `archive.org` and the `*.us.archive.org` servers downloads are redirected to. Environment variables:
`STAGING_ROOT` (default `/staging`) and `PARALLEL_FETCHES` (default `2`).

## Tests

```bash
pip install -r requirements.txt -r requirements-dev.txt
pytest
```

The tests answer for archive.org with a fake, so they don't need the network. The download test needs ffmpeg.
