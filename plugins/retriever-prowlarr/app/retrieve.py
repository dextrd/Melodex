"""Finding a requested album or song among the torrents Prowlarr's indexers offer, and fetching it with qBittorrent.

A torrent usually holds a whole album, so a song is fetched by downloading just its file. Before offering a torrent,
its file list is read from the .torrent file, and it's offered only when every requested track is in it. Torrents
that come only as a magnet link can't be checked until qBittorrent has their file list, so they rank lower and are
checked when fetched.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path, PurePosixPath
from typing import Any

from . import reflinks
from . import torrent as torrents
from .clients import Prowlarr, QBittorrent, without_key
from .contract import Candidate, Job, Kind, Target, probe
from .matching import normalise, similarity, track_score, version_penalty

FORMATS = ["best", "flac", "mp3"]
AFTER = ["keep", "remove"]
LOSSLESS = {".flac"}
LOSSY = {".mp3", ".m4a", ".ogg", ".opus", ".aac"}
POLL_SECONDS = 2.0

_CHECKED = 6  # torrents whose file lists are read per search
_GLUED = re.compile(r"(?<=\d)(?=[^\W\d])|(?<=[^\W\d])(?=\d)")
_TAPER = re.compile(r"d(\d{1,2})t(\d{2,3})", re.IGNORECASE)
_DATE = re.compile(r"\b(\d{4})[-‐‑–./](\d{2})[-‐‑–./](\d{2})\b")
_DISC = re.compile(r"\b(?:cd|disc|disk)\s*(\d+)\b", re.IGNORECASE)


# ---- What's in a torrent ----

def audio(files: list[torrents.TorrentFile], format: str) -> list[torrents.TorrentFile]:
    """The torrent's audio files in the format the setting picks. A torrent can hold an album in FLAC and in MP3."""
    lossless = [f for f in files if PurePosixPath(f.path).suffix.lower() in LOSSLESS]
    lossy = [f for f in files if PurePosixPath(f.path).suffix.lower() in LOSSY]
    if format == "flac":
        return lossless
    if format == "mp3":
        return [f for f in lossy if f.path.lower().endswith(".mp3")]
    return lossless or lossy


def format_of(files: list[torrents.TorrentFile]) -> str | None:
    suffixes = {PurePosixPath(f.path).suffix.lower().lstrip(".") for f in files}
    return suffixes.pop() if len(suffixes) == 1 else None


_CODECS = [("flac", "flac"), ("alac", "alac"), ("wav", "wav"), ("aiff", "aiff"), ("mp3", "mp3"), ("aac", "aac"),
           ("m4a", "aac"), ("opus", "opus"), ("ogg", "vorbis"), ("vorbis", "vorbis")]
_DEPTH = re.compile(r"\b(16|24|32)[ -]?bits?\b|\b(16|24)\s*[/-]\s*(44(?:[.,]1)?|48|88(?:[.,]2)?|96|176(?:[.,]4)?|192)\b",
                    re.IGNORECASE)
_RATE = re.compile(r"\b(44[.,]1|48|88[.,]2|96|176[.,]4|192)\s*khz\b", re.IGNORECASE)
_KBPS = re.compile(r"\b(?:(v0)|(v2)|(320|256|192|160|128)(?:\s*k(?:bps)?|\b))", re.IGNORECASE)


def quality_of_name(name: str) -> dict[str, Any]:
    """The quality a release's name says it is, for when its files aren't known yet: "FLAC 24bit-96kHz" is 24-bit/96
    kHz FLAC, "MP3 V0" about 245 kbps. Only what the name says; the core reads the files once they're here."""
    words = set(re.findall(r"[a-z0-9]+", name.lower()))
    quality: dict[str, Any] = {}
    if codec := next((c for word, c in _CODECS if word in words), None):
        quality["format"] = codec
    if depth := _DEPTH.search(name):
        quality["bit_depth"] = int(depth.group(1) or depth.group(2))
        if depth.group(3):
            quality["sample_rate_hz"] = round(float(depth.group(3).replace(",", ".")) * 1000)
    elif re.search(r"\bhi[ -]?res\b", name, re.IGNORECASE):
        quality["bit_depth"] = 24
    if rate := _RATE.search(name):
        quality["sample_rate_hz"] = round(float(rate.group(1).replace(",", ".")) * 1000)
    if quality.get("format") in (None, "mp3", "aac", "opus", "vorbis") and (kbps := _KBPS.search(name)):
        quality["bitrate_kbps"] = 245 if kbps.group(1) else 190 if kbps.group(2) else int(kbps.group(3))
        quality.setdefault("format", "mp3")
    if "bit_depth" in quality and "format" not in quality:
        quality["format"] = "flac"
    return quality


