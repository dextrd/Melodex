"""The beets worker's HTTP API, called by the Melodex core. See docs/import-worker.md."""

from __future__ import annotations

import base64
import binascii
import os
from typing import Any

import beets
from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse
from fastapi.concurrency import run_in_threadpool
from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel

from .formatter import FormatFile
from .importer import Importer, ImportFailed, Release, StagedFile
from .removal import delete
from .spectrum import analyse

STAGING = os.environ.get("STAGING_ROOT") or "/staging"
LIBRARY = os.environ.get("LIBRARY_ROOT") or "/music"


class Model(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")


class ImportRelease(Model):
    music_brainz_id: str | None = None
    title: str
    artist: str
    date: str | None = None
    track_count: int = 0


class ImportFile(Model):
    path: str
    recording_id: str = ""
    track_id: str | None = None
    title: str
    artist: str
    disc: int = 1
    position: int
    isrc: str | None = None


class ImportRequest(Model):
    staging_directory: str
    release: ImportRelease
    files: list[ImportFile]
    destination_directory: str | None = None
    path_template: str = "$albumartist/$album%aunique{}/$track $title"
    music_brainz_server: str | None = None


class FormatFileModel(Model):
    path: str
    tags: dict[str, str] = {}
    artwork: str | None = None
    remove_artwork: bool = False
    lrc: str | None = None


class FormatArtwork(Model):
    data: str


class FormatRequest(Model):
    files: list[FormatFileModel]
    artwork: dict[str, FormatArtwork] = {}
    path_template: str | None = None
    folder_artwork: str | None = None


class DeleteRequest(Model):
    paths: list[str]


class MusiclessRequest(Model):
    remove: bool = False


class SpectrumFile(Model):
    root: str = "staging"
    path: str


class SpectrumRequest(Model):
    files: list[SpectrumFile]


app = FastAPI(title="Melodex beets worker", version="0.1.0")
importer: Importer | None = None


def get_importer() -> Importer:
    global importer
    if importer is None:
        importer = Importer(STAGING, LIBRARY)
    return importer


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


@app.get("/v1/health")
def health() -> dict[str, Any]:
    for name, path in (("staging", STAGING), ("library", LIBRARY)):
        if not os.path.isdir(path):
            return {"status": "unhealthy", "message": f"The {name} folder {path} isn't mounted."}
    if not os.access(LIBRARY, os.W_OK):
        return {"status": "unhealthy", "message": f"The library folder {LIBRARY} isn't writable."}
    return {"status": "healthy", "message": f"beets {beets.__version__}"}


@app.post("/v1/import")
async def run_import(body: ImportRequest) -> dict[str, Any]:
    if not body.files:
        raise HTTPException(400, {"error": "No files to import.", "code": "invalid-request"})
    release = Release(body.release.music_brainz_id, body.release.title, body.release.artist, body.release.date,
                      body.release.track_count)
    files = [StagedFile(f.path, f.recording_id, f.track_id, f.title, f.artist, f.disc, f.position, f.isrc)
             for f in body.files]
    try:
        imported = await run_in_threadpool(get_importer().run, body.staging_directory, release, files,
                                           body.destination_directory, body.path_template, body.music_brainz_server)
    except ImportFailed as e:
        raise HTTPException(422, {"error": str(e), "code": "import-failed"})
    return {"files": [{k: v for k, v in f.items() if v is not None} for f in imported]}


@app.post("/v1/reflinks")
async def check_reflinks() -> dict[str, Any]:
    """Whether imported files can share their data on disk with the staged ones (see reflinks.py)."""
    works, reason = await run_in_threadpool(get_importer().check_reflinks)
    return {"works": works, **({"reason": reason} if reason else {})}


@app.post("/v1/format")
async def run_format(body: FormatRequest) -> dict[str, Any]:
    try:
        artwork = {key: base64.b64decode(image.data, validate=True) for key, image in body.artwork.items()}
    except binascii.Error:
        raise HTTPException(400, {"error": "Artwork must be base64.", "code": "invalid-request"})
    if body.folder_artwork is not None and body.folder_artwork not in artwork:
        raise HTTPException(400, {"error": f"There's no artwork {body.folder_artwork} in the request.", "code": "invalid-request"})
    files = [FormatFile(f.path, f.tags, f.artwork, f.remove_artwork, f.lrc) for f in body.files]
    folder = artwork[body.folder_artwork] if body.folder_artwork is not None else None
    results, removed = await run_in_threadpool(get_importer().format, files, artwork, body.path_template, folder)
    return {"files": results, "removedFolders": removed}


@app.post("/v1/folders/musicless")
async def musicless_folders(body: MusiclessRequest) -> dict[str, Any]:
    """The library's folders with no music in them, to show before a format of the whole library; with remove, removes
    them, as that format does once its files are done."""
    folders, failed = await run_in_threadpool(get_importer().musicless, body.remove)
    return {"folders": folders, "failed": failed}


@app.post("/v1/delete")
async def run_delete(body: DeleteRequest) -> dict[str, Any]:
    deleted, failed = await run_in_threadpool(delete, LIBRARY, body.paths)
    return {"deleted": deleted, "failed": failed}


@app.post("/v1/spectrum")
async def run_spectrum(body: SpectrumRequest) -> dict[str, Any]:
    """Where each lossless file's highs are cut off sharply, if they are, so the core can tell lossy music passed off as
    lossless (see spectrum.py). Files are read, never changed."""
    def check(file: SpectrumFile) -> dict[str, Any]:
        roots = {"staging": STAGING, "library": LIBRARY}
        if file.root not in roots:
            return {"path": file.path, "root": file.root, "error": "The root is staging or library."}
        root = os.path.realpath(roots[file.root])
        full = os.path.realpath(os.path.join(root, file.path))
        if not full.startswith(root + os.sep):
            return {"path": file.path, "root": file.root, "error": f"It isn't inside the {file.root}."}
        found = analyse(full)
        result = {"path": file.path, "root": file.root, "cutoffHz": found.cutoff_hz, "error": found.error}
        return {k: v for k, v in result.items() if v is not None}

    return {"files": await run_in_threadpool(lambda: [check(f) for f in body.files])}
