"""Qobuz's API, as the plugin uses it: signing in, searching, tracklists and the address of each track's file."""

from __future__ import annotations

import hashlib
import threading
import time
from typing import Any

import httpx

from .contract import Target, env
from .streaming import Album, Stream, Track

API = env("QOBUZ_API", "https://www.qobuz.com/api.json/0.2")
QUALITIES = ["hires", "cd", "mp3"]
_FORMAT_IDS = {"hires": 27, "cd": 6, "mp3": 5}

# Sign-ins by app, email and password, so a search doesn't sign in again.
_tokens: dict[tuple[str, str, str], str] = {}
_lock = threading.Lock()


def http() -> httpx.Client:
    return httpx.Client(base_url=API, timeout=30, follow_redirects=True,
                        headers={"User-Agent": "Melodex-retriever-qobuz/0.1"})


def download() -> httpx.Client:
    return httpx.Client(timeout=60, follow_redirects=True, headers={"User-Agent": "Melodex-retriever-qobuz/0.1"})


def _password(value: str) -> str:
    """Qobuz takes the password's MD5; one already in that form is passed on as it is."""
    if len(value) == 32 and all(c in "0123456789abcdef" for c in value):
        return value
    return hashlib.md5(value.encode()).hexdigest()


class Qobuz:
    name = "Qobuz"

    def __init__(self, settings: dict[str, Any]):
        self.settings = settings
        self.app_id = str(settings.get("appId") or "")
        self.quality = settings.get("quality", "hires")
        self.format = "mp3" if self.quality == "mp3" else "flac"
        self.http = http()
        self.http.headers["X-App-Id"] = self.app_id
        self.user: dict[str, Any] | None = None

    def __enter__(self) -> Qobuz:
        return self

    def __exit__(self, *_: Any) -> None:
        self.http.close()

    # ---- Signing in ----

    def _key(self) -> tuple[str, str, str]:
        return self.app_id, str(self.settings.get("email") or ""), _password(str(self.settings.get("password") or ""))

    def sign_in(self, again: bool = False) -> str:
        if token := self.settings.get("userAuthToken"):
            return str(token)
        with _lock:
            if not again and (token := _tokens.get(self._key())):
                return token
            response = self.http.get("/user/login", params={
                "email": self.settings.get("email"), "password": _password(str(self.settings.get("password") or "")),
                "app_id": self.app_id})
            if response.status_code in (400, 401):
                raise PermissionError(f"Qobuz didn't accept the sign-in: {_message(response)}")
            response.raise_for_status()
            body = response.json()
            self.user = body.get("user")
            token = _tokens[self._key()] = body["user_auth_token"]
            return token

    def _get(self, path: str, **params: Any) -> dict[str, Any]:
        for again in (False, True):
            response = self.http.get(path, params=params, headers={"X-User-Auth-Token": self.sign_in(again)})
            if response.status_code == 401 and not again and not self.settings.get("userAuthToken"):
                continue  # the saved sign-in ran out
            if response.status_code in (400, 401, 403):
                raise PermissionError(f"Qobuz refused {path}: {_message(response)}")
            response.raise_for_status()
            return response.json()
        raise AssertionError("unreachable")

    def account(self) -> dict[str, Any]:
        """The signed-in user, with what their subscription streams."""
        if self.user is None:
            self.user = self._get("/user/get")
        return self.user

    # ---- Searching ----

    def albums(self, target: Target) -> list[Album]:
        found = []
        queries = [q for q in [target.id("upc"), f"{target.artist} {target.title}"] if q]
        for query in queries:
            items = self._get("/album/search", query=query, limit=10).get("albums", {}).get("items", [])
            found += [self._album(a) for a in items if a.get("streamable", True)]
        return found

    def album(self, album_id: str) -> Album:
        return self._album(self._get("/album/get", album_id=album_id))

    def tracks(self, target: Target) -> list[Track]:
        found = []
        queries = [q for q in [target.id("isrc"), f"{target.artist} {target.title}"] if q]
        for query in queries:
            items = self._get("/track/search", query=query, limit=10).get("tracks", {}).get("items", [])
            found += [self._track(t, t.get("album")) for t in items if t.get("streamable", True)]
        return found

    def _album(self, album: dict[str, Any]) -> Album:
        tracks = album.get("tracks", {}).get("items") if isinstance(album.get("tracks"), dict) else None
        return Album(
            id=str(album["id"]),
            title=_titled(album),
            artist=(album.get("artist") or {}).get("name", ""),
            upc=album.get("upc"),
            track_count=album.get("tracks_count"),
            quality=self._quality(album),
            url=f"https://open.qobuz.com/album/{album['id']}",
            tracks=None if tracks is None else [self._track(t, album) for t in tracks if t.get("streamable", True)],
        )

    def _track(self, track: dict[str, Any], album: dict[str, Any] | None) -> Track:
        album = album or {}
        artists = [(track.get("performer") or {}).get("name") or (album.get("artist") or {}).get("name") or ""]
        return Track(
            id=str(track["id"]),
            title=_titled(track),
            artists=[a for a in artists if a],
            duration=track.get("duration"),
            disc=track.get("media_number") or 1,
            position=track.get("track_number") or 0,
            isrc=track.get("isrc"),
            album=_titled(album) if album.get("title") else None,
            album_id=str(album["id"]) if album.get("id") else None,
            quality=self._quality(track if track.get("maximum_bit_depth") else album),
        )

    def _quality(self, item: dict[str, Any]) -> str | None:
        if self.quality == "mp3":
            return "MP3 320"
        bits, rate = item.get("maximum_bit_depth"), item.get("maximum_sampling_rate")
        if not bits or not rate:
            return None
        if self.quality == "cd":
            bits, rate = min(bits, 16), min(rate, 44.1)
        return f"{bits}-bit/{rate:g} kHz"

    # ---- Fetching ----

    def stream(self, track_id: str) -> Stream:
        format_id = _FORMAT_IDS[self.quality]
        stamp = str(int(time.time()))
        secret = str(self.settings.get("appSecret") or "")
        signature = hashlib.md5(
            f"trackgetFileUrlformat_id{format_id}intentstreamtrack_id{track_id}{stamp}{secret}".encode()).hexdigest()
        try:
            body = self._get("/track/getFileUrl", track_id=track_id, format_id=format_id, intent="stream",
                             request_ts=stamp, request_sig=signature)
        except PermissionError as e:
            if "signature" in str(e).lower():
                raise PermissionError("Qobuz didn't accept the request's signature; check the app secret matches the "
                                      "app ID.") from e
            raise
        if body.get("sample") or not body.get("url"):
            raise PermissionError("Qobuz only gives a 30-second sample of this track; your subscription doesn't stream "
                                  "it in full.")
        suffix = ".mp3" if "mpeg" in str(body.get("mime_type", "")) else ".flac"
        return Stream([body["url"]], suffix)


def _titled(item: dict[str, Any]) -> str:
    title, version = item.get("title") or "", item.get("version")
    return f"{title} ({version})" if version and version.lower() not in title.lower() else title


def _message(response: httpx.Response) -> str:
    try:
        return str(response.json().get("message") or response.status_code)
    except ValueError:
        return str(response.status_code)
