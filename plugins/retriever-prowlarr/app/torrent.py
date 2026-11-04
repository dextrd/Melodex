"""Reading .torrent files and magnet links: the info hash and the files inside."""

from __future__ import annotations

import base64
import hashlib
from dataclasses import dataclass
from urllib.parse import parse_qs, urlparse


@dataclass
class TorrentFile:
    index: int
    path: str
    size: int


class BencodeError(ValueError):
    pass


def _decode(data: bytes, at: int, spans: dict[bytes, tuple[int, int]] | None = None):
    """The value starting at `at` and where it ends. Records where the top-level dict's values sit in `spans`."""
    kind = data[at:at + 1]
    if kind == b"i":
        end = data.index(b"e", at)
        return int(data[at + 1:end]), end + 1
    if kind == b"l":
        at, items = at + 1, []
        while data[at:at + 1] != b"e":
            item, at = _decode(data, at)
            items.append(item)
        return items, at + 1
    if kind == b"d":
        at, items = at + 1, {}
        while data[at:at + 1] != b"e":
            key, at = _decode(data, at)
            start = at
            items[key], at = _decode(data, at)
            if spans is not None:
                spans[key] = (start, at)
        return items, at + 1
    if kind.isdigit():
        colon = data.index(b":", at)
        length = int(data[at:colon])
        return data[colon + 1:colon + 1 + length], colon + 1 + length
    raise BencodeError(f"unexpected {kind!r} at {at}")


def read(data: bytes) -> tuple[str, list[TorrentFile]]:
    """A .torrent file's info hash (hex) and its files in the order qBittorrent numbers them."""
    try:
        spans: dict[bytes, tuple[int, int]] = {}
        root, _ = _decode(data, 0, spans)
        info = root[b"info"]
    except (BencodeError, ValueError, IndexError, KeyError, TypeError) as e:
        raise BencodeError(f"not a torrent file: {e}") from e
    start, end = spans[b"info"]
    info_hash = hashlib.sha1(data[start:end]).hexdigest()
    name = _text(info.get(b"name.utf-8") or info.get(b"name", b""))
    if b"files" not in info:
        return info_hash, [TorrentFile(0, name, int(info.get(b"length", 0)))]
    files = []
    for f in info[b"files"]:
        parts = [_text(p) for p in (f.get(b"path.utf-8") or f.get(b"path", []))]
        # Padding files (BEP 47) aren't real files, but qBittorrent still counts them.
        files.append(TorrentFile(len(files), "/".join([name, *parts]), int(f.get(b"length", 0))))
    return info_hash, files


def magnet_hash(magnet: str) -> str | None:
    for xt in parse_qs(urlparse(magnet).query).get("xt", []):
        if xt.lower().startswith("urn:btih:"):
            value = xt[9:]
            if len(value) == 40:
                return value.lower()
            if len(value) == 32:
                return base64.b32decode(value.upper()).hex()
    return None


def _text(value: bytes) -> str:
    return value.decode("utf-8", errors="replace")