def describe(path: str, artist: str = "") -> tuple[int | None, int | None, str]:
    """The disc, number and title a file's name and folder suggest: "CD2/03 - Title.flac" is disc 2, track 3, and
    the Live Music Archive's "gd77-05-08d1t03.flac" is disc 1, track 3."""
    parts = PurePosixPath(path)
    disc = next((int(m.group(1)) for d in reversed(parts.parent.parts) if (m := _DISC.search(d))), None)
    stem = parts.stem
    if taper := _TAPER.search(stem):
        return int(taper.group(1)), int(taper.group(2)), stem[taper.end():]
    # "wh285_05EugeneKha_-_Norder_Remix2014": numbers glued to words are words of their own.
    number = None
    # "01-1127907-CHASMA-Title": a number at the start, then a separator.
    if leading := re.match(r"(\d{1,2})(?:[-._]\s*|\s+)(?!\d{1,2}\b)", stem):
        number, stem = int(leading.group(1)), stem[leading.end():]
    words = _GLUED.sub(" ", stem.replace("_", " ")).split()
    for i, word in enumerate(words[:4] if number is None else []):
        if pair := re.fullmatch(r"(\d)[-.](\d{1,2})\.?", word):
            disc, number = disc or int(pair.group(1)), int(pair.group(2))
        elif re.fullmatch(r"\d{1,2}\.?", word):
            number = int(word.rstrip("."))
        else:
            continue
        words = words[i + 1:]
        break
    title = " ".join(words)
    if artist and (name := normalise(artist)):
        title = re.sub(rf"(?i)^\W*{re.escape(artist)}\W+-\W*|^\W*{re.escape(name)}\s+-\s+", "", title)
    return disc, number, title.strip(" -.")


def match_album(target: Target, files: list[torrents.TorrentFile]) -> tuple[list[tuple[int, int, torrents.TorrentFile]], float] | None:
    """Each requested track's file, and how well they match on average; None when a track is missing."""
    wanted = target.tracks or []
    if not wanted:
        return None
    described = [(f, *describe(f.path, target.artist)) for f in files]
    numbered = len(files) == len(wanted) and all(n is not None for _, _, n, _ in described)
    chosen, scores, used = [], [], set()
    for want in wanted:
        best, best_score = None, 0.0
        for f, disc, number, title in described:
            if f.index in used:
                continue
            same_place = number == want.position and (disc is None or disc == want.disc)
            score = 0.75 * similarity(want.title, title) + (0.25 if same_place else 0)
            # Files named only by number ("Track 03.flac") fit when the torrent holds just the requested tracks.
            if numbered and same_place and not normalise(title).replace("track", "").strip():
                score = max(score, 0.75)
            if score > best_score:
                best, best_score = f, score
        if best is None or best_score < 0.6:
            return None
        used.add(best.index)
        chosen.append((want.disc, want.position, best))
        scores.append(min(1.0, best_score))
    return chosen, sum(scores) / len(scores)


def match_track(target: Target, name: str, files: list[torrents.TorrentFile]) -> tuple[torrents.TorrentFile, float] | None:
    """The file that's the requested song, and how well it matches."""
    scored = [(f, track_score(target.title, target.artist, None, describe(f.path, target.artist)[2], [name, f.path], None))
              for f in files]
    return max(scored, key=lambda s: s[1], default=None)


# ---- Search ----

