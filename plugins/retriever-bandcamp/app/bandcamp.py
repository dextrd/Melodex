"""Bandcamp, as the plugin uses it: your collection, the tracklists of what's in it, and the downloads Bandcamp gives
for each purchase."""

from __future__ import annotations

import html
import json
import os
import re
import shutil
import threading
import time
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any
from urllib.parse import unquote

import httpx

from .contract import Job, Target, env, probe
from .matching import similarity
from .streaming import Album, Offer, Track

BASE = env("BANDCAMP_URL", "https://bandcamp.com")
# Bandcamp's names for its download formats, best first.
FORMATS = ["flac", "alac", "wav", "aiff-lossless", "mp3-320", "mp3-v0", "aac-hi", "vorbis"]
_SUFFIXES = {"flac": ".flac", "alac": ".m4a", "wav": ".wav", "aiff-lossless": ".aiff", "mp3-320": ".mp3",
             "mp3-v0": ".mp3", "aac-hi": ".m4a", "vorbis": ".ogg"}
_AUDIO = set(_SUFFIXES.values())
_FORMAT_NAMES = {"flac": "flac", "alac": "alac", "wav": "wav", "aiff-lossless": "aiff", "mp3-320": "mp3",
                 "mp3-v0": "mp3", "aac-hi": "aac", "vorbis": "vorbis"}
_USER_AGENT = "Melodex-retriever-bandcamp/0.1"

# Collections by sign-in, so searching for each song of an album doesn't read it again; kept briefly, so something
# just bought is found.
_collections: dict[str, tuple[float, list[Purchase]]] = {}
_KEEP_SECONDS = 60
# Downloads, kept for the other songs of an album asked for one at a time.
_KEEP_DOWNLOADS_SECONDS = 1800
_download_locks: dict[str, threading.Lock] = {}
_lock = threading.Lock()


def http(identity: str) -> httpx.Client:
    return httpx.Client(timeout=60, follow_redirects=True, cookies={"identity": identity},
                        headers={"User-Agent": _USER_AGENT})


@dataclass
class Purchase:
    """An album or song in your collection."""
    id: str  # "a" or "t", then Bandcamp's album or track number
    title: str
    artist: str
    url: str
    download: str | None
    album: str | None = None

    @property
    def is_album(self) -> bool:
        return self.id.startswith("a")


