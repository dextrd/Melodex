"""Tagging and filing staged files with beets, pinned to the release Melodex already chose.

Melodex is the library's database, so each import uses a throwaway beets library: beets fetches the release from
MusicBrainz, maps the files onto its tracks, writes the tags and works out the path from the template, and this
module moves the files into the music library. Nothing beets keeps between imports can disagree with Melodex.

An edition MusicBrainz doesn't have (one only another catalogue lists) is filed as tagged: the files get Melodex's
tags and beets only works out their path.
"""

from __future__ import annotations

import datetime
import os
import tempfile
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from beets import config, plugins
from beets.importer import Action, ImportSession, ImportTask
from beets.library import Item, Library
from beets.util import displayable_path
from mediafile import MediaFile

from . import reflinks, seektable, upgrade
from .folders import find_musicless, remove_musicless
from .formatter import FormatFile, format_files

# Beets fields returned to Melodex after an import.
TAG_FIELDS = ["title", "artist", "album", "albumartist", "track", "tracktotal", "disc", "disctotal", "year",
              "original_year", "date", "label", "catalognum", "isrc", "country", "media", "albumtype", "mb_trackid",
              "mb_releasetrackid", "mb_albumid", "mb_artistid", "mb_albumartistid", "mb_releasegroupid", "genre"]


_configured = False


class ImportFailed(Exception):
    """An import that can't go ahead, with a message for the Activity page."""


@dataclass
class Release:
    music_brainz_id: str | None
    title: str
    artist: str
    date: str | None
    track_count: int


@dataclass
class StagedFile:
    path: str
    recording_id: str
    track_id: str | None
    title: str
    artist: str
    disc: int
    position: int
    isrc: str | None = None


class PinnedSession(ImportSession):
    """An import session that takes the pinned release whatever beets thinks of the match, and checks every file
    landed on the recording Melodex said it is."""

    def __init__(self, lib: Library, path: Path, release: Release, files: dict[str, StagedFile]):
        super().__init__(lib, None, [os.fsencode(path)])
        self.release = release
        self.files = files
        self.error: str | None = None
        self.items: list[Item] = []

    def should_resume(self, path: bytes) -> bool:
        return False

    def choose_match(self, task: ImportTask):
        match = next((c for c in task.candidates if c.info.album_id == self.release.music_brainz_id), None)
        if match is None:
            self.error = f"MusicBrainz didn't return release {self.release.music_brainz_id}."
            return Action.SKIP
        if match.extra_items:
            names = ", ".join(Path(displayable_path(i.path)).name for i in match.extra_items)
            self.error = f"Beets couldn't place {names} on the release."
            return Action.SKIP
        for item, track in match.mapping.items():
            staged = self.files[Path(displayable_path(item.path)).name]
            if track.track_id != staged.recording_id:
                self.error = (f"Beets matched {staged.path} to {track.title} ({track.track_id}), not the requested "
                              f"recording {staged.recording_id}.")
                return Action.SKIP
        self.items = list(match.mapping)
        return match

    def choose_item(self, task):
        self.error = "Melodex imports albums, not singletons."
        return Action.SKIP


