# Import worker (beets)

The import worker is the only component that writes the music library. It runs [beets](https://beets.io) in its
own container, in `beets-worker/`. The core calls it after a fetched file passes verification.

Unlike retrieval plugins it's part of Melodex, not a plugin: there is one, and its API is internal. It's written
down here so it can be swapped or debugged.

## Why beets doesn't guess

By the time a file reaches the worker, Melodex knows which MusicBrainz release it belongs to and which recording
it is. The worker pins beets to that release (`search_ids`) and takes it whatever beets' match score says. It then
checks every file landed on the recording Melodex named, and refuses the import if not. So beets never files a
song under the wrong album because the tags looked close enough.

An edition MusicBrainz doesn't have, because only a later catalogue such as Deezer lists it, has no release to pin
to. Its files are filed as tagged instead: the worker writes Melodex's tags (title, artists, album, disc and track
numbers, track total, date and ISRC), beets only works out the path from the template, and nothing is looked up.

Melodex is the library's database, so beets' own library only exists for one import at a time. Nothing beets
remembers can disagree with Melodex. One side effect: `%aunique{}` never adds anything, because beets sees no other
albums to tell apart.

## `POST /v1/import`

```json
{
  "stagingDirectory": "0199f0c2-…",
  "release": { "musicBrainzId": "76df3287-…", "title": "Dummy", "artist": "Portishead", "date": "1994-08-22", "trackCount": 11 },
  "files": [
    { "path": "track.opus", "recordingId": "145f5c43-…", "trackId": null, "title": "Glory Box", "artist": "Portishead", "disc": 1, "position": 11, "isrc": "GBAQT9400066" }
  ],
  "destinationDirectory": null,
  "pathTemplate": "$albumartist/$album%aunique{}/$track $title",
  "musicBrainzServer": "https://musicbrainz.org"
}
```

| Field | Meaning |
| --- | --- |
| `stagingDirectory` | The job's folder, relative to the staging volume (`/staging`). |
| `release` | The edition the files belong to. Its MusicBrainz ID pins the match; without one, the files are filed as tagged. |
| `files` | Each file, relative to the staging folder, with the recording it is (empty without a release), its place on the release, and its ISRC if known. |
| `destinationDirectory` | A folder relative to the library to put the files in, instead of the template's. Melodex sets it when you already own part of the album, so new tracks join the files you have wherever they live. Only the file name comes from the template. |
| `pathTemplate` | A [beets path format](https://beets.readthedocs.io/en/stable/reference/pathformat.html), set under Settings → Fetching. |
| `musicBrainzServer` | The server Melodex's MusicBrainz plugin uses, so a mirror applies to imports too. |

Steps:

1. Copy the files into a scratch folder and tag them with what Melodex sent (title, artist, album, disc and
   track number, MusicBrainz recording, track and release IDs), so beets maps each one onto the right track.
2. Run a beets import pinned to the release, writing tags in place.
3. Check each file matched its recording.
4. Move each file to `library/<template path>`, or `library/<destinationDirectory>/<file name>`. If a file is
   already there, add `.1`, `.2` and so on before the extension, as beets does.
5. Delete the staged originals.

Response `200`:

```json
{
  "files": [
    {
      "source": "track.opus",
      "path": "Portishead/Dummy/11 Glory Box.opus",
      "durationSeconds": 305.573,
      "format": "Opus",
      "tags": { "title": "Glory Box", "mb_trackid": "145f5c43-…", "mb_albumid": "76df3287-…", "isrc": "GBAQT9400066" }
    }
  ]
}
```

`tags` holds the beets fields that have values: titles, artists, track and disc numbers, dates, label, catalogue
number, ISRC, country, media, album type and the MusicBrainz IDs.

Errors are `{ "error": "message", "code": "..." }`: `422 import-failed` when beets can't import the files (for
example MusicBrainz didn't return the release, or a file didn't match its recording), and `400` for a malformed
request. Melodex shows the message on the Activity page and marks the request failed, without trying other
plugins, because another copy of the song would fail the same way.

## `POST /v1/delete`

Deletes files from the library, for removing an artist, album or track from the Library page.

```json
{ "paths": ["Portishead/Dummy/01 Mysterons.opus", "Portishead/Dummy/02 Sour Times.opus"] }
```