class Bandcamp:
    name = "Bandcamp"

    def __init__(self, settings: dict[str, Any]):
        self.identity = str(settings.get("identity") or "").strip()
        # Bandcamp's name for the download format; format is what candidates say they are.
        self.choice = settings.get("format", "flac")
        self.format = _FORMAT_NAMES[self.choice]
        self.http = http(self.identity)

    def __enter__(self) -> Bandcamp:
        return self

    def __exit__(self, *_: Any) -> None:
        self.http.close()

    # ---- Your collection ----

    def account(self) -> dict[str, Any]:
        response = self.http.get(f"{BASE}/api/fan/2/collection_summary")
        body = response.json() if response.headers.get("content-type", "").startswith("application/json") else {}
        if response.status_code in (401, 403) or not body.get("fan_id"):
            raise PermissionError("Bandcamp didn't accept the identity cookie; copy it again from a browser signed "
                                  "in to Bandcamp.")
        response.raise_for_status()
        return body

    def collection(self) -> list[Purchase]:
        with _lock:
            if (kept := _collections.get(self.identity)) and time.monotonic() - kept[0] < _KEEP_SECONDS:
                return kept[1]
        fan = self.account()["fan_id"]
        purchases: list[Purchase] = []
        # Hidden purchases are still yours to download.
        for kind in ["collection_items", "hidden_items"]:
            token = f"{int(time.time())}::a::"
            while True:
                response = self.http.post(f"{BASE}/api/fancollection/1/{kind}",
                                          json={"fan_id": fan, "older_than_token": token, "count": 100})
                response.raise_for_status()
                body = response.json()
                links = body.get("redownload_urls") or {}
                for item in body.get("items") or []:
                    if (purchase := _purchase(item, links)) is not None:
                        purchases.append(purchase)
                if not body.get("more_available") or not body.get("last_token"):
                    break
                token = body["last_token"]
        with _lock:
            _collections[self.identity] = (time.monotonic(), purchases)
        return purchases

    # ---- Searching, in streaming.py's terms ----

    def albums(self, target: Target) -> list[Album]:
        return [Album(id=p.id, title=p.title, artist=p.artist, offers=self._offers(), url=p.url)
                for p in self._named(target.title, target.artist, albums=True)]

    def album(self, album_id: str) -> Album:
        purchase = next(p for p in self.collection() if p.id == album_id)
        return Album(id=purchase.id, title=purchase.title, artist=purchase.artist, offers=self._offers(),
                     url=purchase.url, tracks=self._tracklist(purchase))

    def tracks(self, target: Target) -> list[Track]:
        found = []
        # Songs bought on their own,
        for purchase in self._named(target.title, target.artist, albums=False):
            listed = self._tracklist(purchase)
            found += listed[:1]
        # then the song on an album of the artist's you bought.
        mine = [p for p in self.collection() if p.is_album and similarity(target.artist, p.artist) >= 0.8]
        mine.sort(key=lambda p: similarity(target.album, p.title) if target.album else 0, reverse=True)
        for purchase in mine[:5]:
            found += [t for t in self._tracklist(purchase) if similarity(target.title, t.title) >= 0.6]
        return found

    def _named(self, title: str, artist: str, albums: bool) -> list[Purchase]:
        scored = [(similarity(title, p.title) + similarity(artist, p.artist), p) for p in self.collection()
                  if p.is_album == albums and similarity(artist, p.artist) >= 0.6 and similarity(title, p.title) >= 0.5]
        scored.sort(key=lambda x: x[0], reverse=True)
        return [p for _, p in scored[:5]]

    def _tracklist(self, purchase: Purchase) -> list[Track]:
        """The tracks on the purchase's page. Their IDs say which purchase and which track number."""
        response = self.http.get(purchase.url)
        response.raise_for_status()
        tralbum = _attribute(response.text, "data-tralbum")
        if tralbum is None:
            raise RuntimeError(f"Couldn't read the tracklist on {purchase.url}.")
        album = (tralbum.get("current") or {}).get("title") if purchase.is_album else purchase.album
        tracks = []
        for number, info in enumerate(tralbum.get("trackinfo") or [], 1):
            position = info.get("track_num") or number
            tracks.append(Track(
                id=f"{purchase.id}:{position}",
                title=info.get("title") or "",
                artists=[info.get("artist") or purchase.artist],
                duration=info.get("duration") or None,
                disc=1,
                position=position,
                album=album or purchase.album,
                album_id=purchase.id if purchase.is_album else None,
                offers=self._offers(),
            ))
        return tracks

    def _offers(self) -> list[Offer]:
        """The one format the settings choose; Bandcamp makes any of them from the artist's upload."""
        label = {"flac": "FLAC", "alac": "ALAC", "wav": "WAV", "aiff-lossless": "AIFF", "mp3-320": "MP3 320",
                 "mp3-v0": "MP3 V0", "aac-hi": "AAC", "vorbis": "Ogg Vorbis"}[self.choice]
        # V0 is variable; about 245 kbps on most music.
        bitrate = {"mp3-320": 320, "mp3-v0": 245, "aac-hi": 256}.get(self.choice)
        return [Offer(self.choice, self.format, label, bitrate=bitrate)]

    # ---- Fetching ----

    def fetch(self, job: Job, chosen: list[list[Any]], directory: Path, downloads: Path) -> list[dict[str, Any]]:
        """Downloads each purchase the chosen tracks are on, once, and copies the chosen tracks from it.

        Downloads are kept in the downloads folder for a while: Melodex asks for the missing songs of an album you have
        some of one at a time, and each would otherwise download the whole album again."""
        wanted: dict[str, dict[int, tuple[int, int, str]]] = {}
        for disc, position, track_id, title in chosen:
            purchase, number = track_id.rsplit(":", 1)
            wanted.setdefault(purchase, {})[int(number)] = (disc, position, title)
        purchases = {p.id: p for p in self.collection()}
        written = []
        for index, (purchase_id, tracks) in enumerate(wanted.items()):
            job.check()
            purchase = purchases.get(purchase_id)
            if purchase is None or not purchase.download:
                raise RuntimeError("That purchase isn't in your Bandcamp collection any more.")

            def moved(fraction: float, index: int = index) -> None:
                job.progress = min(0.99, (index + fraction) / len(wanted))

            files = self._kept(job, purchase, downloads, moved)
            for number, (disc, position, title) in sorted(tracks.items()):
                source = files.get(number) if purchase.is_album else next(iter(files.values()), None)
                if source is None:
                    raise RuntimeError(f"Bandcamp's download of {purchase.title} has no track {number}.")
                path = directory / f"{disc}-{position:02} {_safe(title)}{source.suffix.lower()}"
                path.unlink(missing_ok=True)
                try:
                    os.link(source, path)
                except OSError:
                    shutil.copyfile(source, path)
                written.append(probe(path, directory, disc, position))
        return written

    def _kept(self, job: Job, purchase: Purchase, downloads: Path, moved) -> dict[int, Path]:
        """The purchase's files, from an earlier download if it's recent, or downloaded now."""
        folder = downloads / f"{purchase.id}-{self.choice}"
        with _lock:
            lock = _download_locks.setdefault(folder.name, threading.Lock())
        with lock:
            _forget_downloads(downloads)
            complete = folder / ".complete"
            if complete.exists():
                return _numbered([f for f in folder.iterdir() if f.suffix.lower() in _AUDIO])
            shutil.rmtree(folder, ignore_errors=True)
            folder.mkdir(parents=True)
            try:
                files = self._download(job, purchase, folder, moved)
            except BaseException:
                shutil.rmtree(folder, ignore_errors=True)
                raise
            complete.touch()
            return files

    def _download(self, job: Job, purchase: Purchase, folder: Path, moved) -> dict[int, Path]:
        """The purchase's files in the chosen format, by track number."""
        page = self.http.get(purchase.download)
        if page.status_code in (401, 403):
            raise PermissionError("Bandcamp refused the download page; the identity cookie may have run out.")
        page.raise_for_status()
        blob = _attribute(page.text, "data-blob")
        items = (blob or {}).get("digital_items") or (blob or {}).get("download_items") or []
        downloads = (items[0].get("downloads") if items else None) or {}
        if self.choice not in downloads:
            offered = ", ".join(downloads) or "none"
            raise RuntimeError(f"Bandcamp doesn't offer {purchase.title} as {self.choice} (it offers {offered}).")
        url = self._prepared(downloads[self.choice]["url"])

        archive = folder / "download"
        with self.http.stream("GET", url) as response:
            if response.status_code in (401, 403):
                raise PermissionError("Bandcamp refused the download; the link may have run out. Try again.")
            response.raise_for_status()
            name = _filename(response.headers.get("content-disposition", "")) or ""
            total = int(response.headers.get("content-length") or 0)
            done = 0
            with archive.open("wb") as out:
                for chunk in response.iter_bytes(1 << 16):
                    job.check()
                    out.write(chunk)
                    done += len(chunk)
                    if total:
                        moved(done / total)
        if zipfile.is_zipfile(archive):
            with zipfile.ZipFile(archive) as zipped:
                for member in zipped.infolist():
                    if Path(member.filename).suffix.lower() in _AUDIO and not member.is_dir():
                        # Flattened, so nothing in the archive can write outside the folder.
                        target = folder / Path(member.filename).name
                        with zipped.open(member) as source, target.open("wb") as out:
                            while chunk := source.read(1 << 16):
                                out.write(chunk)
            archive.unlink()
            files = [f for f in folder.iterdir() if f.suffix.lower() in _AUDIO]
        else:
            single = folder / f"track{Path(name).suffix.lower() or _SUFFIXES[self.choice]}"
            archive.rename(single)
            files = [single]
        return _numbered(files)

    def _prepared(self, url: str) -> str:
        """Bandcamp zips an album when it's first asked for; its status link says when the zip is ready."""
        status = url.replace("/download/", "/statdownload/", 1)
        if status == url:
            return url
        for _ in range(30):
            try:
                response = self.http.get(f"{status}{'&' if '?' in status else '?'}.vrs=1")
                text = response.text
            except httpx.HTTPError:
                return url
            if (ready := re.search(r'"download_url"\s*:\s*"([^"]+)"', text)) and '"ok"' in text:
                return ready.group(1).replace("\\/", "/")
            if '"result"' not in text:
                return url
            time.sleep(2)
        raise RuntimeError("Bandcamp didn't finish preparing the download; try again later.")


