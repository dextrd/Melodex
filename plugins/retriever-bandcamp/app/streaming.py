"""What a streaming service's retrieval plugin does whichever service it is: finding the requested album or song among
what the service lists, and downloading the tracks it streams.

Shared by the Qobuz, Tidal and Bandcamp plugins; each gives it a Service that answers in these terms. Bandcamp only
searches with it: its downloads are whole purchases, not streams.
"""

from __future__ import annotations

import json
import re
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol

import httpx

from .contract import Candidate, Job, Kind, Target, probe
from .matching import duration_score, similarity, track_score

# How many albums found by searching have their tracklists read.
_CHECKED = 5


@dataclass(frozen=True)
class Offer:
    """One quality a service has something in: what the core ranks it by, and the key fetching asks for it with."""
    key: str
    format: str
    label: str
    bit_depth: int | None = None
    sample_rate: int | None = None
    bitrate: int | None = None


@dataclass
class Track:
    id: str
    title: str
    artists: list[str]
    duration: float | None
    disc: int
    position: int
    isrc: str | None = None
    album: str | None = None
    album_id: str | None = None
    offers: list[Offer] = field(default_factory=list)


@dataclass
class Album:
    id: str
    title: str
    artist: str
    upc: str | None = None
    track_count: int | None = None
    offers: list[Offer] = field(default_factory=list)
    url: str | None = None
    tracks: list[Track] | None = None


@dataclass
class Stream:
    """Where a track's audio is: one file, or a DASH initialisation segment and the segments after it."""
    urls: list[str]
    suffix: str
    # Segments of fragmented MP4, joined and then put in the suffix's container: FLAC is decoded and encoded again,
    # which gives the same samples and a header that knows the length; anything else is copied as it is.
    remux: bool = False
    headers: dict[str, str] = field(default_factory=dict)


class Service(Protocol):
    name: str
    format: str

    def albums(self, target: Target) -> list[Album]: ...

    def album(self, album_id: str) -> Album: ...

    def tracks(self, target: Target) -> list[Track]: ...

    def stream(self, track_id: str, quality: str | None) -> Stream: ...


# ---- Search ----

_LOSSLESS = {"flac", "alac", "wav", "aiff"}

def _upc(value: str | None) -> str:
    return (value or "").strip().lstrip("0")


def search(service: Service, target: Target, limit: int, settings: dict[str, Any]) -> list[Candidate]:
    min_score = float(settings.get("minScore", 0.6))
    if target.kind == Kind.album:
        candidates = _albums(service, target)
    else:
        candidates = _tracks(service, target)
    candidates = [c for c in candidates if (c.score or 0) >= min_score]
    candidates.sort(key=lambda c: c.score or 0, reverse=True)
    return candidates[:limit]


def _albums(service: Service, target: Target) -> list[Candidate]:
    found = service.albums(target)
    upc = _upc(target.id("upc"))
    # The edition with the requested barcode first, then the best-named.
    found.sort(key=lambda a: (bool(upc) and _upc(a.upc) == upc,
                              similarity(target.title, a.title) + similarity(target.artist, a.artist)), reverse=True)
    candidates = []
    seen: set[str] = set()
    for listed in found:
        if listed.id in seen or len(seen) >= _CHECKED:
            continue
        seen.add(listed.id)
        album = listed if listed.tracks is not None else service.album(listed.id)
        candidates += _album(service, target, album)
    return candidates


def match_album(target: Target, tracks: list[Track]) -> tuple[list[tuple[int, int, Track]], float] | None:
    """The album's track for every requested one, and how well they match; None when one is missing."""
    used: set[str] = set()
    chosen, scores = [], []
    for want in target.tracks or []:
        isrcs = {i.value.upper() for i in want.ids or [] if i.provider == "isrc"}

        def score(t: Track) -> float:
            if t.isrc and t.isrc.upper() in isrcs:
                return 1.0
            position = 1.0 if (t.disc, t.position) == (want.disc, want.position) else 0.0
            return (0.6 * similarity(want.title, t.title) + 0.15 * position
                    + 0.25 * duration_score(want.duration_seconds, t.duration))

        best = max(((score(t), t) for t in tracks if t.id not in used), key=lambda x: x[0], default=None)
        if best is None or best[0] < 0.6:
            return None
        used.add(best[1].id)
        chosen.append((want.disc, want.position, best[1]))
        scores.append(best[0])
    if not chosen:
        return None
    return chosen, sum(scores) / len(scores)


def _album(service: Service, target: Target, album: Album) -> list[Candidate]:
    tracks = album.tracks or []
    if not target.tracks:
        # With no tracklist to check, only the title says it's the album.
        if similarity(target.title, album.title) < 0.85 or not tracks:
            return []
        chosen, track_match = [(t.disc, t.position, t) for t in tracks], 0.7
    elif (matched := match_album(target, tracks)) is None:
        return []
    else:
        chosen, track_match = matched
    score = 0.3 * similarity(target.title, album.title) + 0.2 * similarity(target.artist, album.artist) + 0.5 * track_match
    if (upc := _upc(target.id("upc"))) and _upc(album.upc) == upc:
        score = max(score, 0.99)
    elif target.tracks and len(tracks) != len(target.tracks):
        # Another edition with every requested track, but not the same one.
        score *= 0.97
    return _candidates(service, Kind.album, album.title, album.artist, None, album.offers, album.url, chosen, score)