def title_score(target: Target, name: str) -> float:
    """How well a torrent's name fits the request before its files are looked at."""
    wanted = target.album or target.title if target.kind == Kind.album else target.album or ""
    album = similarity(wanted, name) if wanted else 0.5
    artist = similarity(target.artist, name)
    return 0.6 * album + 0.4 * artist


def query(artist: str, title: str) -> str:
    """What's searched for: the artist and the title's words, or the date when the title has one, as concerts' do."""
    if date := _DATE.search(title):
        return f"{artist} {'-'.join(date.groups())}"
    return " ".join(re.findall(r"\w+", f"{artist} {title}"))


def search(target: Target, limit: int, settings: dict[str, Any]) -> list[Candidate]:
    format = settings.get("format", "best")
    min_score = float(settings.get("minScore", 0.6))
    min_seeders = int(settings.get("minSeeders", 1))
    queries = [query(target.artist, target.title)]
    if target.kind == Kind.track and target.album:
        queries.insert(0, query(target.artist, target.album))

    with Prowlarr(settings) as prowlarr:
        releases: dict[str, dict[str, Any]] = {}
        for text in queries:
            for release in prowlarr.search(text):
                if (release.get("seeders") or 0) >= min_seeders:
                    releases.setdefault(release.get("infoHash") or release.get("guid") or release["title"], release)
        ranked = sorted(releases.values(), key=lambda r: (title_score(target, r["title"]), r.get("seeders") or 0), reverse=True)
        with ThreadPoolExecutor(max_workers=3) as pool:
            checked = list(pool.map(lambda r: (r, _files(prowlarr, r)), ranked[:_CHECKED]))

    candidates = []
    for release, found in checked:
        if (candidate := _candidate(target, release, found, format)) and (candidate.score or 0) >= min_score:
            candidates.append(candidate)
    # Between equally good matches, lossless first, then the better-seeded.
    candidates.sort(key=lambda c: (c.score or 0, c.format == "flac", _seeders(c)), reverse=True)
    return candidates[:limit]


def _seeders(candidate: Candidate) -> int:
    return int(m.group(1)) if (m := re.search(r"\((\d+) seeders\)", candidate.source)) else 0


def _files(prowlarr: Prowlarr, release: dict[str, Any]) -> tuple[str, list[torrents.TorrentFile]] | str | None:
    """The release's info hash and files, its magnet link when that's all there is, or None when it can't be had."""
    link = release.get("downloadUrl")
    try:
        got = prowlarr.torrent(link) if link else release.get("magnetUrl")
        if isinstance(got, bytes):
            return torrents.read(got)
        return got
    except Exception:
        return release.get("magnetUrl")


def _candidate(target: Target, release: dict[str, Any], found: Any, format: str) -> Candidate | None:
    name = release["title"]
    common = {"source": f"{release.get('indexer') or 'Prowlarr'} ({release.get('seeders') or 0} seeders)",
              "url": release.get("infoUrl") or release.get("commentUrl")}
    if isinstance(found, tuple):
        info_hash, files = found
        files = audio(files, format)
        if not files:
            return None
        link = without_key(release["downloadUrl"])
        if target.kind == Kind.album:
            matched = match_album(target, files)
            if matched is None:
                return None
            chosen, tracks = matched
            score = 0.35 * similarity(target.title, name) + 0.2 * similarity(target.artist, name) + 0.45 * tracks
            picked = [(d, p, f) for d, p, f in chosen]
        else:
            matched = match_track(target, name, files)
            if matched is None:
                return None
            f, score = matched
            picked = [(None, None, f)]
        size = sum(f.size for _, _, f in picked)
        return Candidate(
            candidate_id=json.dumps({"h": info_hash, "t": link, "f": [[d, p, f.index, f.path] for d, p, f in picked]}),
            kind=target.kind, title=name if target.kind == Kind.album else describe(picked[0][2].path, target.artist)[2],
            artist=target.artist, album=name if target.kind == Kind.track else None,
            **_quality(name, [f for _, _, f in picked], _bitrate(size, target, picked)),
            score=round(score * version_penalty(target.title, name), 3), **common)

    # A magnet link only: offered on the name alone, and checked once qBittorrent has the file list.
    magnet = found if isinstance(found, str) else release.get("magnetUrl")
    info_hash = release.get("infoHash") or (torrents.magnet_hash(magnet) if magnet else None)
    if not magnet or not info_hash:
        return None
    return Candidate(candidate_id=json.dumps({"h": info_hash.lower(), "m": magnet, "f": None}), kind=target.kind,
                     title=name, artist=target.artist, score=round(0.8 * title_score(target, name), 3),
                     **quality_of_name(name), **common)