def _numbered(files: list[Path]) -> dict[int, Path]:
    numbered: dict[int, Path] = {}
    for path in sorted(files):
        # Bandcamp names them "Artist - Album - 01 Title.flac".
        match = re.search(r" - (\d+) ", path.name) or re.match(r"(\d+)[ .-]", path.name)
        numbered[int(match.group(1)) if match else len(numbered) + 1] = path
    return numbered


def _forget_downloads(downloads: Path) -> None:
    """Removes downloads kept longer than they're needed."""
    if not downloads.is_dir():
        return
    for folder in downloads.iterdir():
        complete = folder / ".complete"
        if complete.exists() and time.time() - complete.stat().st_mtime > _KEEP_DOWNLOADS_SECONDS:
            shutil.rmtree(folder, ignore_errors=True)


def _purchase(item: dict[str, Any], links: dict[str, str]) -> Purchase | None:
    kind = item.get("tralbum_type") or ("a" if item.get("item_type") in ("album", "package") else "t")
    number = item.get("tralbum_id") or item.get("item_id")
    if kind not in ("a", "t") or not number or not item.get("item_url"):
        return None
    download = links.get(f"{item.get('sale_item_type')}{item.get('sale_item_id')}")
    return Purchase(id=f"{kind}{number}", title=item.get("item_title") or item.get("album_title") or "",
                    artist=item.get("band_name") or "", url=item["item_url"], download=download,
                    album=item.get("album_title"))


def _attribute(page: str, name: str) -> dict[str, Any] | None:
    """A JSON attribute of Bandcamp's pages, such as data-tralbum or data-blob."""
    if (match := re.search(rf'{name}="([^"]*)"', page)) is None:
        return None
    try:
        return json.loads(html.unescape(match.group(1)))
    except ValueError:
        return None


def _filename(disposition: str) -> str | None:
    if match := re.search(r"filename\*=UTF-8''([^;]+)", disposition):
        return unquote(match.group(1))
    if match := re.search(r'filename="?([^";]+)"?', disposition):
        return match.group(1)
    return None


def _safe(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", name).strip()[:120] or "track"