class Importer:
    """Runs one import at a time: beets' configuration is global."""

    def __init__(self, staging_root: str, library_root: str):
        self.staging = Path(staging_root).resolve()
        self.library = Path(library_root).resolve()
        self._lock = threading.Lock()
        self._server: str | None = None
        global _configured
        if not _configured:
            # Beets' own defaults only, never a user's config file. Plugins add their defaults as they load, once.
            config.clear()
            config.read(user=False, defaults=True)
            config["plugins"] = ["musicbrainz"]
            plugins.load_plugins()
            _configured = True
        config["threaded"] = False
        # Beets tags the files where they are; moving them into the library is done here.
        for key, value in {"write": True, "copy": False, "move": False, "link": False, "hardlink": False,
                           "reflink": False, "resume": False, "incremental": False, "quiet": True, "timid": False,
                           "autotag": True, "singletons": False, "group_albums": False, "log": None}.items():
            config["import"][key] = value
        # Melodex's catalogue lists every track of a release, so a requested track may be a video or data track,
        # such as a live film on an EP's DVD; beets would leave it out of the release and match the file elsewhere.
        config["match"]["ignore_video_tracks"] = False
        config["match"]["ignore_data_tracks"] = False
        self._scratch = tempfile.TemporaryDirectory(prefix="beets-")
        self._lib = Library(Path(self._scratch.name) / "library.db", directory=str(self.library))

    def run(self, staging_directory: str, release: Release, files: list[StagedFile], destination: str | None,
            template: str, server: str | None) -> list[dict[str, Any]]:
        source = (self.staging / staging_directory).resolve()
        if self.staging not in source.parents:
            raise ImportFailed("stagingDirectory must be inside the staging volume.")
        if destination is not None and (Path(destination).is_absolute() or ".." in Path(destination).parts):
            raise ImportFailed("destinationDirectory must be a folder inside the library.")
        # Worked on in the staging volume, so the copies can share their data with the staged files, and the
        # library's with theirs, where the filesystem allows it.
        work_root = self.staging / ".import"
        work_root.mkdir(exist_ok=True)
        with self._lock, tempfile.TemporaryDirectory(prefix="import-", dir=work_root) as scratch:
            self._use_server(server)
            # Only the requested files go to beets, under names that can't clash, tagged with what Melodex knows so
            # beets maps each one onto the right track.
            work = Path(scratch) / "album"
            work.mkdir()
            by_name: dict[str, StagedFile] = {}
            for index, staged in enumerate(files):
                original = source / staged.path
                if not original.is_file() or source not in original.resolve().parents:
                    raise ImportFailed(f"{staged.path} isn't in the staging directory.")
                name = f"{index:03d}{original.suffix.lower()}"
                reflinks.clone(original, work / name)
                self._pretag(work / name, staged, release)
                by_name[name] = staged

            config["paths"] = {"default": template, "comp": template, "singleton": template}
            self._lib.__dict__.pop("path_formats", None)
            try:
                if release.music_brainz_id is None:
                    return self._file(self._as_tagged(work), source, destination, by_name)
                config["import"]["search_ids"] = [release.music_brainz_id]
                session = PinnedSession(self._lib, work, release, by_name)
                session.run()
                if session.error or not session.items:
                    raise ImportFailed(session.error or "Beets didn't import anything; MusicBrainz may be unreachable.")
                return self._file(session.items, source, destination, by_name)
            finally:
                # The library only lives for the import: Melodex is the database.
                for album in self._lib.albums():
                    album.remove(delete=False, with_items=True)
                for item in self._lib.items():
                    item.remove(delete=False)

    def format(self, files: list[FormatFile], artwork: dict[str, bytes], template: str | None,
               folder_artwork: bytes | None = None) -> tuple[list[dict[str, Any]], list[str]]:
        """Rewrites files in the library: see formatter.py. With a template, moves each one to where beets would file
        it now, going by its new tags."""
        with self._lock:
            destination = None
            if template is not None:
                config["paths"] = {"default": template, "comp": template, "singleton": template}
                self._lib.__dict__.pop("path_formats", None)

                def destination(full: Path) -> Path:
                    item = Item.from_path(os.fsencode(full))
                    item.add(self._lib)
                    try:
                        return Path(displayable_path(item.destination(relative_to_libdir=True)))
                    finally:
                        item.remove(delete=False)

            return format_files(self.library, files, artwork, destination, folder_artwork)

    def replace(self, staged: str, path: str, hold: str | None) -> dict[str, Any]:
        """Replaces a library file with a better copy of it: see upgrade.py."""
        with self._lock:
            return upgrade.replace(self.staging, self.library, staged, path, hold)

    def restore(self, held: str, path: str, remove: str | None) -> dict[str, Any]:
        """Puts a replaced file back: see upgrade.py."""
        with self._lock:
            return upgrade.restore(self.staging, self.library, held, path, remove)

    def musicless(self, remove: bool) -> tuple[list[str], list[dict[str, str]]]:
        """The library's folders with no music in them (see folders.py), and with <remove>, removes them. Not while an
        import or a format runs, as either makes folders before it moves files into them."""
        with self._lock:
            if not remove:
                found, failed = find_musicless(self.library), []
            else:
                found, failed = remove_musicless(self.library)
            return [f.relative_to(self.library).as_posix() for f in found], failed

    def _as_tagged(self, work: Path) -> list[Item]:
        """The pre-tagged files as beets items, for an edition with no MusicBrainz release: nothing is looked up."""
        items = []
        for path in sorted(work.iterdir()):
            item = Item.from_path(os.fsencode(path))
            item.add(self._lib)
            items.append(item)
        return items

    def _file(self, items: list[Item], source: Path, destination: str | None,
              by_name: dict[str, StagedFile]) -> list[dict[str, Any]]:
        results = []
        for item in items:
            staged = by_name[Path(displayable_path(item.path)).name]
            relative = Path(displayable_path(item.destination(relative_to_libdir=True)))
            if destination is not None:
                relative = Path(destination) / relative.name
            target = self._unique(self.library / relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            reflinks.move(Path(displayable_path(item.path)), target)
            (source / staged.path).unlink(missing_ok=True)
            results.append({
                "source": staged.path,
                "path": target.relative_to(self.library).as_posix(),
                "durationSeconds": round(item.length, 3) if item.length else None,
                "format": item.format or None,
                "tags": {f: str(item.get(f)) for f in TAG_FIELDS if item.get(f) not in (None, "", 0, [])},
            })
        return results

    def check_reflinks(self) -> tuple[bool, str | None]:
        """Whether files can go from staging to the library as reflinks."""
        work_root = self.staging / ".import"
        work_root.mkdir(exist_ok=True)
        return reflinks.check(work_root, self.library)

    def _use_server(self, server: str | None) -> None:
        """Points beets at the MusicBrainz server Melodex uses, so a mirror applies to imports too."""
        server = (server or "https://musicbrainz.org").rstrip("/")
        if server == self._server:
            return
        https = server.startswith("https://")
        config["musicbrainz"]["host"] = server.split("://", 1)[-1]
        config["musicbrainz"]["https"] = https
        # A mirror has no rate limit; the public server allows one request a second.
        config["musicbrainz"]["ratelimit"] = 1 if "musicbrainz.org" in server else 100
        for plugin in plugins.find_plugins():
            plugin.__dict__.pop("mb_api", None)
        self._server = server

    @staticmethod
    def _pretag(path: Path, staged: StagedFile, release: Release) -> None:
        media = MediaFile(path)
        media.title = staged.title
        media.artist = staged.artist
        media.album = release.title
        media.albumartist = release.artist
        media.track = staged.position
        media.disc = staged.disc
        if staged.isrc:
            media.isrc = staged.isrc
        if release.music_brainz_id is None:
            # Filed as tagged, so these are the tags it keeps; beets would otherwise have filled them in.
            if release.track_count:
                media.tracktotal = release.track_count
            if release.date:
                media.date = _date(release.date)
            media.save()
            seektable.ensure(path)
            return
        media.mb_trackid = staged.recording_id
        if staged.track_id:
            media.mb_releasetrackid = staged.track_id
        media.mb_albumid = release.music_brainz_id
        media.save()
        seektable.ensure(path)

    @staticmethod
    def _unique(path: Path) -> Path:
        """The path, or with .1, .2 and so on before the extension if a file is already there, as beets does."""
        candidate, n = path, 0
        while candidate.exists():
            n += 1
            candidate = path.with_name(f"{path.stem}.{n}{path.suffix}")
        return candidate


def _date(value: str) -> datetime.date | None:
    """A catalogue's date, which may be only a year or a year and month, as the first day it could be."""
    parts = (value.split("-") + ["1", "1"])[:3]
    try:
        return datetime.date(*(int(p) for p in parts))
    except ValueError:
        return None