def _quality(name: str, files: list[torrents.TorrentFile], bitrate: int | None) -> dict[str, Any]:
    """What the files are, by their extension and size, and what the name adds: a FLAC's bit depth and sample rate."""
    said = quality_of_name(name)
    format = format_of(files)
    format = {"m4a": "aac", "ogg": "vorbis"}.get(format, format) if format else said.get("format")
    quality: dict[str, Any] = {"format": format, "bitrate_kbps": bitrate}
    if format == said.get("format"):
        quality |= {k: v for k, v in said.items() if k in ("bit_depth", "sample_rate_hz")}
    return quality


def _bitrate(size: int, target: Target, picked: list[tuple[Any, Any, torrents.TorrentFile]]) -> int | None:
    if target.kind == Kind.album:
        lengths = [t.duration_seconds for t in target.tracks or []]
        seconds = sum(lengths) if lengths and all(lengths) else None
    else:
        seconds = target.duration_seconds
    return round(size * 8 / seconds / 1000) if seconds and size else None


# ---- Fetch ----

def fetch(job: Job, candidate_id: str, target: Target, directory: Path, settings: dict[str, Any]) -> list[dict[str, Any]]:
    chosen = json.loads(candidate_id)
    info_hash: str = chosen["h"]
    with QBittorrent(settings) as qb:
        added = False
        try:
            existing = qb.info(info_hash)
            if existing is None:
                if chosen.get("t"):
                    with Prowlarr(settings) as prowlarr:
                        source = prowlarr.torrent(chosen["t"])
                else:
                    source = chosen["m"]
                qb.add(source, str(settings.get("savePath") or "/downloads/melodex"), str(settings.get("category") or "melodex"),
                       f"melodex,melodex-{job.id}")
                added = True

            listed = _wait_for_files(job, qb, info_hash)
            picked = _pick(chosen, listed, target, settings.get("format", "best"))
            wanted = {index for _, _, index, _ in picked}
            if added:
                qb.priority(info_hash, [f["index"] for f in listed if f["index"] not in wanted], 0)
            # A torrent that was already there keeps the files it had; the requested ones are added to it.
            qb.priority(info_hash, [f["index"] for f in listed if f["index"] in wanted and f["priority"] == 0], 1)
            qb.start(info_hash)

            _wait_for_download(job, qb, info_hash, wanted)
            folder = _local_folder(settings, qb.info(info_hash) or {})
            # Two discs' files can share a name ("CD1/01.flac", "CD2/01.flac").
            names = [PurePosixPath(path).name for _, _, _, path in picked]
            clash = len(set(names)) < len(names)
            share = bool(settings.get("reflinks", True))
            written = [_copy(folder / path, directory, disc, position, clash, share) for disc, position, _, path in picked]
            if added and settings.get("afterwards", "keep") == "remove":
                qb.delete(info_hash, files=True)
            return written
        except BaseException:
            # A torrent added for this request goes again with what it downloaded; one that was already there stays.
            if added:
                try:
                    qb.delete(info_hash, files=True)
                except Exception:
                    pass
            raise


def _wait_for_files(job: Job, qb: QBittorrent, info_hash: str) -> list[dict[str, Any]]:
    """The torrent's files, once qBittorrent has them; a magnet link has to fetch them from peers first."""
    while True:
        job.check()
        if listed := qb.files(info_hash):
            return listed
        time.sleep(POLL_SECONDS)


