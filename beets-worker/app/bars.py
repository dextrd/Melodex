"""The visualiser's bars beside songs, as .bars files. See docs/import-worker.md.

The core works them out from each song, as it can only read the library, and sends them here to be written. A .bars
file is gzipped and starts with MDXB once unzipped; one that doesn't is someone else's, and is never overwritten or
deleted.
"""

from __future__ import annotations

import gzip
import os
from pathlib import Path
from typing import Any

EXTENSION = ".bars"
MAGIC = b"MDXB"
# Beside a file while it's written, as formatting's working copies are.
WORKING_PREFIX = ".melodex-"


def sidecar(path: Path) -> Path:
    return path.with_suffix(EXTENSION)


def is_bars(path: Path) -> bool:
    try:
        with gzip.open(path, "rb") as file:
            return file.read(len(MAGIC)) == MAGIC
    except (OSError, EOFError):
        return False


def write(library: Path, files: list[tuple[str, bytes | None]]) -> list[dict[str, Any]]:
    """Writes each song's bars beside it, or with None deletes them. A song that isn't there, or bars beside it that
    aren't Melodex's, are reported with why and the rest carry on."""
    root = library.resolve()
    results = []
    for path, data in files:
        try:
            full = (root / path).resolve()
            if root not in full.parents:
                raise ValueError("It isn't inside the library.")
            target = sidecar(full)
            if target.exists() and not is_bars(target):
                raise ValueError(f"{target.name} isn't Melodex's bars, so it's left alone.")
            if data is None:
                target.unlink(missing_ok=True)
                results.append({"path": path})
                continue
            if not full.is_file():
                raise ValueError("It isn't in the music folder any more.")
            if not data.startswith(b"\x1f\x8b") or gzip.decompress(data)[:len(MAGIC)] != MAGIC:
                raise ValueError("They aren't bars.")
            working = target.with_name(WORKING_PREFIX + target.name)
            working.write_bytes(data)
            os.replace(working, target)
            results.append({"path": path})
        except (OSError, ValueError, EOFError, gzip.BadGzipFile) as e:
            results.append({"path": path, "error": str(e) or type(e).__name__})
    return results
