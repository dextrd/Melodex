# Testing without a real library

Melodex is developed against a generated test library and a throwaway Navidrome, so nothing touches a real
music collection until you choose to point it at one.

## The test library

`tools/testlib` builds a small library from real MusicBrainz data, with a known answer for what's owned and what's
missing.

```bash
python3 tools/testlib/generate.py
```

This writes `tools/testlib/out/music/` (85 silent files, about 10 MB) and `tools/testlib/out/expected.json`, the
answer key. It needs ffmpeg and the Python `mutagen` package, and runs offline in about ten seconds.

| File | What it is |
| --- | --- |
| `scenario.json` | What to build: which artists, which editions are owned, how each album is tagged, and the stray files |
| `catalogue.json` | A committed snapshot of the MusicBrainz data the library is built from |
| `fetch_catalogue.py` | Refreshes the snapshot (about 70 requests, at MusicBrainz's one request a second) |
| `generate.py` | Builds the files and the answer key from the two JSON files |

The scenarios, two artists with deep and messy discographies (Portishead: 90 release groups; Daft Punk: 217):

| Album | Format | Tags | Exercises |
| --- | --- | --- | --- |
| Dummy | FLAC | Full MusicBrainz | The easy path |
| Portishead | MP3 | Full MusicBrainz | Owned album with 2 tracks missing |
| Roseland NYC Live | M4A | Basic only | Fuzzy matching; a live album that's owned although live albums are hidden from the missing view |
| Discovery | MP3 | Basic + ISRC | ISRC matching without MusicBrainz IDs |
| Homework | FLAC | Basic only | Fuzzy matching through a messy folder name, shouting, a version suffix and a changed artist credit |
| Random Access Memories (10th anniversary) | M4A | Full MusicBrainz | Two-disc deluxe edition owning the album, with one disc-2 bonus track missing |
| Three strays | MP3 | Various | A wrong-length "Glory Box", a track not in the catalogue, and an untagged file: all go to review |

Tags are written the way beets and Picard write them for each format: Vorbis comments, ID3v2.4 with a UFID for the
recording ID, and MP4 freeform atoms.

### The answer key

`expected.json` says, for each file, the match method Melodex should use (named as in `MatchMethod`) and the
recording, edition and album it belongs to; stray files say `Unmatched`. For each artist, it lists the owned albums
(complete or with their missing tracks), the missing albums the default filters show, and how many missing albums
the filters hide.

Each missing album also says whether it's `coveredWhenFetchedOnce`: every track of one of its official editions is
owned on other albums. With the default `duplicateTracks` setting (fetch a song once, keep it in the album) those
albums are covered, not missing; with `keepBoth` they're missing.

The default filters are in `scenario.json` under `policy`. The plan names live albums, compilations, remixes, demos
and soundtracks as hidden. Real data also has interviews, mixtapes and DJ mixes, so the policy hides any release
group with a secondary type. It also hides groups with only bootleg releases, such as Daft Punk's *Leftovers*.

### The gate test

`LibraryGateTests` scans the test library through the real core and checks every file's match and every album's
status against the answer key, in both duplicate modes. It replays recorded MusicBrainz responses from
`tests/fixtures/musicbrainz`, so it runs offline in seconds. After changing what the MusicBrainz plugin or the matcher
asks for, record them again (about ten minutes, at one request a second) and commit the fixtures:

```bash
MELODEX_RECORD=1 dotnet test --filter LibraryGateTests
```

It's skipped if the library hasn't been generated. CI generates it first.

## The sandbox

`sandbox/sandbox.sh` runs the whole stack over a copy of the test library, with podman or docker: Navidrome 0.64.2,
the Melodex core, the beets worker, and both retrieval plugins.

```bash
sandbox/sandbox.sh up
```

This generates the library if it isn't there, copies it to `sandbox/music` (the beets worker writes into it, so the
generated library stays as the answer key expects), builds any image that's missing, starts everything on a private
network, creates a Navidrome admin, adds Navidrome and both plugins to Melodex, and scans.

- Navidrome: http://localhost:4533. The user is `admin`; the password is generated into `sandbox/.env` on first run.
- Melodex: http://localhost:8787. Press **Scan**, then use the Get buttons.
- The inbox plugin reads `sandbox/inbox`. Drop files there, then press Retry on the request.

The beets worker and the plugins run as you, so the files they write in `sandbox/music` and take from `sandbox/inbox`
stay yours. `build` rebuilds the images after a change, `scan` rescans Navidrome, `status` reports, `down` removes the
containers and their data, and `reset` also throws away the library copy and the inbox.

## Moving on to a real library

Scanning only reads the library, so you can check the missing view against your real files before fetching anything:
the core mounts the music folder read-only, and only the beets worker writes it, when a request lands. Files without
MusicBrainz IDs will match less reliably until they're adopted with beets. To try the adopt step without risk, run it on a copy.