def _pick(chosen: dict[str, Any], listed: list[dict[str, Any]], target: Target, format: str) -> list[tuple[int | None, int | None, int, str]]:
    if chosen.get("f"):
        names = {f["index"]: f["name"] for f in listed}
        # qBittorrent may have left out the torrent's top folder; its names are what's on disk.
        return [(d, p, i, names.get(i, path)) for d, p, i, path in chosen["f"]]
    files = audio([torrents.TorrentFile(f["index"], f["name"], f["size"]) for f in listed], format)
    if target.kind == Kind.album:
        if (matched := match_album(target, files)) is None:
            raise RuntimeError("The torrent doesn't have every track of the album.")
        return [(d, p, f.index, f.path) for d, p, f in matched[0]]
    if (found := match_track(target, "", files)) is None or found[1] < 0.5:
        raise RuntimeError("The torrent doesn't have the song.")
    return [(None, None, found[0].index, found[0].path)]


def _wait_for_download(job: Job, qb: QBittorrent, info_hash: str, wanted: set[int]) -> None:
    while True:
        job.check()
        info = qb.info(info_hash)
        if info is None:
            raise RuntimeError("The torrent was removed from qBittorrent.")
        if info.get("state") in ("error", "missingFiles"):
            raise RuntimeError(f"qBittorrent stopped the torrent ({info['state']}); its log says why, often that it can't "
                               f"write to the save folder.")
        files = [f for f in qb.files(info_hash) if f["index"] in wanted]
        size = sum(f["size"] for f in files)
        done = sum(f["size"] * f["progress"] for f in files)
        if files and all(f["progress"] >= 1 for f in files):
            return
        job.progress = min(0.99, done / size) if size else None
        time.sleep(POLL_SECONDS)


def _local_folder(settings: dict[str, Any], info: dict[str, Any]) -> Path:
    """Where this plugin sees the torrent's folder: qBittorrent's save path, with the start swapped for the plugin's."""
    save_path = str(settings.get("savePath") or "/downloads/melodex").rstrip("/")
    local = str(settings.get("downloadsFolder") or save_path).rstrip("/")
    actual = str(info.get("save_path") or save_path).rstrip("/")
    if actual == save_path or actual.startswith(save_path + "/"):
        actual = local + actual[len(save_path):]
    return Path(actual)


def _copy(source: Path, directory: Path, disc: int | None, position: int | None, prefix: bool,
          share: bool) -> dict[str, Any]:
    # Copied rather than moved, so qBittorrent can keep seeding it; as a reflink where it can be, so the copy takes
    # next to no space and tagging it leaves the seeded file alone.
    if ".." in source.parts or not source.is_file():
        raise RuntimeError(f"Can't see {source}; check that this plugin can read qBittorrent's downloads folder.")
    name = f"{disc or 1}-{position:02} {source.name}" if prefix and position is not None else source.name
    target = directory / re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", name)
    if share:
        reflinks.clone(source, target)
    else:
        shutil.copyfile(source, target)
    return probe(target, directory, disc, position)


def check_reflinks(settings: dict[str, Any], staging: Path) -> tuple[bool, str]:
    """Whether a file qBittorrent downloaded can be reflinked into staging, tried with one already there."""
    folder = _local_folder(settings, {})
    if not folder.is_dir():
        return False, f"This plugin can't see qBittorrent's downloads folder at {folder}; mount it into its container."
    sample = next((f for f in _files_under(folder) if f.stat().st_size > 0), None)
    if sample is None:
        return False, ("There's nothing in qBittorrent's downloads folder to try it with yet; check again once "
                       "something has downloaded.")
    target = staging / f".melodex-reflink-check-{os.getpid()}"
    try:
        works, why = reflinks.try_clone(sample, target)
    finally:
        target.unlink(missing_ok=True)
    if works:
        return True, "From qBittorrent's downloads to staging, files can share their disk space."
    return False, f"From qBittorrent's downloads to staging, files can't share their disk space: {why}."


def _files_under(folder: Path, limit: int = 2000):
    """Files under the folder, a bounded number of them."""
    seen = 0
    for root, _, files in os.walk(folder):
        for name in files:
            yield Path(root) / name
            seen += 1
            if seen >= limit:
                return
