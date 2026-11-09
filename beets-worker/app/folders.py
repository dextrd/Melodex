"""Folders with no music left in them, which formatting and deleting tidy away. See docs/import-worker.md.

A folder has no music in it when all it holds is leftovers (artwork, rip logs, playlists, lyrics and notes), files the
system leaves (.DS_Store, Thumbs.db), and folders that are like that too. Anything else, a video or a PDF or a file of a
kind not known here, keeps its folder, as does a hidden folder, which may be another program's (.stfolder).
"""

from __future__ import annotations

import os
import re
import shutil
from pathlib import Path

# What a folder may still hold and be tidied away with its music: artwork, playlists, lyrics, rip logs and notes.
LEFTOVERS = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".cue", ".log", ".m3u", ".m3u8", ".nfo", ".txt",
             ".sfv", ".accurip", ".md5", ".lrc"}
# Left by the system, and deleted rather than moved: Finder's, Windows Explorer's, and a Synology's thumbnails.
JUNK_FILES = {".ds_store", "thumbs.db", "ehthumbs.db", "desktop.ini"}
JUNK_FOLDERS = {"@eadir", ".appledouble", "__macosx"}
# A folder of its own for each disc of an album: its leftovers belong with the album's, not a folder above.
DISC_FOLDER = re.compile(r"^(cd|disc|disk)\s*\d+\b", re.IGNORECASE)


def is_junk(name: str) -> bool:
    return name.lower() in JUNK_FILES or name.startswith("._")


def is_leftover(name: str) -> bool:
    # Melodex's working copies (.melodex-*) are mid-write: never leftovers.
    return not name.startswith(".") and os.path.splitext(name)[1].lower() in LEFTOVERS


def _kept_folder(name: str) -> bool:
    """A folder that's never tidied away, nor its folder: hidden, and so maybe another program's, or the system's own."""
    return (name.startswith(".") and name.lower() not in JUNK_FOLDERS) or name == "lost+found"


def musicless(folder: Path, bare: bool = False) -> bool:
    """Whether a folder holds nothing but leftovers, junk and folders like that; or if <bare>, nothing but junk and
    empty folders."""
    try:
        entries = list(os.scandir(folder))
    except OSError:
        return False
    for entry in entries:
        if entry.is_symlink():
            return False
        if entry.is_dir():
            if entry.name.lower() in JUNK_FOLDERS:
                continue
            if _kept_folder(entry.name) or not musicless(Path(entry.path), bare):
                return False
        elif not (is_junk(entry.name) or (not bare and is_leftover(entry.name))):
            return False
    return True


def clear_away(folder: Path, library: Path, moved_to: Path | None = None) -> list[Path]:
    """Removes the folder if it has no music in it, then each folder above it left the same, up to the library. Their
    leftovers go where their music went when that's given, keeping the folders they're in: a folder's to <moved_to>,
    and the one above's to the folder above that (or the same, above a disc's folder). Otherwise, or where that's the
    library itself, they're deleted. Returns the folders removed."""
    removed: list[Path] = []
    target = moved_to
    while folder != library and library in folder.parents and folder.is_dir() and musicless(folder):
        if target is not None and (target == library or library not in target.parents):
            target = None
        try:
            _empty(folder, target)
        except OSError:
            break
        removed.append(folder)
        if target is not None and not DISC_FOLDER.match(folder.name):
            target = target.parent
        folder = folder.parent
    return removed


def _empty(folder: Path, target: Path | None) -> None:
    """Moves a folder's leftovers into <target>, or deletes them, and removes it. Something put there meanwhile keeps it,
    as a folder only goes once it's empty."""
    for entry in list(os.scandir(folder)):
        path = Path(entry.path)
        if entry.is_dir(follow_symlinks=False):
            _empty(path, None if target is None or entry.name.lower() in JUNK_FOLDERS else target / entry.name)
        elif target is None or is_junk(entry.name):
            os.remove(path)
        elif (target / entry.name).exists():
            # What's there already, such as the cover just written, stays.
            os.remove(path)
        else:
            target.mkdir(parents=True, exist_ok=True)
            shutil.move(path, target / entry.name)
    folder.rmdir()


def find_musicless(library: Path) -> list[Path]:
    """The folders in the library with no music in them: each the topmost of its kind, as one goes with what's in it.
    Beside an album's music, or its discs' folders, a folder of its artwork (Scans/) is the album's, and stays: there,
    only a folder with nothing in it at all goes."""
    found: list[Path] = []

    def visit(folder: Path) -> None:
        try:
            entries = sorted(os.scandir(folder), key=lambda e: e.name.lower())
        except OSError:
            return
        folders = [e for e in entries if e.is_dir(follow_symlinks=False) and not _kept_folder(e.name)
                   and e.name.lower() not in JUNK_FOLDERS]
        album = any(not e.is_dir() and not is_junk(e.name) and not is_leftover(e.name) for e in entries) or any(
            DISC_FOLDER.match(e.name) and not musicless(Path(e.path)) for e in folders)
        for entry in folders:
            path = Path(entry.path)
            if musicless(path, bare=album):
                found.append(path)
            elif not musicless(path):
                visit(path)

    visit(library)
    return found


def remove_musicless(library: Path) -> tuple[list[Path], list[dict[str, str]]]:
    """Removes the folders in the library with no music in them, and their leftovers. Returns those removed, and those
    that couldn't be, with why."""
    removed: list[Path] = []
    failed: list[dict[str, str]] = []
    for folder in find_musicless(library):
        # Looked at again just before, in case something's arrived since.
        if not musicless(folder):
            continue
        try:
            _empty(folder, None)
            removed.append(folder)
        except OSError as e:
            failed.append({"path": folder.relative_to(library).as_posix(), "error": e.strerror or str(e)})
    return removed, failed
