"""Stand-ins for Qobuz's, Tidal's and Bandcamp's APIs, for the sandbox only, so their plugins can be tried without
accounts.

Albums and songs are looked up in MusicBrainz, so whatever Melodex asks for is found, with MusicBrainz's titles,
lengths, barcodes and ISRCs. The audio is a quiet tone of each track's length, made when it's first asked for:
16-bit/44.1 kHz FLAC as one file, and 24-bit/96 kHz FLAC as DASH segments, the way the services send them.

Sign-in: Qobuz takes any email and password, with the app ID and secret below. Tidal gives a link to a page here that
approves the code. Bandcamp takes the identity cookie below; its collection starts empty, and the page at /bandcamp
buys albums and songs into it.
"""

from __future__ import annotations

import base64
import hashlib
import html
import json
import os
import re
import secrets
import subprocess
import threading
import time
import zipfile
from pathlib import Path
from typing import Any
from urllib.parse import parse_qsl

import httpx
from fastapi import FastAPI, Request
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, Response

QOBUZ_APP_ID, QOBUZ_SECRET = "standin", "standin-secret"
TIDAL_CLIENT_ID = "standin"
BANDCAMP_IDENTITY = "standin"
PUBLIC = os.environ.get("PUBLIC_URL", "http://localhost:8095")
INTERNAL = os.environ.get("INTERNAL_URL", "http://streaming-standin:8080")
AUDIO = Path(os.environ.get("AUDIO_ROOT", "/tmp/standin"))
MUSICBRAINZ = "https://musicbrainz.org/ws/2"

app = FastAPI(title="Qobuz, Tidal and Bandcamp stand-ins")
_lock = threading.Lock()
_last_call = 0.0
_releases: dict[str, dict[str, Any] | None] = {}
# Albums and tracks by the number both services know them by.
_albums: dict[int, dict[str, Any]] = {}
_by_release: dict[str, int] = {}
_tracks: dict[int, tuple[dict[str, Any], dict[str, Any]]] = {}


# ---- MusicBrainz ----

def _musicbrainz(path: str, **params: Any) -> dict[str, Any]:
    global _last_call
    for attempt in range(4):
        with _lock:
            # MusicBrainz asks for a request a second at most, and answers 503 when it's busy.
            time.sleep(max(0.0, _last_call + 1.1 + attempt * 2 - time.monotonic()))
            _last_call = time.monotonic()
            response = httpx.get(f"{MUSICBRAINZ}{path}", params={**params, "fmt": "json"}, timeout=30,
                                 headers={"User-Agent": "Melodex-sandbox-standin/0.1 (https://github.com/dextrd/Melodex)"})
        if response.status_code != 503:
            break
    response.raise_for_status()
    return response.json()


def _credit(credits: list[dict[str, Any]] | None) -> str:
    return "".join(c.get("name", "") + c.get("joinphrase", "") for c in credits or [])


def _album(release_id: str) -> dict[str, Any] | None:
    if release_id in _by_release:
        return _albums[_by_release[release_id]]
    if release_id not in _releases:
        try:
            _releases[release_id] = _musicbrainz(f"/release/{release_id}", inc="recordings isrcs artist-credits")
        except httpx.HTTPError:
            _releases[release_id] = None
    release = _releases[release_id]
    if not release:
        return None
    number = len(_albums) + 1
    album = {"id": number, "mbid": release_id, "title": release["title"], "artist": _credit(release.get("artist-credit")),
             "upc": release.get("barcode") or None, "tracks": []}
    for medium in release.get("media", []):
        for track in medium.get("tracks", []):
            recording = track.get("recording") or {}
            track_id = number * 1000 + medium.get("position", 1) * 100 + track.get("position", 0)
            entry = {"id": track_id, "title": track.get("title") or recording.get("title", ""),
                     "duration": round((track.get("length") or recording.get("length") or 180000) / 1000),
                     "isrc": (recording.get("isrcs") or [None])[0], "disc": medium.get("position", 1),
                     "position": track.get("position", 0),
                     "artist": _credit(track.get("artist-credit") or recording.get("artist-credit")) or album["artist"]}
            album["tracks"].append(entry)
            _tracks[track_id] = (entry, album)
    _albums[number] = album
    _by_release[release_id] = number
    return album


