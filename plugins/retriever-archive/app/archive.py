"""Finding requested music in the Internet Archive's collections, and downloading it.

An item is an album or a concert; its audio files are the tracks. Each original file comes with copies the Archive
made from it (VBR MP3, Ogg Vorbis), so a track is an original and its copies, and the format setting picks which one
is fetched.
"""

from __future__ import annotations

import json
import re
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any
from urllib.parse import quote

import httpx

from .contract import Candidate, Job, Kind, Target, probe
from .matching import duration_score, similarity, track_score

API = "https://archive.org"
USER_AGENT = "Melodex-retriever-archive/0.1 (+https://github.com/dextrd/Melodex)"

FORMATS = ["flac", "best", "mp3", "ogg"]
DEFAULT_COLLECTIONS = "etree, netlabels"

# The Archive's format names, by what the format setting calls them. Shorten (the Live Music Archive's old lossless
# format) isn't offered: hardly anything plays it.
_LOSSLESS = {"Flac", "24bit Flac"}
_MP3 = {"VBR MP3", "MP3", "320Kbps MP3", "256Kbps MP3", "192Kbps MP3", "128Kbps MP3", "64Kbps MP3"}
_OGG = {"Ogg Vorbis"}
_AUDIO = _LOSSLESS | _MP3 | _OGG | {"Shorten"}

_LUCENE = re.compile(r'([+\-!(){}\[\]^"~*?:\\/]|&&|\|\|)')
# MusicBrainz writes dates in titles with a Unicode hyphen ("1977‐05‐08").
_DATE = re.compile(r"\b(\d{4})[-‐‑–./](\d{2})[-‐‑–./](\d{2})\b")
# Tapers mark a song that runs into the next with "->" or ">" after its title.
_SEGUE = re.compile(r"\s*-?>\s*$")


def client() -> httpx.Client:
    """The HTTP client every call goes through; tests swap in a fake one."""
    return httpx.Client(base_url=API, headers={"User-Agent": USER_AGENT}, timeout=30, follow_redirects=True)


def collections(settings: dict[str, Any]) -> list[str]:
    raw = settings.get("collections") or DEFAULT_COLLECTIONS
    return [c for c in re.split(r"[\s,]+", str(raw)) if c]


def escape(text: str) -> str:
    return _LUCENE.sub(r"\\\1", text)


def items(http: httpx.Client, settings: dict[str, Any], query: str, rows: int) -> list[str]:
    """Identifiers of downloadable audio items in the chosen collections that match the query, best first."""
    q = (f"collection:({' OR '.join(collections(settings))}) AND mediatype:(audio OR etree) "
         f"AND -collection:(stream_only) AND ({query})")
    response = http.get("/advancedsearch.php", params={"q": q, "fl[]": "identifier", "rows": rows, "output": "json"})
    response.raise_for_status()
    return [d["identifier"] for d in response.json()["response"]["docs"]]


def metadata(http: httpx.Client, identifier: str) -> dict[str, Any] | None:
    response = http.get(f"/metadata/{quote(identifier)}")
    response.raise_for_status()
    item = response.json()
    # Stream-only items list their files, but don't let them be downloaded.
    if not item or item.get("metadata", {}).get("access-restricted-item") in ("true", True):
        return None
    return item


def seconds(length: Any) -> float | None:
    """The Archive writes lengths as seconds ("250.5") or as minutes and seconds ("04:10")."""
    if length in (None, ""):
        return None
    try:
        parts = [float(p) for p in str(length).split(":")]
    except ValueError:
        return None
    total = 0.0
    for part in parts:
        total = total * 60 + part
    return total or None


def _number(text: Any) -> int | None:
    match = re.match(r"\s*(\d+)", str(text or ""))
    return int(match.group(1)) if match else None


def tracks(item: dict[str, Any], format: str) -> list[dict[str, Any]]:
    """The item's tracks in order, each with the file the format setting picks; tracks it has no such file for are left out."""
    files = [f for f in item.get("files", []) if f.get("format") in _AUDIO and not f.get("private")]
    originals = [f for f in files if f.get("source") != "derivative"]
    copies: dict[str, list[dict[str, Any]]] = {}
    for f in files:
        if f.get("source") == "derivative" and f.get("original"):
            copies.setdefault(f["original"], []).append(f)

    album = item.get("metadata", {})
    result = []
    for original in originals:
        group = [original] + copies.get(original["name"], [])
        pick = _pick(group, format)
        if pick is None:
            continue
        def first(key: str) -> Any:
            return next((f.get(key) for f in group if f.get(key)), None)
        result.append({
            "name": pick["name"],
            "format": "flac" if pick["format"] in _LOSSLESS else "mp3" if pick["format"] in _MP3 else "ogg",
            "size": int(pick.get("size") or 0),
            "title": _SEGUE.sub("", first("title") or Path(original["name"]).stem),
            "artist": first("creator") or _text(album.get("creator")),
            "track": _number(first("track")),
            "length": seconds(first("length")),
        })
    # In track order where the files say, then by name, which is how tapers number them.
    result.sort(key=lambda t: (t["track"] is None, t["track"] or 0, t["name"]))
    return result


def _pick(group: list[dict[str, Any]], format: str) -> dict[str, Any] | None:
    by = {
        "flac": [_LOSSLESS],
        "best": [_LOSSLESS, {"VBR MP3"}, _MP3, _OGG],
        "mp3": [{"VBR MP3"}, _MP3],
        "ogg": [_OGG],
    }[format]
    for formats in by:
        if found := next((f for f in group if f.get("format") in formats), None):
            return found
    return None


