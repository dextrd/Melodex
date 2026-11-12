"""Rewriting files already in the library: their tags, their embedded cover, and where they live. See
docs/import-worker.md.

Melodex works out the values from its catalogue and sends them; this only writes them. Moving a file to where the
path template says uses beets, as imports do, so formatted files land where an import would put them.
"""

from __future__ import annotations

import os
import shutil
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

from mediafile import Image, ImageType, MediaFile

from . import seektable
from .folders import clear_away

TEXT_FIELDS = {"title", "artist", "album", "albumartist", "country", "media", "albumtype", "label", "catalognum",
               "isrc", "lyrics", "mb_trackid", "mb_releasetrackid", "mb_albumid", "mb_artistid", "mb_albumartistid",
               "mb_releasegroupid"}
NUMBER_FIELDS = {"track", "tracktotal", "disc", "disctotal"}
# Dates arrive as yyyy, yyyy-MM or yyyy-MM-dd and are written as their parts.
DATE_FIELDS = {"date": ("year", "month", "day"), "original_date": ("original_year", "original_month", "original_day")}


@dataclass
class FormatFile:
    path: str
    tags: dict[str, str]
    artwork: str | None
    remove_artwork: bool
    lrc: str | None = None


class FormatFailed(Exception):
    """One file couldn't be formatted, with why."""


def write_tags(full: Path, tags: dict[str, str], artwork: bytes | None, remove_artwork: bool) -> None:
    """Writes the given tags, leaving the rest as they are. An empty value clears the tag."""
    unknown = [name for name in tags if name not in TEXT_FIELDS | NUMBER_FIELDS | DATE_FIELDS.keys()]
    if unknown:
        raise FormatFailed(f"Melodex doesn't write the tag {', '.join(unknown)}.")
    if not tags and artwork is None and not remove_artwork:
        return
    # Written to a copy beside it, which then takes its place in one step: a song playing from it carries on reading
    # the old file to its end, and nothing ever finds it half written.
    copy = full.with_name(WORKING_PREFIX + full.name)
    try:
        shutil.copy2(full, copy)
        _write(copy, tags, artwork, remove_artwork)
        os.replace(copy, full)
    finally:
        copy.unlink(missing_ok=True)


# Beside a file while its copy is written. Melodex's scans pass them by, and media servers pass by hidden files.
WORKING_PREFIX = ".melodex-"


def _write(full: Path, tags: dict[str, str], artwork: bytes | None, remove_artwork: bool) -> None:
    media = MediaFile(full)
    for name, value in tags.items():
        if name in DATE_FIELDS:
            parts = [int(p) for p in value.split("-")[:3] if p.isdigit()] if value else []
            for field, part in zip(DATE_FIELDS[name], parts + [None] * 3):
                setattr(media, field, part)
        elif name in NUMBER_FIELDS:
            setattr(media, name, int(value) if value else None)
        else:
            setattr(media, name, value or None)
    if artwork is not None:
        media.images = [Image(data=artwork, type=ImageType.front)]
    elif remove_artwork:
        media.images = []
    media.save()
    seektable.ensure(full)


def format_files(library: Path, files: list[FormatFile], artwork: dict[str, bytes],
                 destination: Callable[[Path], Path] | None,
                 folder_artwork: bytes | None = None) -> tuple[list[dict[str, Any]], list[str]]:
    """Formats each file, moving it to <destination> of it (library-relative) when that's given. A file that can't be
    formatted is reported with why and the rest carry on. With folder artwork, each formatted file's folder gets it as
    its cover image too. Returns what happened to each file, and the folders that moving them left with no music in
    them, and so removed (see folders.py), by the topmost of each."""
    results = []
    folders: set[Path] = set()
    moved_from: dict[Path, list[Path]] = defaultdict(list)
    for file in files:
        try:
            full = inside(library, file.path)
            if not full.is_file():
                raise FormatFailed("It isn't in the music folder any more.")
            if file.artwork is not None and file.artwork not in artwork:
                raise FormatFailed(f"There's no artwork {file.artwork} in the request.")
            write_tags(full, file.tags, artwork.get(file.artwork) if file.artwork else None, file.remove_artwork)
            if file.lrc == "":
                # Empty lyrics: the .lrc beside it goes.
                full.with_suffix(".lrc").unlink(missing_ok=True)
            elif file.lrc is not None:
                full.with_suffix(".lrc").write_text(file.lrc if file.lrc.endswith("\n") else file.lrc + "\n", encoding="utf-8")
            target = full
            if destination is not None:
                wanted = library / destination(full)
                if wanted != full:
                    target = unique(wanted)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.move(full, target)
                    # Its lyrics go with it, under its new name.
                    if full.with_suffix(".lrc").is_file():
                        shutil.move(full.with_suffix(".lrc"), target.with_suffix(".lrc"))
                    moved_from[full.parent].append(target.parent)
            folders.add(target.parent)
            stat = target.stat()
            results.append({"path": file.path, "newPath": target.relative_to(library).as_posix(), "size": stat.st_size})
        except (FormatFailed, OSError, ValueError) as e:
            results.append({"path": file.path, "error": str(e) or type(e).__name__})
        except Exception as e:  # mediafile raises its own errors for files it can't read or write
            results.append({"path": file.path, "error": f"{type(e).__name__}: {e}"})
    removed: list[Path] = []
    # Deepest first, so a disc's folder goes before its album's.
    for folder in sorted(moved_from, key=lambda f: len(f.parts), reverse=True):
        removed += clear_away(folder, library, Counter(moved_from[folder]).most_common(1)[0][0])
    # After the tidying, so the cover written takes the place of any the old folders brought along.
    if folder_artwork is not None:
        for folder in folders:
            write_folder_cover(folder, folder_artwork)
    # Each by the topmost folder that went, as the ones in it went with it.
    topmost = [f for f in removed if not any(other in f.parents for other in removed)]
    return results, [f.relative_to(library).as_posix() for f in topmost]


FOLDER_COVERS = ("cover", "folder", "front")
IMAGE_TYPES = (".jpg", ".jpeg", ".png", ".webp", ".gif")


def write_folder_cover(folder: Path, data: bytes) -> None:
    """Writes the folder's cover image: in place of one with a cover's usual name, else as cover.jpg (or .png)."""
    extension = ".png" if data[:4] == b"\x89PNG" else ".jpg"
    existing = [p for p in folder.iterdir() if p.is_file() and p.stem.lower() in FOLDER_COVERS and p.suffix.lower() in IMAGE_TYPES]
    for old in existing:
        old.unlink()
    target = folder / f"{existing[0].stem if existing else 'cover'}{extension}"
    target.write_bytes(data)


def inside(library: Path, path: str) -> Path:
    full = (library / path).resolve()
    if library not in full.parents:
        raise FormatFailed("It isn't inside the library.")
    return full


def unique(path: Path) -> Path:
    """The path, or with .1, .2 and so on before the extension if a file is already there, as beets does."""
    candidate, n = path, 0
    while candidate.exists():
        n += 1
        candidate = path.with_name(f"{path.stem}.{n}{path.suffix}")
    return candidate