def _by_artist(text: str, field: str) -> str:
    """The plugins search for "artist title": MusicBrainz finds that best as the artist's releases with those words."""
    for artist in _musicbrainz("/artist", query=_escape(text), limit=3).get("artists", []):
        name = artist.get("name", "")
        if text.casefold().startswith(name.casefold()) and (rest := text[len(name):].strip()):
            return f"arid:{artist['id']} AND {field}:({_escape(rest)})"
    return _escape(text)


def _search_albums(query: str) -> list[dict[str, Any]]:
    text = query.strip()
    lucene = f"barcode:{text}" if text.isdigit() else _by_artist(text, "release")
    found = _musicbrainz("/release", query=lucene, limit=4).get("releases", [])
    return [a for a in (_album(r["id"]) for r in found) if a]


def _search_tracks(query: str) -> list[tuple[dict[str, Any], dict[str, Any]]]:
    text = query.strip()
    isrc = re.fullmatch(r"[A-Za-z]{2}[A-Za-z0-9]{3}\d{7}", text)
    lucene = f"isrc:{text}" if isrc else _by_artist(text, "recording")
    found = []
    for recording in _musicbrainz("/recording", query=lucene, limit=3).get("recordings", []):
        for release in (recording.get("releases") or [])[:1]:
            if (album := _album(release["id"])) is None:
                continue
            found += [(t, album) for t in album["tracks"] if t["title"].casefold() == recording["title"].casefold()][:1]
    return found


def _escape(text: str) -> str:
    return re.sub(r'([+\-!(){}\[\]^"~*?:\\/]|&&|\|\|)', r"\\\1", text)


# ---- Audio ----

def _flac(track_id: int, hires: bool) -> Path:
    track, _ = _tracks[track_id]
    AUDIO.mkdir(parents=True, exist_ok=True)
    path = AUDIO / f"{track_id}-{'hires' if hires else 'cd'}.flac"
    if not path.exists():
        rate, format = ("96000", ["-sample_fmt", "s32", "-bits_per_raw_sample", "24"]) if hires else ("44100", ["-sample_fmt", "s16"])
        tone = 220 + (track["position"] * 55) % 440
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", f"sine=f={tone}:r={rate}",
                        "-t", str(track["duration"]), "-af", "volume=0.05", "-ac", "2", "-c:a", "flac", *format,
                        str(path.with_suffix(".tmp.flac"))], check=True)
        path.with_suffix(".tmp.flac").rename(path)
    return path


