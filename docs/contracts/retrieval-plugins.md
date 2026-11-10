# Retrieval plugins (contract v1.0)

Retrieval plugins answer **how music is fetched**. Each one runs in its own container behind a small HTTP API, so it
can be written in any language and updated on its own schedule. That matters because they wrap fast-changing
tools like yt-dlp. The core ships no retrieval plugins; they live in their own repositories.

The normative spec is [`retrieval-v1.openapi.yaml`](retrieval-v1.openapi.yaml). The matching .NET wire types are in
`Melodex.Contracts.Plugins.Retrieval`, with `RetrievalJson.Options` for serialization.

## Endpoints

All paths are under `/v1`. Bodies are JSON with camelCase names, lowercase enum values and nulls omitted.
Durations are in seconds (`durationSeconds`).

| Endpoint | Purpose |
| --- | --- |
| `GET /v1/manifest` | The plugin's manifest |
| `POST /v1/health` | Health check with the given settings; backs the Test button |
| `POST /v1/checks/{id}` | One of the checks the manifest's `checks` offer as buttons in the plugin's settings (optional) |
| `POST /v1/search` | Candidates for a target, best first |
| `POST /v1/fetch` | Start fetching a chosen candidate into staging; returns `202` at once |
| `GET /v1/fetch/{jobId}` | Status of a fetch |
| `DELETE /v1/fetch/{jobId}` | Cancel a fetch (only with the `cancel` capability) |

Errors use any 4xx or 5xx status with a body of `{ "error": "message", "code": "machine-code" }`. Use
`invalid-settings` when settings are wrong, and `unknown-job` for a job ID the plugin doesn't know.

## Stateless by design

- **Settings travel with every call.** The core stores settings and sends them in each request body, so a plugin
  container can restart, or run several instances with different settings, without any setup.
- **Candidates are self-describing.** `candidateId` is opaque to the core and passed back unchanged to
  `/v1/fetch`. Put whatever you need to fetch in it, such as a video ID or URL.
- **Jobs are the only state.** A plugin keeps in-flight fetches in memory. If it restarts mid-fetch, it answers
  `404 unknown-job` and the core retries or falls back.

## Flow

1. **Search.** The core sends a `RetrievalTarget`: kind (track or album), title, artist, album, expected duration,
   for albums the expected track listing, and every ID it knows (MusicBrainz, ISRC, UPC). The plugin returns
   candidates with its own `score` from 0 to 1, and what each will be: `format`, `bitrateKbps`, and for lossless
   `bitDepth` and `sampleRateHz` where known. A source with several qualities offers a candidate for each, with the
   quality in its `candidateId`, so the core can choose.
2. **Choose.** The core searches every plugin at once and picks among all their candidates: those it's sure are the
   right song (a score of 0.8 or more) first, by the user's quality profile, then the plugin's score; then less sure
   ones by score. Qualities the profile turns off aren't fetched. Or the user picks one on the Activity page.
3. **Fetch.** The core creates `staging/<stagingDirectory>/` and calls `POST /v1/fetch`. The plugin writes only
   inside that directory, in the output format chosen in its settings.
4. **Follow.** The core polls `GET /v1/fetch/{jobId}` until the state is `completed`, `failed` or `cancelled`.
   A completed status lists every file written, relative to the staging directory, with codec, container,
   bitrate and duration; for albums, with disc and position where the plugin knows them.
5. **Verify and import.** These belong to the core, not the plugin. The core checks duration (and, when enabled, an
   AcoustID fingerprint) and reads the files' quality: one the profile turns off is refused, and one worse than its
   candidate said is noted. If verification fails, the next candidate, from whichever plugin, is tried. When they run
   out, the job goes to the review list.

## Capabilities and output

| Capability | Meaning |
| --- | --- |
| `fetch-track` | Can fetch single tracks |
| `fetch-album` | Can fetch whole albums in one job. Without it, the core fetches albums track by track. |
| `cancel` | Supports `DELETE /v1/fetch/{jobId}` |

The manifest's `retrieval` block lists the output formats and bitrate range the plugin can produce. The user picks
within it in the plugin's settings. The core never transcodes.

## Checks

A plugin can offer checks that the health check doesn't cover, listed in the manifest's `checks` as an `id`, a
`title` for the button and a `description`. The settings dialog shows a button for each; pressing it calls
`POST /v1/checks/{id}` with the settings in the form, saved or not, and shows the `{ "passed": ..., "message": ... }`
that comes back.

One ID means something to the core: `reflinks`, for a plugin that copies files it has elsewhere into staging and
can make those copies [reflinks](../import-worker.md#reflinks). The plugin says whether that works from its side,
and Melodex adds whether the import worker can reflink from staging into the library.

## Packaging

- One container per plugin, for example `retriever-ytdlp`, listening on port `8080`.
- Mount the staging volume at `/staging`. `stagingDirectory` paths are relative to it.
- Document the hosts the plugin contacts in the manifest's `access` block and in the README.
- Users are responsible for the plugins they run and for complying with each source's terms.

## Reference plugins

yt-dlp first, plus a second one to prove the contract, such as a watched inbox folder or slskd.
