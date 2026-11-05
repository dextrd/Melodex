"""Tidal's API, as the plugin uses it: signing in with a code, searching, tracklists and each track's stream."""

from __future__ import annotations

import base64
import json
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

import httpx

from .contract import Target, env
from .streaming import Album, Stream, Track

API = env("TIDAL_API", "https://api.tidal.com/v1")
AUTH = env("TIDAL_AUTH", "https://auth.tidal.com/v1/oauth2")
DATA = Path(env("DATA_ROOT", "/data"))
QUALITIES = ["hires", "lossless", "aac"]
_AUDIO = {"hires": "HI_RES_LOSSLESS", "lossless": "LOSSLESS", "aac": "HIGH"}
_LABELS = {"HI_RES_LOSSLESS": "hi-res FLAC", "HI_RES": "hi-res FLAC", "LOSSLESS": "16-bit/44.1 kHz FLAC",
           "HIGH": "AAC 320", "LOW": "AAC 96"}
_DASH = "{urn:mpeg:dash:schema:mpd:2011}"
_SCOPE = "r_usr w_usr w_sub"

_lock = threading.Lock()
# Sign-ins waiting for someone to enter the code, and why the last one failed, by client ID.
_waiting: dict[str, dict[str, Any]] = {}
_failed: dict[str, str] = {}


def http(base_url: str) -> httpx.Client:
    return httpx.Client(base_url=base_url, timeout=30, follow_redirects=True,
                        headers={"User-Agent": "Melodex-retriever-tidal/0.1"})


def download() -> httpx.Client:
    return httpx.Client(timeout=60, follow_redirects=True, headers={"User-Agent": "Melodex-retriever-tidal/0.1"})


# ---- Signing in ----

def _saved_path(client_id: str) -> Path:
    return DATA / f"tidal-{''.join(c for c in client_id if c.isalnum())[:40]}.json"


def saved(client_id: str) -> dict[str, Any] | None:
    try:
        return json.loads(_saved_path(client_id).read_text())
    except (OSError, ValueError):
        return None


def _save(client_id: str, session: dict[str, Any]) -> None:
    DATA.mkdir(parents=True, exist_ok=True)
    path = _saved_path(client_id)
    path.write_text(json.dumps(session))
    path.chmod(0o600)


def forget(client_id: str) -> None:
    _saved_path(client_id).unlink(missing_ok=True)


def _session(token: dict[str, Any], previous: dict[str, Any] | None = None) -> dict[str, Any]:
    user = token.get("user") or {}
    previous = previous or {}
    return {
        "access": token["access_token"],
        "refresh": token.get("refresh_token") or previous.get("refresh"),
        "expires": time.time() + float(token.get("expires_in", 3600)),
        "country": user.get("countryCode") or previous.get("country"),
        "user": user.get("username") or user.get("email") or previous.get("user"),
        "userId": user.get("userId") or previous.get("userId"),
    }


def _client(settings: dict[str, Any]) -> dict[str, str]:
    body = {"client_id": str(settings.get("clientId") or "")}
    if secret := settings.get("clientSecret"):
        body["client_secret"] = str(secret)
    return body


def sign_in(settings: dict[str, Any]) -> dict[str, Any]:
    """Starts signing in, or says how the one already started is going: the link and code to enter, until it's done."""
    client_id = str(settings.get("clientId") or "")
    with _lock:
        if failed := _failed.pop(client_id, None):
            raise PermissionError(f"Signing in to Tidal failed: {failed}")
        waiting = _waiting.get(client_id)
        if waiting and waiting["expires"] > time.time():
            return waiting
        with http(AUTH) as auth:
            response = auth.post("/device_authorization", data={**_client(settings), "scope": _SCOPE})
            if response.status_code in (400, 401):
                raise PermissionError(f"Tidal didn't accept the client ID: {_message(response)}")
            response.raise_for_status()
            body = response.json()
        uri = body.get("verificationUriComplete") or body.get("verificationUri") or "link.tidal.com"
        waiting = _waiting[client_id] = {
            "link": uri if uri.startswith("http") else f"https://{uri}",
            "code": body.get("userCode"),
            "device": body["deviceCode"],
            "expires": time.time() + float(body.get("expiresIn", 300)),
            "interval": float(body.get("interval", 2)),
        }
    threading.Thread(target=_wait_for_code, args=(dict(settings), waiting), daemon=True).start()
    return waiting


