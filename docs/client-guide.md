# Melodex client guide

How an app works with Melodex: finding what's in a library and what's missing, asking for music, listening to a
track before it's in the library, and hearing about it when things happen. Everything here is version 1 of the API,
at `/api/v1` on the Melodex server (port 8787 by default).

The [interactive API reference](#the-api-reference), served by Melodex itself, lists every call with its fields. This
guide covers what the reference can't: how the pieces fit together.

## Contents

- [Getting a key](#getting-a-key)
- [Scopes](#scopes)
- [IDs](#ids)
- [Finding music](#finding-music)
- [Requests](#requests)
- [Listen now](#listen-now)
- [Spectrums](#spectrums)
- [Events](#events)
- [Webhooks](#webhooks)
- [Errors](#errors)
- [Versioning](#versioning)
- [The Subsonic API](#the-subsonic-api)
- [Stars, ratings and the play queue](#stars-ratings-and-the-play-queue)
- [Listening history](#listening-history)
- [Similar music and recommendations](#similar-music-and-recommendations)
- [Radio](#radio)
- [The API reference](#the-api-reference)

## Getting a key

Apps authenticate with an API key. Someone with the API keys permission makes one under **Settings → API**,
choosing what it may do, and gives it to your app. A key belongs to whoever made it and can never do more than they
may: if their permissions shrink, or they're removed, so does the key. Send it on every call in the `X-Api-Key` header:

```bash
curl -H "X-Api-Key: mdx_..." http://melodex.local:8787/api/v1/library
```

Where a header can't be set, such as the `src` of an audio element, pass it as the `apikey` query parameter instead.
Prefer the header: query strings end up in logs.

Keys start with `mdx_`. Melodex keeps only a hash, so a lost key can't be shown again: make a new one and revoke the
old. A revoked key is refused at once. Ask for one key per app, so each can be revoked alone. Requests your app makes
are credited to the key's name on the Melodex Activity page and in events.

The web app itself signs in with a username and password and a session cookie (`/auth/*`). Apps don't use those.
Everyone signs in, even on the local network.

A few calls need no key at all: `GET /health`, `GET /version`, `GET /auth/status`, artwork (`GET /artwork/...`),
signed stream URLs, and this reference.

## Scopes

Each key has one or more scopes, the same permissions users are given:

| Scope | Allows |
| --- | --- |
| `read` | Reading the library, the catalogue, requests, listen-now tracks and tasks, and following `GET /events` |
| `request` | Making, cancelling, retrying and choosing for requests; ignoring and un-ignoring albums and tracks |
| `listen` | Starting, adding and discarding listen-now tracks, and playing tracks and files |
| `review` | The review lists: approving, matching, excluding, deferring and hiding files |
| `format` | Planning and running formatting, which rewrites files' tags and covers and moves them |
| `remove` | Removing music from the library |
| `scan` | Scanning the library, and pausing, cancelling and moving tasks |
| `settings` | Reading and changing settings and plugins, and reading the log |
| `keys` | Making and revoking API keys, and managing webhooks |
| `users` | Adding, changing and removing users |
| `admin` | Everything its owner may do |

Unless the reference says otherwise, a `GET` needs `read` and anything that changes something needs `admin`, which only
the admin's keys have in full. Each
operation in the reference ends with the scope it needs. Most apps want `read` plus `request` or `listen`, or both.

Calling something your key can't do gives `403`, with the scope it needed:

```json
{ "status": 403, "code": "scope-required", "scope": "request", "title": "Not allowed", "detail": "..." }
```

## IDs

Melodex IDs are GUIDs, and stable: an artist, album, edition or track keeps its ID for the life of the server.
They aren't MusicBrainz IDs, which can merge or move. Each object carries its outside IDs in `ids`, as
`{ "provider": "musicbrainz", "value": "..." }`, to match against your own data.

The catalogue models music in four levels:

- **Artist**
- **Album**: the release group, such as *Dummy*. An album's `status` is `complete`, `partial`, `covered` (you have
  every song, on other albums) or `missing`.
- **Edition**: a specific release of it, such as the 1994 CD or a deluxe reissue, each with its own track list.
- **Track**: a song, which can be on many editions. Requests and listen-now can name a track alone; Melodex picks the
  edition, or you can pass `editionId`.

## Finding music

| Call | What |
| --- | --- |
| `GET /catalogue/search?q=...` | Artists, albums and tracks, each saying whether you have it. Add `kinds=tracks` (or `artists`, `albums`, comma-separated) to search less, which is quicker; `local=true` searches only what Melodex already holds, and answers at once |
| `GET /artists/{id}` | An artist's whole discography, each album with its status |
| `GET /albums/{id}` | An album's editions, and the tracks of one edition with their status |
| `GET /library/artists`, `/library/albums`, `/library/tracks` | What's in the library |
| `GET /library/missing` | Missing albums across the library, after the owner's filters |

The catalogue comes from MusicBrainz, which allows about one request a second. A search that isn't `local` can take a
few seconds, or answer `503` with `Retry-After` when MusicBrainz is busy (see [Errors](#errors)). Searching `local`
first and then in full gives quick results that fill in.

Pictures are at `GET /artwork/{artist|album|edition}/{id}`, also given as `imageUrl` and `coverUrl` on results. They
answer `503` with `Retry-After` while being fetched for the first time, and `404` when there's no picture.

## Requests

A request asks Melodex to fetch an artist, an album or a track into the library.

```http
POST /api/v1/requests
X-Api-Key: mdx_...
Content-Type: application/json

{ "kind": "album", "id": "0190f1c4-...", "editionId": null }
```

- `202 Accepted` with the new request, or `200 OK` with the existing one if the same thing is already on its way.
  Asking twice is safe: it's how you retry a lost response.
- `404` if there's no such artist, album or track; `409` while a library scan is reading the library.

A request's `state` moves `queued` → `searching` → `fetching` → `verifying` → `importing` → `notifying` → `inLibrary`.
It stops at `inLibrary`, `failed`, `needsReview` (every source was tried and nothing passed the checks; the person
running Melodex can choose a candidate) or `cancelled`. Artist and album requests are `inProgress` while their parts
run, and `parts` counts where those stand; `GET /requests/{id}` lists them in `children`.

| Call | What |
| --- | --- |
| `GET /requests?active=true` | Requests still running, newest first |
| `GET /requests/{id}` | One request, with its parts and fetch attempts |
| `DELETE /requests/{id}` | Cancel it and its unfinished parts |
| `POST /requests/{id}/retry` | Try a failed or reviewed request again |

Rather than polling, follow [events](#events): `request.completed`, `request.failed` and `request.needs_review` say
when a request stops.

## Listen now

Listen now fetches a track to play straight away, without adding it to the library. If the listener likes it, it can
be added; if not, it's deleted after a while.

```http
POST /api/v1/listen
{ "kind": "track", "id": "0190f1c4-..." }
```

This returns a list of listen-now items: one for a track, or one per missing track, in order, for
`"kind": "album"`. A track you already have is refused with `409`: play it with `GET /tracks/{id}/play`, which gives a
signed `streamUrl` for its file.

Each item's `state`:

| State | Meaning |
| --- | --- |
| `requested`, `fetching` | On its way |
| `playable` | Ready: `streamUrl` plays it, with seeking. `mediaType`, such as `audio/ogg; codecs=opus`, says what to check the player supports |
| `importing` | Being added to the library; still playable |
| `inLibrary` | In the library. `mediaServerTrackId` is its ID in the media server, to play it from there from now on |
| `failed` | Nothing found passed the checks |
| `expired` | Discarded, or not played in time; the file is gone |

A playable track is kept for the time set in Melodex's settings (an hour by default) after it was last played, so
`expiresAt` moves later each time it's played. `POST /listen/{id}/add` adds it to the library without fetching it
again; `DELETE /listen/{id}` discards it now.

**Stream URLs** are relative to the server, like `api/v1/listen/{id}/stream?exp=...&sig=...`. They need no key,
support range requests, and stop working after 12 hours; get a fresh one from `GET /listen/{id}`. They're for
handing to an audio element, not for keeping.

The `listen.ready` event says when an item becomes playable, with its stream URL.

## Spectrums

Each song has a spectrum, which Melodex's own visualiser draws its bars from and any app can use for its own: how loud
each band of pitch is, 30 times a second, with how loud the whole moment is, its peak, the outline of its waveform and
where the beats fall. Melodex works it out once from the song, so an app needn't decode the music a second time.

A song's spectrum is at its stream URL with `spectrum` in place of `stream`, signed the same way:

```http
GET /api/v1/library/files/{id}/spectrum?exp=...&sig=...
GET /api/v1/listen/{id}/spectrum?exp=...&sig=...
```

- `bands=8`, `16` or `32` gives fewer bands than 64, each the loudest of those it covers.
- `warm=true` has it made without sending it, ahead of the song being played (`202`).
- `404` while the visualiser is off in Melodex's settings, `503` if it can't be made, such as when the song won't
  decode.

It's made on demand, in a second or so, unless Melodex keeps one beside each song (see the user guide), and sent
gzipped, which a browser or HTTP client unpacks for you.

### The format

A header, then one frame after another from the start of the song. Numbers of more than a byte are little-endian.

| Bytes | Header |
| --- | --- |
| 0–3 | `MDXB` |
| 4 | The version: 2 |
| 5 | Frames a second: 30 |
| 6 | Bands a frame: 64, or as asked |
| 7 | The header's length: 16 |
| 8–9 | The lowest pitch, in hertz: 40 |
| 10–11 | The highest pitch, in hertz: 16000 |
| 12 | The quietest level, in decibels of full scale, as a signed byte: -96 |
| 13 | The loudest: 0 |
| 14–15 | A frame's length: the bands and 5 |

| Bytes | Each frame |
| --- | --- |
| One for each band | How loud it is, 0 to 255 between the quietest and loudest decibels, bass first. Bands are spaced by pitch, as we hear it, each the loudest frequency in it |
| Next | Loudness: the root mean square of the samples since the last frame, on the same scale |
| Next | Peak: the loudest sample since the last frame, on the same scale |
| Next two | The waveform's lowest and highest sample since the last frame, as signed bytes, -127 to 127 being full scale |
| Last | A beat: 0 for none, otherwise how strong, 1 to 255 |

A frame's moment is its number over the frames a second; its bands are of the 4096 samples (at 44.1 kHz) leading up
to it, so the first few rise as the song begins. The levels aren't smoothed: a bar that moves gently, as Melodex's own
does, keeps part of its last height each frame. A level in decibels is the quietest plus the level times the range
over 255. Read the header's lengths rather than assuming them: a later version may add bytes to the header or to
each frame, which an app that skips them can still read.

`/bars`, in place of `/spectrum`, still gives version 1 of the format, for apps written for it: an 8-byte header
(`MDXB`, 1, 30, 64 and a spare byte), then 64 bars a frame from 40 Hz to 10 kHz, smoothed, without the rest.

## Events

Melodex announces what happens in two ways: a stream your app reads while it's running, and webhooks for when it
isn't.

### The event stream

`GET /events` is a [server-sent events](https://developer.mozilla.org/docs/Web/API/Server-sent_events) stream
(`read` scope). It sends two sorts of event:

- **Changes**, named `requests`, `listen`, `library` or `tasks`: something of that kind changed, so reload what you
  show of it. `marks` and `queue` say someone's stars and ratings, or play queue, changed: reload your own from
  `GET /marks` or `GET /player/queue`. These come at most four times a second per kind.
- **Happenings**, such as `request.completed`, with an `id` and data: the same events webhooks get, described below.

```text
event: requests
data: {"type":"requests","at":"2026-10-27T21:04:11Z"}

id: 0192d6b0-6d7e-7c35-9a1e-1f5c2b0a7d44
event: request.completed
data: {"id":"0192d6b0-...","type":"request.completed","occurredAt":"2026-10-27T21:04:11Z","data":{...}}
```

A comment line (`: keep-alive`) comes every 15 seconds when nothing else does. The stream doesn't replay what was
missed while disconnected, so reload what you show when you reconnect. A browser's `EventSource` can't send headers:
pass the key as `?apikey=`.

### Event types

Every event has the same envelope:

```json
{ "id": "0192d6b0-...", "type": "request.completed", "occurredAt": "2026-10-27T21:04:11Z", "data": { } }
```

| Type | When | `data` |
| --- | --- | --- |
| `request.completed` | A request is in the library | `{ request, requester }` |
| `request.failed` | A request failed | `{ request, requester }` |
| `request.needs_review` | Every source was tried and nothing passed the checks | `{ request, requester }` |
| `listen.ready` | A listen-now track became playable | `{ listen, requester }` |
| `scan.completed` | A library scan finished | `{ task }` |
| `scan.failed` | A library scan failed | `{ task }` |
| `play.recorded` | Someone played a song | `{ play, username }` |
| `ping` | Only from **Test** on a webhook | `{ message }` |

`request` is the same object `GET /requests/{id}` returns (without `children` and `jobs`), `listen` the same as
`GET /listen/{id}`, `task` the same as `GET /tasks/{id}`, and `play` the same as one of `GET /plays`. `requester` is the username of whoever asked, or
the name of the key that did. Only requests asked for directly are announced, not the parts of an artist or album request. More types may
be added; ignore ones you don't know.

## Webhooks

A webhook is an address Melodex posts events to, set up under **Settings → Webhooks** (or `POST /webhooks`, with
`admin`). Each chooses its events, or takes all of them.

Each delivery is a `POST` of one event's JSON, with these headers:

| Header | Value |
| --- | --- |
| `Melodex-Event` | The event type |
| `Melodex-Delivery` | The delivery's ID |
| `Melodex-Signature` | `t=<unix seconds>,v1=<hex HMAC-SHA256>` |

Answer with any `2xx` within 15 seconds. Anything else, or no answer, is tried again after 30 seconds, 2 minutes,
10 minutes, an hour and 6 hours, then given up. A retry sends the same body, so the event `id` is the same: use it to
ignore repeats. Deliveries can arrive out of order.

### Checking the signature

The signature is HMAC-SHA256, keyed with the webhook's secret (`whsec_...`), of the timestamp, a dot, and the body
exactly as received. Check it before trusting the body, and refuse old timestamps so a captured delivery can't be
replayed:

```python
import hashlib, hmac, time

def verify(secret: str, header: str, body: bytes, tolerance: int = 300) -> bool:
    parts = dict(p.split("=", 1) for p in header.split(","))
    timestamp = int(parts["t"])
    if abs(time.time() - timestamp) > tolerance:
        return False
    expected = hmac.new(secret.encode(), f"{timestamp}.".encode() + body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, parts["v1"])
```

```javascript
import { createHmac, timingSafeEqual } from "node:crypto";

function verify(secret, header, rawBody, tolerance = 300) {
  const parts = Object.fromEntries(header.split(",").map(p => p.split("=", 2)));
  const timestamp = Number(parts.t);
  if (Math.abs(Date.now() / 1000 - timestamp) > tolerance) return false;
  const expected = createHmac("sha256", secret).update(`${timestamp}.${rawBody}`).digest("hex");
  return expected.length === parts.v1.length && timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1));
}
```

```csharp
static bool Verify(string secret, string header, string body, int tolerance = 300)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var timestamp = long.Parse(parts["t"]);
    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > tolerance) return false;
    var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts["v1"]));
}
```

Use the raw body, before any JSON parsing: re-serialising changes the bytes. A new secret (**Make a new secret**, or
`POST /webhooks/{id}/secret`) applies to every delivery from then on, retries included.

## Errors

Errors are [problem details](https://www.rfc-editor.org/rfc/rfc9457) JSON, with a `detail` written to show a person:

```json
{ "status": 409, "title": "The library is being scanned", "detail": "..." }
```

Invalid bodies are `400` with `errors`, field by field:

```json
{ "status": 400, "title": "One or more validation errors occurred.", "errors": { "editionId": ["..."] } }
```

| Status | Meaning |
| --- | --- |
| `400` | Something in the body is wrong; see `errors` |
| `401` | No key, or a revoked one. `code` is `invalid-key` for a key Melodex doesn't know, `sign-in-required` for no key, or `setup-required` before Melodex has been set up |
| `403` | The key lacks a scope; see [Scopes](#scopes). A stream URL whose signature is wrong or out of date is also `403` |
| `404` | No such thing |
| `409` | Not now: for example, asking while a scan reads the library, or listening to a track you have |
| `503` | A catalogue source is busy. Wait for `Retry-After` seconds, then try again |

## Versioning

Everything under `/api/v1` keeps working the same way for the life of v1. Within it, Melodex may add calls, fields,
event types, enum values (such as a new request state) and query parameters, so ignore what you don't know and don't
reject unknown values. Anything that would break a client goes into `/api/v2`, with v1 kept alongside for a while.
`GET /version` gives the server's version and its `apiVersion`.

## The Subsonic API

Besides its own API, Melodex speaks the [Subsonic API](http://www.subsonic.org/pages/api.jsp), version 1.16.1, with
[OpenSubsonic](https://opensubsonic.netlify.app/)'s extensions, at `/rest`, so an app built for Subsonic servers
works with Melodex as it is. The user guide's [Subsonic players](guide/subsonic-players.md) page covers it from a
listener's side.

- **Signing in** is always with an API key that has the `listen` scope: OpenSubsonic's `apiKey` takes any such key;
  otherwise `u` is the key's owner and the password is a key made with `"subsonic": true` (**Let a Subsonic player
  sign in with it** in the web app), as `p` (plain, or `enc:` and hex) or as a token, `t` and `s`. Such a key is kept
  encrypted, as a token can only be checked against the key itself. The person's own password doesn't work here.
- **Answers** are XML, or JSON with `f=json`; errors are a 200 with Subsonic's error codes. Calls can be form posts
  (`formPost`), and `stream` takes `timeOffset` when it transcodes (`transcodeOffset`).
- **IDs** are Melodex's own: an artist's and album's are the same as in `/api/v1`, and a song's is its file's ID
  (`fileId` in `/api/v1`). A file moved or renamed outside Melodex gets a new ID on the next scan. Pictures are
  `al-`, `ar-`, `mf-` or `pl-` and the album's, artist's, song's or playlist's ID; `size` is ignored, and the picture
  comes whole.
- **The calls Melodex answers**: `ping`, `getLicense`, `getOpenSubsonicExtensions`, `getMusicFolders`, `getUser`,
  `getScanStatus`; `getIndexes`, `getMusicDirectory`, `getArtists`, `getArtist`, `getAlbum`, `getSong`,
  `getAlbumList`, `getAlbumList2`, `getRandomSongs`, `getGenres`, `getSongsByGenre`; `search2`, `search3`;
  `stream`, `download`, `getCoverArt`, `getLyrics`, `getLyricsBySongId`; `getPlaylists`, `getPlaylist`,
  `createPlaylist`, `updatePlaylist`, `deletePlaylist`; `star`, `unstar`, `setRating`, `getStarred`,
  `getStarred2`; `getPlayQueue`, `savePlayQueue`, with OpenSubsonic's `getPlayQueueByIndex` and
  `savePlayQueueByIndex` (`indexBasedQueue`); `scrobble` and `getNowPlaying`; and `getSimilarSongs` and
  `getSimilarSongs2`, a radio of songs you have from a song, album or artist. `getArtistInfo`, `getAlbumInfo` and
  `getTopSongs` (and their `2`s) answer empty for now. Anything else is error 70.
- **Stars, ratings and the queue** are the same as `/marks` and `/player/queue` below: a song's star is kept on its
  track, so it stays when the file's replaced. A queue saved by a player shows in the web player, and one saved by the
  web player shows in players, less songs not in the library.
- **Plays**: `scrobble` records a play in the same history as `POST /plays` (or with `submission=false`, what's playing
  now), and songs and albums carry `playCount` and `played`; `getAlbumList`'s `frequent` and `recent` are by them.

### Melodex's own extension

`getOpenSubsonicExtensions` lists `melodex`, version 1, for apps that know Melodex. Its answers carry a little more:

- Every song has `melodexTrackId`, its ID in `/api/v1` (where a file of your own goes by its file's ID), so search
  results, requests and Listen now from `/api/v1` can be matched to the songs a player lists.
- Every album has `melodexStatus`, `complete` or `partial`, and `melodexOwnedSongs` and `melodexTotalSongs`, to badge
  albums with songs missing.

The rest of what such an app wants, such as searching the whole catalogue with what you have marked, asking for music
and following it, and Listen now, is `/api/v1` itself: a player's key works there too, sent as `X-Api-Key`, with what
it may do there set by its scopes. Other players ignore all of this.

## Stars, ratings and the play queue

Each person's own, the same as in Subsonic players:

- `GET /marks` (`read` scope) lists your stars and ratings, latest first: `kind` (`song`, `album` or `artist`), `id`
  (a song's track ID), `starredAt` and `rating` (1 to 5).
- `PUT /marks/{kind}/{id}` with `{"starred": true}`, `{"rating": 4}`, or both, changes one; what's left out stays as it
  was, and `"rating": 0` takes a rating away. It answers with what's left, or 204 once there's neither.
- `GET /player/queue` (`listen` scope) is your queue: its songs, with what a player shows of each; `current`, the place
  of the one playing; `position` in seconds; and `changedBy`, `web` or a player's name. 204 when there's none.
- `PUT /player/queue` keeps a queue in place of the one before, at most 5000 songs; an empty one clears it. Say who you
  are with `client`.

## Listening history

Each person's own, from the web player and Subsonic players alike:

- `POST /plays` (`listen` scope) records a song you played, once it's played for half its length or four minutes,
  whichever comes first. A song in the library needs only `trackId` (and `fileId`, if you know which file); any other
  needs `title` and `artistCredit` too. `playedAt` is when it started, now if left out; `source` says what it was
  played from (`{"kind": "playlist", "id": ..., "name": ...}`); `client` names your app. The same song again within 30
  seconds is the same play, and the one already recorded comes back. Linked ListenBrainz and Last.fm accounts get it
  too.
- `PUT /plays/now` with the same body says what you've started: Subsonic players' now playing shows it, and linked
  accounts are told.
- `GET /plays` (`read` scope) lists your plays, latest first, with `skip` and `take` (at most 500), and the `total`.
- `GET /plays/top/songs`, `/albums` or `/artists` lists what you've played most, with `period` (`week`, `month`,
  `year` or `all`) and `take`.
- `DELETE /plays/{id}` takes one out.

Linking accounts is for a signed-in person, in the web app (`/account/scrobbling`); the admin sets the Last.fm API
key and secret with `PUT /settings/scrobbling`.

## Similar music and recommendations

From the discovery plugins, combined, with the `read` scope:

- `GET /discovery` says what the plugins that are on can find: `similarArtists`, `similarSongs`, `recommendations`.
- `GET /artists/{id}/similar` lists artists like a catalogue artist, closest first, with `take` (20 by default, at most
  100). Each has a `score` from 0 to 1 and the plugins it's `from`; those matched to the catalogue have its `id` and
  whether they're `inLibrary`, and the rest only their `name`.
- `GET /tracks/{id}/similar` lists songs like a song, the same way, but only those the catalogue has: each has its
  `trackId`, whether it's `owned`, and an album with its cover.
- `GET /discovery/recommendations` lists songs recommended for the key's user, best first (50 by default): from what
  the plugins know of them through their linked ListenBrainz and Last.fm accounts, and what they've played lately.

Answers are kept a while (a day, or six hours for recommendations), so asking again is cheap.

## Radio

Songs like a song, album, artist or playlist, without end, with the `listen` scope and a key that belongs to a user:

- `POST /radio` gives the next songs: send the `seedId` it was started from (a track ID, or an album, artist or one of
  your playlists), the `recent` songs it's queued so far as track IDs, oldest first, and how many to `take` (5 by
  default, at most 50). Nothing is kept on the server, so send them each time. The answer has its `name` and `kind`,
  and `songs` in the order to play them, shaped as similar songs are: those not `owned` are to fetch to listen to.
  Started from a song, the first batch starts with it. Songs you don't have make up the server's share,
  `radioMissingPercent` in `GET /settings/fetch` (0 to 100, 25 by default), counted over the `recent` songs as well,
  so a batch makes up for those before it. `ownedOnly` leaves them out, whatever the share. A seed that isn't there is a 404.
- `POST /radio/skips` with a `trackId` says a radio's song was skipped in its first half minute: radios leave it out,
  and play its artist less, for 90 days.
- `GET /radio/settings` and `PUT /radio/settings` are the user's: `autoplay`, whether a player carries on with a radio
  when its queue runs out.

## The API reference

Melodex serves an interactive reference at `/api/docs`, where you can try calls with a key, and the OpenAPI document
it's built from at `/openapi/v1.json`, to generate a client from. Both are open to anyone, as there's nothing private
in them.