def _tracks(service: Service, target: Target) -> list[Candidate]:
    isrc = (target.id("isrc") or "").upper()
    scored = []
    seen: set[str] = set()
    for track in service.tracks(target):
        if track.id in seen:
            continue
        seen.add(track.id)
        if isrc and (track.isrc or "").upper() == isrc:
            score = 1.0
        else:
            score = track_score(target.title, target.artist, target.duration_seconds, track.title, track.artists,
                                track.duration)
            if target.album:
                # The same song on the album asked for, rather than a compilation.
                score = 0.9 * score + 0.1 * similarity(target.album, track.album)
        scored.append((score, track))
    scored.sort(key=lambda x: x[0], reverse=True)
    return [c for score, t in scored[:3] for c in _candidates(service, Kind.track, t.title, ", ".join(t.artists), t.album,
                                                              t.offers, None, [(t.disc, t.position, t)], score)]


def _candidates(service: Service, kind: Kind, title: str, artist: str | None, album: str | None, offers: list[Offer],
                url: str | None, chosen: list[tuple[int, int, Track]], score: float) -> list[Candidate]:
    """A candidate for each quality it's offered in, so the core can choose by the user's quality profile."""
    lengths = [t.duration for _, _, t in chosen]
    tracks = [[d, p, t.id, t.title] for d, p, t in chosen]
    return [Candidate(
        candidate_id=json.dumps({"t": tracks, "q": offer.key} if offer else {"t": tracks}),
        kind=kind,
        title=title,
        artist=artist or None,
        album=album,
        duration_seconds=round(sum(lengths), 1) if all(lengths) else None,
        source=f"{service.name} ({offer.label})" if offer else service.name,
        url=url,
        format=offer.format if offer else service.format,
        bitrate_kbps=offer.bitrate if offer else None,
        bit_depth=offer.bit_depth if offer else None,
        sample_rate_hz=offer.sample_rate if offer else None,
        # Lossless straight from the store, not from someone's copy.
        vouched=True if (offer.format if offer else service.format) in _LOSSLESS else None,
        score=round(score, 3),
    ) for offer in offers or [None]]


# ---- Fetch ----

def fetch(job: Job, service: Service, candidate_id: str, directory: Path, download: httpx.Client) -> list[dict[str, Any]]:
    parsed = json.loads(candidate_id)
    chosen, quality = parsed["t"], parsed.get("q")
    written = []
    for index, (disc, position, track_id, title) in enumerate(chosen):
        job.check()
        stream = service.stream(track_id, quality)
        name = f"{disc}-{position:02} {_safe(title)}"
        path = directory / f"{name}{stream.suffix}"

        def moved(fraction: float, index: int = index) -> None:
            job.progress = min(0.99, (index + fraction) / len(chosen))

        if stream.remux:
            joined = directory / f"{name}.part.mp4"
            _download(job, download, stream, joined, moved)
            _remux(joined, path)
        else:
            _download(job, download, stream, path, moved)
        written.append(probe(path, directory, disc, position))
    return written


def _download(job: Job, http: httpx.Client, stream: Stream, path: Path, moved) -> None:
    try:
        with path.open("wb") as out:
            for number, url in enumerate(stream.urls):
                with http.stream("GET", url, headers=stream.headers) as response:
                    if response.status_code in (401, 403):
                        raise RuntimeError(f"The service refused the download ({response.status_code}); the link may "
                                           "have expired.")
                    response.raise_for_status()
                    total = int(response.headers.get("content-length") or 0)
                    done = 0
                    for chunk in response.iter_bytes(1 << 16):
                        job.check()
                        out.write(chunk)
                        done += len(chunk)
                        # One file reports its bytes; segments report how many are done.
                        if len(stream.urls) == 1 and total:
                            moved(done / total)
                if len(stream.urls) > 1:
                    moved((number + 1) / len(stream.urls))
    except BaseException:
        path.unlink(missing_ok=True)
        raise


def _remux(source: Path, target: Path) -> None:
    try:
        codec = "flac" if target.suffix == ".flac" else "copy"
        result = subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source), "-map", "0:a", "-map_metadata", "-1",
                                 "-c:a", codec, str(target)], capture_output=True, text=True)
    finally:
        source.unlink(missing_ok=True)
    if result.returncode != 0:
        target.unlink(missing_ok=True)
        raise RuntimeError(f"Couldn't take the audio out of the stream: {result.stderr.strip()[-300:]}")


def _safe(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", name).strip()[:120] or "track"