def _wait_for_code(settings: dict[str, Any], waiting: dict[str, Any]) -> None:
    client_id = str(settings.get("clientId") or "")
    try:
        with http(AUTH) as auth:
            while time.time() < waiting["expires"]:
                time.sleep(waiting["interval"])
                response = auth.post("/token", data={
                    **_client(settings), "device_code": waiting["device"], "scope": _SCOPE,
                    "grant_type": "urn:ietf:params:oauth:grant-type:device_code"})
                if response.status_code == 200:
                    _save(client_id, _session(response.json()))
                    return
                if _error(response) not in ("authorization_pending", "slow_down"):
                    _failed[client_id] = _message(response)
                    return
    except Exception as e:
        _failed[client_id] = str(e)
    finally:
        with _lock:
            if _waiting.get(client_id) is waiting:
                del _waiting[client_id]


class Tidal:
    name = "Tidal"

    def __init__(self, settings: dict[str, Any]):
        self.settings = settings
        self.client_id = str(settings.get("clientId") or "")
        self.quality = settings.get("quality", "hires")
        self.format = "aac" if self.quality == "aac" else "flac"
        self.http = http(API)
        self.auth = http(AUTH)
        self.session: dict[str, Any] | None = None

    def __enter__(self) -> Tidal:
        return self

    def __exit__(self, *_: Any) -> None:
        self.http.close()
        self.auth.close()

    def signed_in(self) -> dict[str, Any] | None:
        """The session, refreshed when it's run out; None when nobody has signed in."""
        if self.session is None:
            if token := self.settings.get("refreshToken"):
                self.session = {"refresh": str(token), "expires": 0}
            else:
                self.session = saved(self.client_id)
        if self.session is None:
            return None
        if self.session["expires"] - 60 < time.time():
            self._refresh()
        return self.session

    def _refresh(self) -> None:
        assert self.session is not None
        response = self.auth.post("/token", data={**_client(self.settings), "refresh_token": self.session["refresh"],
                                                  "grant_type": "refresh_token", "scope": _SCOPE})
        if response.status_code in (400, 401):
            if not self.settings.get("refreshToken"):
                forget(self.client_id)
            raise PermissionError(f"Tidal ended the sign-in ({_message(response)}); sign in again.")
        response.raise_for_status()
        self.session = _session(response.json(), self.session)
        if not self.session.get("country"):
            account = self._get("/sessions", country=False)
            self.session["country"] = account.get("countryCode")
            self.session["userId"] = account.get("userId")
        if not self.settings.get("refreshToken"):
            _save(self.client_id, self.session)

    def _get(self, path: str, country: bool = True, **params: Any) -> dict[str, Any]:
        for again in (False, True):
            session = self.signed_in()
            if session is None:
                raise PermissionError("Not signed in to Tidal yet; the plugin's health check gives the link to sign in.")
            if country:
                params["countryCode"] = session.get("country") or "US"
            response = self.http.get(path, params=params, headers={"Authorization": f"Bearer {session['access']}"})
            if response.status_code == 401 and not again:
                session["expires"] = 0
                continue
            if response.status_code in (400, 401, 403):
                raise PermissionError(f"Tidal refused {path}: {_message(response)}")
            response.raise_for_status()
            return response.json()
        raise AssertionError("unreachable")

    def account(self) -> dict[str, Any]:
        session = self.signed_in()
        assert session is not None
        return self._get(f"/users/{session['userId']}/subscription", country=False) if session.get("userId") else {}

    # ---- Searching ----

    def albums(self, target: Target) -> list[Album]:
        body = self._get("/search", query=f"{target.artist} {target.title}", types="ALBUMS", limit=10)
        return [self._album(a) for a in body.get("albums", {}).get("items", []) if a.get("allowStreaming", True)]

    def album(self, album_id: str) -> Album:
        album = self._album(self._get(f"/albums/{album_id}"))
        items: list[dict[str, Any]] = []
        while True:
            page = self._get(f"/albums/{album_id}/tracks", limit=100, offset=len(items))
            items += page.get("items", [])
            if not page.get("items") or len(items) >= page.get("totalNumberOfItems", 0):
                break
        album.tracks = [self._track(t) for t in items if t.get("allowStreaming", True)]
        return album

    def tracks(self, target: Target) -> list[Track]:
        body = self._get("/search", query=f"{target.artist} {target.title}", types="TRACKS", limit=10)
        return [self._track(t) for t in body.get("tracks", {}).get("items", []) if t.get("allowStreaming", True)]

    def _album(self, album: dict[str, Any]) -> Album:
        return Album(
            id=str(album["id"]),
            title=_titled(album),
            artist=_artists(album)[0] if _artists(album) else "",
            upc=album.get("upc"),
            track_count=album.get("numberOfTracks"),
            quality=self._quality(album),
            url=f"https://tidal.com/browse/album/{album['id']}",
        )

    def _track(self, track: dict[str, Any]) -> Track:
        album = track.get("album") or {}
        return Track(
            id=str(track["id"]),
            title=_titled(track),
            artists=_artists(track),
            duration=track.get("duration"),
            disc=track.get("volumeNumber") or 1,
            position=track.get("trackNumber") or 0,
            isrc=track.get("isrc"),
            album=album.get("title"),
            album_id=str(album["id"]) if album.get("id") else None,
            quality=self._quality(track),
        )

    def _quality(self, item: dict[str, Any]) -> str | None:
        if self.quality == "aac":
            return _LABELS["HIGH"]
        tags = (item.get("mediaMetadata") or {}).get("tags") or []
        if self.quality == "hires" and "HIRES_LOSSLESS" in tags:
            return _LABELS["HI_RES_LOSSLESS"]
        return _LABELS["LOSSLESS"] if "LOSSLESS" in tags or item.get("audioQuality") in ("LOSSLESS", "HI_RES", "HI_RES_LOSSLESS") else None

    # ---- Fetching ----

    def stream(self, track_id: str) -> Stream:
        body = self._get(f"/tracks/{track_id}/playbackinfopostpaywall", audioquality=_AUDIO[self.quality],
                         playbackmode="STREAM", assetpresentation="FULL")
        if body.get("assetPresentation") == "PREVIEW":
            raise PermissionError("Tidal only gives a 30-second preview of this track; your subscription doesn't stream "
                                  "it in full.")
        manifest = base64.b64decode(body.get("manifest") or "")
        kind = body.get("manifestMimeType", "")
        if kind == "application/vnd.tidal.bts":
            return _bts(json.loads(manifest))
        if kind == "application/dash+xml":
            return _dash(manifest)
        raise RuntimeError(f"Tidal sent a kind of stream this plugin doesn't know ({kind or 'none'}).")