def _segments(track_id: int) -> list[bytes]:
    """The hi-res FLAC as fragmented MP4, cut into an initialisation segment and the rest."""
    mp4 = AUDIO / f"{track_id}-hires.mp4"
    if not mp4.exists():
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(_flac(track_id, True)), "-c:a", "copy", "-strict", "-2",
                        "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "10000000",
                        str(mp4.with_suffix(".tmp.mp4"))], check=True)
        mp4.with_suffix(".tmp.mp4").rename(mp4)
    data = mp4.read_bytes()
    size = max(1, len(data) // 8)
    return [data[i:i + size] for i in range(0, len(data), size)]


# ---- Qobuz ----

_qobuz_tokens: set[str] = set()


def _qobuz_album(album: dict[str, Any], with_tracks: bool) -> dict[str, Any]:
    body = {"id": str(album["id"]), "title": album["title"], "upc": album["upc"], "artist": {"name": album["artist"]},
            "tracks_count": len(album["tracks"]), "maximum_bit_depth": 24, "maximum_sampling_rate": 96.0,
            "streamable": True}
    if with_tracks:
        body["tracks"] = {"items": [_qobuz_track(t, None) for t in album["tracks"]]}
    return body


def _qobuz_track(track: dict[str, Any], album: dict[str, Any] | None) -> dict[str, Any]:
    body = {"id": track["id"], "title": track["title"], "duration": track["duration"], "isrc": track["isrc"],
            "track_number": track["position"], "media_number": track["disc"], "performer": {"name": track["artist"]},
            "maximum_bit_depth": 24, "maximum_sampling_rate": 96.0, "streamable": True}
    if album:
        body["album"] = _qobuz_album(album, False)
    return body


@app.api_route("/qobuz/api.json/0.2/{path:path}", methods=["GET"])
def qobuz(path: str, request: Request) -> Response:
    params = request.query_params
    if request.headers.get("X-App-Id") != QOBUZ_APP_ID:
        return JSONResponse({"message": "Invalid or missing app_id parameter"}, 400)
    user = {"display_name": "sandbox listener", "credential": {"parameters": {
        "short_label": "Studio (stand-in)", "lossless_streaming": True, "hires_streaming": True}}}
    if path == "user/login":
        token = secrets.token_hex(16)
        _qobuz_tokens.add(token)
        return JSONResponse({"user_auth_token": token, "user": user})
    if request.headers.get("X-User-Auth-Token") not in _qobuz_tokens:
        return JSONResponse({"message": "User authentication is required."}, 401)
    if path == "user/get":
        return JSONResponse(user)
    if path == "album/search":
        return JSONResponse({"albums": {"items": [_qobuz_album(a, False) for a in _search_albums(params["query"])]}})
    if path == "album/get":
        return JSONResponse(_qobuz_album(_albums[int(params["album_id"])], True))
    if path == "track/search":
        return JSONResponse({"tracks": {"items": [_qobuz_track(t, a) for t, a in _search_tracks(params["query"])]}})
    if path == "track/getFileUrl":
        wanted = hashlib.md5(f"trackgetFileUrlformat_id{params['format_id']}intentstreamtrack_id{params['track_id']}"
                             f"{params['request_ts']}{QOBUZ_SECRET}".encode()).hexdigest()
        if params.get("request_sig") != wanted:
            return JSONResponse({"message": "Invalid Request Signature parameter (request_sig)"}, 400)
        hires = params["format_id"] in ("7", "27")
        return JSONResponse({"url": f"{INTERNAL}/qobuz/file/{params['track_id']}?hires={int(hires)}",
                             "mime_type": "audio/flac", "format_id": int(params["format_id"])})
    return JSONResponse({"message": f"No stand-in for {path}"}, 404)


@app.get("/qobuz/file/{track_id}")
def qobuz_file(track_id: int, hires: int = 1) -> FileResponse:
    return FileResponse(_flac(track_id, bool(hires)), media_type="audio/flac")


# ---- Tidal ----

_codes: dict[str, dict[str, Any]] = {}
_tidal_tokens: set[str] = set()


def _tidal_token() -> JSONResponse:
    access = secrets.token_hex(16)
    _tidal_tokens.add(access)
    return JSONResponse({"access_token": access, "refresh_token": f"refresh-{secrets.token_hex(8)}", "expires_in": 3600,
                         "user": {"userId": 1, "countryCode": "GB", "username": "sandbox listener"}})


async def _form(request: Request) -> dict[str, str]:
    # Read by hand: the image the stand-in runs in has no form parser.
    return dict(parse_qsl((await request.body()).decode()))


@app.post("/tidal/oauth2/device_authorization")
async def tidal_device(request: Request) -> JSONResponse:
    if (await _form(request)).get("client_id") != TIDAL_CLIENT_ID:
        return JSONResponse({"error": "invalid_client", "error_description": "Client not found"}, 401)
    code = "".join(secrets.choice("ABCDEFGHJKLMNPQRSTUVWXYZ") for _ in range(5))
    _codes[code] = {"device": secrets.token_hex(12), "approved": False}
    return JSONResponse({"deviceCode": _codes[code]["device"], "userCode": code,
                         "verificationUriComplete": f"{PUBLIC}/tidal/link/{code}", "expiresIn": 600, "interval": 2})


@app.post("/tidal/oauth2/token")
async def tidal_token(request: Request) -> JSONResponse:
    form = await _form(request)
    if form.get("client_id") != TIDAL_CLIENT_ID:
        return JSONResponse({"error": "invalid_client", "error_description": "Client not found"}, 401)
    if form.get("grant_type") == "refresh_token":
        valid = form.get("refresh_token", "").startswith("refresh-")
        return _tidal_token() if valid else JSONResponse({"error": "invalid_grant"}, 400)
    code = next((c for c in _codes.values() if c["device"] == form.get("device_code")), None)
    if code is None:
        return JSONResponse({"error": "expired_token", "error_description": "The code has run out."}, 400)
    if not code["approved"]:
        return JSONResponse({"error": "authorization_pending"}, 400)
    return _tidal_token()


_PAGE = """<!doctype html><meta name=viewport content="width=device-width"><title>Tidal stand-in</title>
<body style="font:16px system-ui;max-width:28rem;margin:3rem auto;padding:0 1rem">
<h1 style="font-size:1.4rem">Tidal stand-in</h1>{body}</body>"""


@app.get("/tidal/link/{code}")
def tidal_link(code: str) -> HTMLResponse:
    if code not in _codes:
        return HTMLResponse(_PAGE.format(body="<p>That code has run out. Check the plugin again for a new one.</p>"), 404)
    if _codes[code]["approved"]:
        return HTMLResponse(_PAGE.format(body="<p>Signed in. You can close this page.</p>"))
    return HTMLResponse(_PAGE.format(body=f"""<p>The sandbox's stand-in for Tidal's sign-in page. Melodex is asking to
        sign in with the code <b>{html.escape(code)}</b>.</p>
        <form method=post><button style="font:inherit;padding:.5rem 1rem">Sign in</button></form>"""))


@app.post("/tidal/link/{code}")
def tidal_approve(code: str) -> HTMLResponse:
    if code in _codes:
        _codes[code]["approved"] = True
    return tidal_link(code)


def _tidal_album(album: dict[str, Any]) -> dict[str, Any]:
    return {"id": album["id"], "title": album["title"], "upc": album["upc"], "artists": [{"name": album["artist"]}],
            "numberOfTracks": len(album["tracks"]), "allowStreaming": True, "audioQuality": "LOSSLESS",
            "mediaMetadata": {"tags": ["LOSSLESS", "HIRES_LOSSLESS"]}}


def _tidal_track(track: dict[str, Any], album: dict[str, Any]) -> dict[str, Any]:
    return {"id": track["id"], "title": track["title"], "duration": track["duration"], "isrc": track["isrc"],
            "trackNumber": track["position"], "volumeNumber": track["disc"], "artists": [{"name": track["artist"]}],
            "album": {"id": album["id"], "title": album["title"]}, "allowStreaming": True, "audioQuality": "LOSSLESS",
            "mediaMetadata": {"tags": ["LOSSLESS", "HIRES_LOSSLESS"]}}


@app.get("/tidal/v1/{path:path}")
def tidal(path: str, request: Request) -> Response:
    if request.headers.get("Authorization", "").removeprefix("Bearer ") not in _tidal_tokens:
        return JSONResponse({"userMessage": "The token has expired."}, 401)
    params = request.query_params
    if path == "sessions":
        return JSONResponse({"userId": 1, "countryCode": "GB"})
    if path == "users/1/subscription":
        return JSONResponse({"subscription": {"type": "HIFI_PLUS (stand-in)"}, "highestSoundQuality": "HI_RES_LOSSLESS"})
    if path == "search":
        if params.get("types") == "ALBUMS":
            return JSONResponse({"albums": {"items": [_tidal_album(a) for a in _search_albums(params["query"])]}})
        return JSONResponse({"tracks": {"items": [_tidal_track(t, a) for t, a in _search_tracks(params["query"])]}})
    if match := re.fullmatch(r"albums/(\d+)(/tracks)?", path):
        album = _albums.get(int(match[1]))
        if album is None:
            return JSONResponse({"userMessage": "Album not found"}, 404)
        if match[2]:
            items = [_tidal_track(t, album) for t in album["tracks"]]
            return JSONResponse({"items": items, "totalNumberOfItems": len(items)})
        return JSONResponse(_tidal_album(album))
    if match := re.fullmatch(r"tracks/(\d+)/playbackinfopostpaywall", path):
        track_id = int(match[1])
        if params.get("audioquality") == "HI_RES_LOSSLESS":
            count = len(_segments(track_id)) - 1
            manifest = f"""<?xml version="1.0"?><MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static"><Period>
<AdaptationSet contentType="audio" mimeType="audio/mp4"><Representation id="FLAC,96000,24" codecs="flac" bandwidth="4600000"
audioSamplingRate="96000"><SegmentTemplate timescale="96000" initialization="{INTERNAL}/tidal/segment/{track_id}/0"
media="{INTERNAL}/tidal/segment/{track_id}/$Number$" startNumber="1"><SegmentTimeline><S d="960000" r="{count - 1}"/>
</SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>""".encode()
            kind, quality = "application/dash+xml", "HI_RES_LOSSLESS"
        else:
            manifest = json.dumps({"mimeType": "audio/flac", "codecs": "flac", "encryptionType": "NONE",
                                   "urls": [f"{INTERNAL}/tidal/file/{track_id}"]}).encode()
            kind, quality = "application/vnd.tidal.bts", "LOSSLESS"
        return JSONResponse({"trackId": track_id, "assetPresentation": "FULL", "audioQuality": quality,
                             "manifestMimeType": kind, "manifest": base64.b64encode(manifest).decode()})
    return JSONResponse({"userMessage": f"No stand-in for {path}"}, 404)


@app.get("/tidal/file/{track_id}")
def tidal_file(track_id: int) -> FileResponse:
    return FileResponse(_flac(track_id, False), media_type="audio/flac")


@app.get("/tidal/segment/{track_id}/{number}")
def tidal_segment(track_id: int, number: int) -> Response:
    return Response(_segments(track_id)[number], media_type="audio/mp4")


# ---- Bandcamp ----

# What's been bought, newest first: ("a", album number) or ("t", track number).
_bought: list[tuple[str, int]] = []

_BANDCAMP_PAGE = """<!doctype html><meta name=viewport content="width=device-width"><title>Bandcamp stand-in</title>
<body style="font:16px system-ui;max-width:32rem;margin:3rem auto;padding:0 1rem">
<h1 style="font-size:1.4rem">Bandcamp stand-in</h1>{body}</body>"""


def _purchase(kind: str, number: int) -> tuple[str, str, str | None, list[dict[str, Any]]]:
    """Its title, artist, album (for a song) and tracks."""
    if kind == "a":
        album = _albums[number]
        return album["title"], album["artist"], None, album["tracks"]
    track, album = _tracks[number]
    return track["title"], track["artist"], album["title"], [track]


@app.get("/bandcamp")
def bandcamp_page(note: str = "") -> HTMLResponse:
    rows = "".join(f"<li>{html.escape(_purchase(k, n)[1])} – {html.escape(_purchase(k, n)[0])}"
                   f"{' (album)' if k == 'a' else ' (song)'}</li>" for k, n in _bought) or "<li>Nothing yet.</li>"
    return HTMLResponse(_BANDCAMP_PAGE.format(body=f"""<p>The sandbox's stand-in for your Bandcamp collection. The
        plugin signs in with the identity cookie <code>{BANDCAMP_IDENTITY}</code>.</p>
        {f'<p><b>{html.escape(note)}</b></p>' if note else ''}
        <form method=post action=/bandcamp/buy style="display:grid;gap:.5rem">
          <label>Artist and album or song, as MusicBrainz names them
            <input name=query required style="font:inherit;width:100%;box-sizing:border-box" placeholder="Portishead Third"></label>
          <span><button name=kind value=album style="font:inherit">Buy the album</button>
          <button name=kind value=song style="font:inherit">Buy the song</button></span>
        </form>
        <h2 style="font-size:1.1rem">Your collection</h2><ul>{rows}</ul>"""))


@app.post("/bandcamp/buy")
async def bandcamp_buy(request: Request) -> HTMLResponse:
    form = await _form(request)
    query = form.get("query", "")
    if form.get("kind") == "song":
        found = [("t", t["id"], f"{t['title']} by {t['artist']}") for t, _ in _search_tracks(query)[:1]]
    else:
        found = [("a", a["id"], f"{a['title']} by {a['artist']}") for a in _search_albums(query)[:1]]
    if not found:
        return bandcamp_page(note=f"MusicBrainz has nothing called {query}.")
    kind, number, name = found[0]
    if (kind, number) not in _bought:
        _bought.insert(0, (kind, number))
    return bandcamp_page(note=f"Bought {name}.")


def _signed_in(request: Request) -> bool:
    return request.cookies.get("identity") == BANDCAMP_IDENTITY


@app.get("/bandcamp/api/fan/2/collection_summary")
def bandcamp_summary(request: Request) -> JSONResponse:
    if not _signed_in(request):
        return JSONResponse({"error": True, "error_message": "Not logged in"})
    return JSONResponse({"fan_id": 1, "collection_summary": {"username": "sandbox-listener"}})


@app.post("/bandcamp/api/fancollection/1/{kind}")
def bandcamp_collection(kind: str, request: Request) -> JSONResponse:
    if not _signed_in(request):
        return JSONResponse({"error": True, "error_message": "Not logged in"})
    items, links = [], {}
    for index, (k, number) in enumerate(_bought if kind == "collection_items" else []):
        title, artist, album, _ = _purchase(k, number)
        items.append({"tralbum_type": k, "tralbum_id": number, "item_type": "album" if k == "a" else "track",
                      "item_title": title, "band_name": artist, "album_title": album, "sale_item_type": "p",
                      "sale_item_id": index + 1, "item_url": f"{INTERNAL}/bandcamp/{'album' if k == 'a' else 'track'}/{number}"})
        links[f"p{index + 1}"] = f"{INTERNAL}/bandcamp/download?payment_id={k}{number}"
    return JSONResponse({"items": items, "redownload_urls": links, "more_available": False, "last_token": "0"})


@app.get("/bandcamp/{kind}/{number}")
def bandcamp_tralbum(kind: str, number: int) -> HTMLResponse:
    title, artist, _, tracks = _purchase("a" if kind == "album" else "t", number)
    # Bandcamp has no discs: tracks are numbered straight through.
    tralbum = {"current": {"title": title}, "artist": artist,
               "trackinfo": [{"title": t["title"], "track_num": n if kind == "album" else None,
                              "duration": t["duration"], "artist": None} for n, t in enumerate(tracks, 1)]}
    return HTMLResponse(f'<script data-tralbum="{html.escape(json.dumps(tralbum))}"></script>')


@app.get("/bandcamp/download")
def bandcamp_download_page(payment_id: str, request: Request) -> HTMLResponse:
    if not _signed_in(request):
        return HTMLResponse("Not logged in", 403)
    kind = "album" if payment_id.startswith("a") else "track"
    downloads = {f: {"url": f"{INTERNAL}/bandcamp/files/download/{kind}?enc={f}&id={payment_id}"}
                 for f in ["mp3-v0", "mp3-320", "flac", "aac-hi", "vorbis", "alac", "wav", "aiff-lossless"]}
    blob = {"digital_items": [{"downloads": downloads}]}
    return HTMLResponse(f'<div id="pagedata" data-blob="{html.escape(json.dumps(blob))}"></div>')


@app.get("/bandcamp/files/statdownload/{kind}")
def bandcamp_status(kind: str, request: Request) -> JSONResponse:
    query = "&".join(f"{k}={v}" for k, v in request.query_params.items() if k != ".vrs")
    return JSONResponse({"result": "ok", "download_url": f"{INTERNAL}/bandcamp/files/download/{kind}?{query}"})


@app.get("/bandcamp/files/download/{kind}")
def bandcamp_file(kind: str, id: str, enc: str = "flac") -> Response:
    if enc != "flac":
        return Response("The stand-in only makes FLAC.", 404)
    title, artist, album, tracks = _purchase(id[0], int(id[1:]))
    if kind == "track":
        name = f"{artist} - {title}.flac"
        return FileResponse(_flac(tracks[0]["id"], False), media_type="audio/flac", filename=name)
    zipped = AUDIO / f"bandcamp-{id}.zip"
    if not zipped.exists():
        AUDIO.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(zipped.with_suffix(".tmp"), "w") as out:
            for n, track in enumerate(tracks, 1):
                out.write(_flac(track["id"], False), f"{artist} - {title} - {n:02} {track['title'].replace('/', '_')}.flac")
        zipped.with_suffix(".tmp").rename(zipped)
    return FileResponse(zipped, media_type="application/zip", filename=f"{artist} - {title}.zip")