Each path is relative to the library. After the files go, so does any folder left holding nothing but artwork,
playlists, rip logs or notes (`.jpg`, `.png`, `.cue`, `.log`, `.m3u`, `.nfo`, `.txt` and the like), then its parent
the same way, up to the library itself. A path outside the library is refused.

Response `200`, listing a file that was already gone as deleted:

```json
{ "deleted": ["Portishead/Dummy/01 Mysterons.opus"], "failed": [{ "path": "Portishead/Dummy/02 Sour Times.opus", "error": "Permission denied" }] }
```

## `POST /v1/format`

Rewrites files already in the library, for formatting an artist, album, track or the whole library. Melodex works
out every value from its catalogue; the worker only writes them.

```json
{
  "files": [
    { "path": "incoming/glory box.flac", "tags": { "title": "Glory Box", "album": "Dummy", "track": "11", "date": "1994-08-22" }, "artwork": "cover" }
  ],
  "artwork": { "cover": { "data": "<base64>" } },
  "pathTemplate": "$albumartist/$album%aunique{}/$track $title"
}
```

| Field | Meaning |
| --- | --- |
| `files[].tags` | Tags to write, by beets field name. The rest stay as they are, and an empty value clears one. Melodex writes `title`, `artist`, `album`, `albumartist`, `track`, `tracktotal`, `disc`, `disctotal`, `date` and `original_date` (as `yyyy`, `yyyy-MM` or `yyyy-MM-dd`), `country`, `media`, `albumtype`, `label`, `catalognum`, `isrc`, `lyrics` and the `mb_*` IDs; any other name fails that file. |
| `files[].artwork` | The key of an image in `artwork` to embed as the front cover, replacing what's there. Omit it to leave the embedded art. |
| `files[].removeArtwork` | Take the embedded art out. |
| `files[].lrc` | Lyrics to write to an `.lrc` file beside the file, with its name. Omit it to leave any there. |
| `artwork` | Images by key, base64, so an album's files share one copy. |
| `pathTemplate` | Move each file to where this path format puts it, going by its new tags, as an import would. Omit it to leave files where they are. |
| `folderArtwork` | The key of an image in `artwork` to write as each formatted file's folder image too, for an album's chosen cover. It replaces a `cover`, `folder` or `front` image already there, under that name, or else goes in as `cover.jpg` (`.png` for a PNG). |

A moved file never overwrites another: `.1`, `.2` and so on go before the extension. Its `.lrc`, if it has one, moves
with it under its new name. Once a folder holds no music,
its artwork and the like follow the music to its new folder, and the folder goes, then any parent left empty.

Response `200`, a result for each file in order. A file that couldn't be formatted, for example because it's gone
or outside the library, says why, and the rest carry on:

```json
{ "files": [{ "path": "incoming/glory box.flac", "newPath": "Portishead/Dummy/11 Glory Box.flac", "size": 72970 }, { "path": "gone.flac", "error": "It isn't in the music folder any more." }] }
```

## `POST /v1/reflinks`

Whether a file in staging can be reflinked into the library, tried with a small file made for the purpose and
removed again: `{ "works": true }`, or `{ "works": false, "reason": "they're on different filesystems" }`. It
backs the Check reflinks button of plugins that copy into staging as reflinks.

## Reflinks

A reflink is a copy that shares its data on disk with the original until either is changed. The worker copies
staged files into its working folder, tags them there and moves them into the library, all as reflinks where the
filesystem can make them, and as ordinary copies where it can't. Its working folder is `.import` in the staging
volume for this.

So when a plugin also reflinks into staging, as the Prowlarr plugin does from qBittorrent's downloads, a song you
keep seeding takes next to no extra space in the library: only the blocks the tags changed. Tagging only changes
the library's copy. A file whose new tags don't fit the space its format leaves for them is rewritten whole, and
then takes its full size, as an ordinary copy does.

Reflinks need btrfs, XFS made with reflink support, or ZFS 2.2 or later, and every folder involved on the same
filesystem: qBittorrent's downloads, the staging volume and the library. A staging volume Docker manages lives
wherever Docker keeps its volumes, so for reflinks, make it a folder on the same filesystem as the library.

## `GET /v1/health`

`{ "status": "healthy" | "unhealthy", "message": "..." }`. Unhealthy when the staging or library volume isn't
mounted, or the library isn't writable.

## Running

One import runs at a time, because beets' configuration is global. Mount the staging volume at `/staging` and the
library at `/music`, and run the container as the user that owns the library. Beets reads no config file, only its
defaults plus the settings above.