def _bts(manifest: dict[str, Any]) -> Stream:
    if (manifest.get("encryptionType") or "NONE") != "NONE":
        raise RuntimeError("Tidal sent this track encrypted; Melodex doesn't decrypt streams, so it can't be fetched "
                           "from here.")
    urls = manifest.get("urls") or []
    if not urls:
        raise RuntimeError("Tidal's stream had no address in it.")
    mime = manifest.get("mimeType", "")
    suffix = ".flac" if "flac" in mime or manifest.get("codecs") == "flac" else ".m4a"
    return Stream([urls[0]], suffix)


def _dash(manifest: bytes) -> Stream:
    """A DASH manifest's one representation as the addresses of its segments, in order."""
    root = ET.fromstring(manifest)
    if root.find(f".//{_DASH}ContentProtection") is not None:
        raise RuntimeError("Tidal sent this track encrypted; Melodex doesn't decrypt streams, so it can't be fetched "
                           "from here.")
    representation = root.find(f".//{_DASH}Representation")
    if representation is None:
        raise RuntimeError("Tidal's stream had no audio in it.")
    template = representation.find(f"{_DASH}SegmentTemplate")
    if template is None:
        adaptation = root.find(f".//{_DASH}AdaptationSet")
        template = adaptation.find(f"{_DASH}SegmentTemplate") if adaptation is not None else None
    if template is None:
        raise RuntimeError("Tidal's stream doesn't say where its segments are.")
    count = sum(1 + int(s.get("r", "0")) for s in template.iterfind(f"{_DASH}SegmentTimeline/{_DASH}S"))
    start = int(template.get("startNumber", "1"))
    media, initialisation = template.get("media", ""), template.get("initialization", "")
    urls = [initialisation] + [media.replace("$Number$", str(n)) for n in range(start, start + count)]
    flac = "flac" in (representation.get("codecs") or "")
    return Stream(urls, ".flac" if flac else ".m4a", remux=True)


def _artists(item: dict[str, Any]) -> list[str]:
    names = [a.get("name") for a in item.get("artists") or [] if a.get("name")]
    if not names and (item.get("artist") or {}).get("name"):
        names = [item["artist"]["name"]]
    return names


def _titled(item: dict[str, Any]) -> str:
    title, version = item.get("title") or "", item.get("version")
    return f"{title} ({version})" if version and version.lower() not in title.lower() else title


def _error(response: httpx.Response) -> str:
    try:
        return str(response.json().get("error") or "")
    except ValueError:
        return ""


def _message(response: httpx.Response) -> str:
    try:
        body = response.json()
        return str(body.get("userMessage") or body.get("error_description") or body.get("error") or response.status_code)
    except ValueError:
        return str(response.status_code)
