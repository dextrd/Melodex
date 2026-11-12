"""Replacing a library file with a better copy of the same song, and putting it back. See docs/import-worker.md.

The new file takes the old one's tags, every one beets knows, and its path less the extension, so a media server sees
the same song in a better file: Navidrome 0.55 and later keep its stars, plays and playlists only when its tags are
the same. The old file leaves the library before the new one goes in, held in staging or deleted.
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Any

import mutagen
from mediafile import MediaFile

from . import reflinks, seektable

# Parts of other fields (date sets year, month and day), a copy of another (art is the first of images), or about
# the encode rather than the song, so the new file keeps its own.
_NOT_COPIED = {"art", "year", "month", "day", "original_year", "original_month", "original_day", "encoder",
               "rg_track_gain", "rg_track_peak", "rg_album_gain", "rg_album_peak", "r128_track_gain", "r128_album_gain"}
_OWN = ("rg_track_gain", "rg_track_peak", "rg_album_gain", "rg_album_peak", "r128_track_gain", "r128_album_gain")


class UpgradeFailed(Exception):
    pass


def inside(root: Path, relative: str, what: str) -> Path:
    full = (root / relative).resolve()
    if root not in full.parents:
        raise UpgradeFailed(f"{relative} isn't inside the {what}.")
    return full


def copy_tags(old: Path, new: Path) -> None:
    """Gives new exactly the tags old has, as beets reads them, and its embedded art; the new file's own are cleared,
    except its loudness, which is its own encode's."""
    source = MediaFile(old)
    own = {field: getattr(MediaFile(new), field) for field in _OWN}
    tagged = mutagen.File(new)
    if tagged is None:
        raise UpgradeFailed("The new file isn't audio mutagen can read.")
    tagged.delete()
    if hasattr(tagged, "clear_pictures"):
        tagged.clear_pictures()
    tagged.save()
    target = MediaFile(new)
    for field in MediaFile.fields():
        if field in _NOT_COPIED:
            continue
        value = getattr(source, field)
        if value not in (None, "", [], 0):
            setattr(target, field, value)
    for field, value in own.items():
        if value is not None:
            setattr(target, field, value)
    target.save()
    seektable.ensure(new)


def replace(staging: Path, library: Path, staged: str, path: str, hold: str | None) -> dict[str, Any]:
    """Puts the staged file in the library in place of the file at path: tagged as it was, at its path with the new
    file's extension. The old file goes to hold, a folder in staging, or is deleted when there's none."""
    new = inside(staging, staged, "staging volume")
    old = inside(library, path, "library")
    if not new.is_file():
        raise UpgradeFailed(f"{staged} isn't in staging.")
    if not old.is_file():
        raise UpgradeFailed(f"{path} isn't in the library any more.")
    target = old.with_suffix(new.suffix.lower())
    if target != old and target.exists():
        raise UpgradeFailed(f"There's already a file at {target.relative_to(library).as_posix()}.")
    held = None
    if hold is not None:
        held_folder = inside(staging, hold, "staging volume")
        held = held_folder / old.name

    # Tagged in a copy, so the staged file stays as it was if anything fails.
    work_root = staging / ".import"
    work_root.mkdir(exist_ok=True)
    work = work_root / f".upgrade-{os.getpid()}-{new.name}"
    try:
        reflinks.clone(new, work)
        copy_tags(old, work)
        # The old file leaves first, so the media server never sees both.
        if held is not None:
            held.parent.mkdir(parents=True, exist_ok=True)
            # Made by this worker, deleted by the core once it's held long enough.
            os.chmod(held.parent, 0o777)
            os.chmod(held.parent.parent, 0o777)
            reflinks.move(old, held)
        else:
            old.unlink()
        try:
            reflinks.move(work, target)
        except OSError:
            if held is not None:
                reflinks.move(held, old)
            raise
    except OSError as e:
        raise UpgradeFailed(f"Couldn't replace it: {e.strerror or e}")
    finally:
        work.unlink(missing_ok=True)
    new.unlink(missing_ok=True)

    media = MediaFile(target)
    return {
        "path": target.relative_to(library).as_posix(),
        "size": target.stat().st_size,
        "durationSeconds": round(media.length, 3) if media.length else None,
        "format": media.format or None,
        "held": held.relative_to(staging).as_posix() if held is not None else None,
    }


def restore(staging: Path, library: Path, held: str, path: str, remove: str | None) -> dict[str, Any]:
    """Puts a held file back at path, deleting the file that replaced it (remove) first."""
    source = inside(staging, held, "staging volume")
    target = inside(library, path, "library")
    if not source.is_file():
        raise UpgradeFailed("The old file isn't held any more.")
    replacement = inside(library, remove, "library") if remove is not None else None
    if target.exists() and target != replacement:
        raise UpgradeFailed(f"There's already a file at {path}.")
    try:
        if replacement is not None:
            replacement.unlink(missing_ok=True)
        target.parent.mkdir(parents=True, exist_ok=True)
        reflinks.move(source, target)
    except OSError as e:
        raise UpgradeFailed(f"Couldn't put it back: {e.strerror or e}")
    try:
        source.parent.rmdir()
    except OSError:
        pass
    return {"path": target.relative_to(library).as_posix(), "size": target.stat().st_size}
