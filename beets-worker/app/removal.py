"""Deleting files from the library, for removing an artist, album or track. See docs/import-worker.md."""

from __future__ import annotations

import os
from pathlib import Path

from .folders import clear_away


def delete(library: str, paths: list[str]) -> tuple[list[str], list[dict[str, str]]]:
    """Deletes each file, then any folder left with no music in it (see folders.py), up to the library. Returns the paths deleted
    and those that couldn't be, with why. A file that's already gone counts as deleted."""
    root = os.path.realpath(library)
    deleted: list[str] = []
    failed: list[dict[str, str]] = []
    folders: set[str] = set()
    for path in paths:
        full = os.path.realpath(os.path.join(root, path))
        if not full.startswith(root + os.sep):
            failed.append({"path": path, "error": "It isn't inside the library."})
            continue
        try:
            os.remove(full)
        except FileNotFoundError:
            pass
        except OSError as e:
            failed.append({"path": path, "error": e.strerror or str(e)})
            continue
        deleted.append(path)
        folders.add(os.path.dirname(full))
        # Its lyrics go with it.
        try:
            os.remove(os.path.splitext(full)[0] + ".lrc")
        except OSError:
            pass
    for folder in sorted(folders, key=len, reverse=True):
        clear_away(Path(folder), Path(root))
    return deleted, failed