def _text(value: Any) -> str:
    return "; ".join(value) if isinstance(value, list) else str(value or "")


def _bitrate(track: dict[str, Any]) -> int | None:
    return round(track["size"] * 8 / track["length"] / 1000) if track["size"] and track["length"] else None


def _date(text: str | None) -> str | None:
    match = _DATE.search(text or "")
    return "-".join(match.groups()) if match else None


# ---- Search ----

def search(target: Target, limit: int, settings: dict[str, Any]) -> list[Candidate]:
    format = settings.get("format", "flac")
    min_score = float(settings.get("minScore", 0.6))
    with client() as http:
        artist = f'creator:("{escape(target.artist)}")'
        if target.kind == Kind.album:
            # A concert is found by its date when the album's title has one, as live recordings' titles usually do.
            date = _date(target.album or target.title)
            found = items(http, settings, f"{artist} AND date:{date}" if date else f"{artist} AND ({escape(target.title)})", 10)
        else:
            found = items(http, settings, f'{artist} AND ("{escape(target.title)}")', 8)
        with ThreadPoolExecutor(max_workers=4) as pool:
            fetched = [m for m in pool.map(lambda i: metadata(http, i), found) if m]

    candidates: list[Candidate] = []
    for item in fetched:
        listed = tracks(item, format)
        if not listed:
            continue
        candidates += _album(target, item, listed) if target.kind == Kind.album else _tracks(target, item, listed)
    candidates = [c for c in candidates if (c.score or 0) >= min_score]
    candidates.sort(key=lambda c: c.score or 0, reverse=True)
    return candidates[:limit]


def _candidate(item: dict[str, Any], kind: Kind, title: str, chosen: list[tuple[int | None, int | None, dict[str, Any]]],
               score: float) -> Candidate:
    meta = item["metadata"]
    lengths = [t["length"] for _, _, t in chosen]
    rates = [r for _, _, t in chosen if (r := _bitrate(t))]
    return Candidate(
        candidate_id=json.dumps({"i": meta["identifier"], "f": [[d, p, t["name"], t["size"]] for d, p, t in chosen]}),
        kind=kind,
        title=title,
        artist=_text(meta.get("creator")) or None,
        # An album candidate's title is already the item's.
        album=None if kind == Kind.album else _text(meta.get("title")) or None,
        duration_seconds=round(sum(lengths), 1) if all(lengths) else None,
        source="Internet Archive",
        url=f"{API}/details/{meta['identifier']}",
        format=chosen[0][2]["format"],
        bitrate_kbps=round(sum(rates) / len(rates)) if rates else None,
        score=round(score, 3),
    )


def _tracks(target: Target, item: dict[str, Any], listed: list[dict[str, Any]]) -> list[Candidate]:
    scored = [(track_score(target.title, target.artist, target.duration_seconds, t["title"], [t["artist"]], t["length"]), t)
              for t in listed]
    scored.sort(key=lambda x: x[0], reverse=True)
    return [_candidate(item, Kind.track, t["title"], [(None, None, t)], score) for score, t in scored[:2]]


def _album(target: Target, item: dict[str, Any], listed: list[dict[str, Any]]) -> list[Candidate]:
    """The item as the requested album, offered only when it has a track for every requested one."""
    meta = item["metadata"]
    wanted = target.tracks or []
    used: set[str] = set()
    chosen = []
    matches = []
    for want in wanted:
        best = max(((0.6 * similarity(want.title, t["title"]) + 0.4 * duration_score(want.duration_seconds, t["length"]), t)
                    for t in listed if t["name"] not in used), key=lambda x: x[0], default=None)
        if best is None or best[0] < 0.5:
            return []
        used.add(best[1]["name"])
        chosen.append((want.disc, want.position, best[1]))
        matches.append(best[0])
    title_match = max(similarity(target.title, _text(meta.get("title"))),
                      similarity(target.title, next((t.get("album") for t in item.get("files", []) if t.get("album")), "")))
    if (date := _date(target.album or target.title)) and date == str(meta.get("date", ""))[:10]:
        title_match = 1.0
    if not wanted:
        # With no tracklist to check, only the title says it's the album.
        if title_match < 0.85:
            return []
        chosen = [(1, i + 1, t) for i, t in enumerate(listed)]
        matches = [0.7]
    artist_match = similarity(target.artist, _text(meta.get("creator")))
    score = 0.3 * title_match + 0.25 * artist_match + 0.45 * sum(matches) / len(matches)
    return [_candidate(item, Kind.album, _text(meta.get("title")) or target.title, chosen, score)]


# ---- Fetch ----

def fetch(job: Job, candidate_id: str, directory: Path, settings: dict[str, Any]) -> list[dict[str, Any]]:
    chosen = json.loads(candidate_id)
    identifier, files = chosen["i"], chosen["f"]
    total = sum(size for _, _, _, size in files) or None
    done = 0
    written = []
    with client() as http:
        for disc, position, name, size in files:
            job.check()
            path = directory / _safe(Path(name).name)
            with http.stream("GET", f"/download/{quote(identifier)}/{quote(name)}") as response:
                if response.status_code in (401, 403):
                    raise RuntimeError(f"The Internet Archive doesn't allow {name} to be downloaded.")
                response.raise_for_status()
                with path.open("wb") as out:
                    for chunk in response.iter_bytes(1 << 16):
                        job.check()
                        out.write(chunk)
                        done += len(chunk)
                        if total:
                            job.progress = min(0.99, done / total)
            if not total:
                job.progress = len(written) / len(files)
            written.append(probe(path, directory, disc, position))
    return written


def _safe(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", name).strip() or "track"
