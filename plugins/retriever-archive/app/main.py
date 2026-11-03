"""The Internet Archive retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from . import archive
from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "format": {
            "type": "string", "enum": archive.FORMATS, "default": "flac", "title": "Format",
            "description": "flac fetches only lossless files. best fetches FLAC where an item has it and its MP3 otherwise. "
                           "mp3 and ogg are the copies the Archive makes of every file.",
        },
        "collections": {
            "type": "string", "default": archive.DEFAULT_COLLECTIONS, "title": "Collections",
            "description": "The Internet Archive collections to search, separated by commas. The Live Music Archive (etree) "
                           "holds concert recordings shared with the bands' permission; netlabels holds releases under "
                           "Creative Commons licences.",
        },
        "minScore": {
            "type": "number", "minimum": 0, "maximum": 1, "default": 0.6, "title": "Lowest match score",
            "description": "Items that match the request less well than this aren't offered.",
        },
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.archive",
    "name": "Internet Archive",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Finds concerts in the Live Music Archive and netlabel releases on the Internet Archive, and downloads them, in FLAC where there is one.",
    "homepage": "https://archive.org/details/etree",
    "capabilities": ["fetch-track", "fetch-album", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": ["archive.org", "*.us.archive.org"], "files": ["staging (write)"]},
    "maxRequestsPerSecond": 2,
    "retrieval": {"formats": ["flac", "mp3", "ogg"], "lossless": True},
}

app = FastAPI(title="Melodex Internet Archive retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


def check_settings(settings: dict[str, Any]) -> None:
    if settings.get("format", "flac") not in archive.FORMATS:
        raise error(400, f"format must be one of {', '.join(archive.FORMATS)}.", "invalid-settings")
    if not archive.collections(settings):
        raise error(400, "Name at least one collection to search.", "invalid-settings")


@app.get("/v1/manifest")
def manifest() -> dict[str, Any]:
    return MANIFEST


@app.post("/v1/health")
def health(body: HealthRequest) -> dict[str, Any]:
    try:
        check_settings(body.settings)
    except HTTPException as e:
        return {"status": "unhealthy", "message": e.detail["error"]}
    try:
        with archive.client() as http:
            found = archive.items(http, body.settings, "*:*", 1)
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't search the Internet Archive: {e}"}
    if not found:
        return {"status": "degraded", "message": f"Nothing to download in {', '.join(archive.collections(body.settings))}."}
    return {"status": "healthy", "message": f"Searching {', '.join(archive.collections(body.settings))}"}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    try:
        candidates = archive.search(body.target, max(1, min(body.limit, 25)), body.settings)
    except Exception as e:
        raise error(502, f"Internet Archive search failed: {e}", "source-unavailable")
    return {"candidates": dump(candidates)}


@app.post("/v1/fetch", status_code=202)
def fetch(body: FetchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    directory = staging_directory(STAGING, body.staging_directory)
    job = jobs.start(body.job_id, lambda j: archive.fetch(j, body.candidate.candidate_id, directory, body.settings))
    return job.status()


@app.get("/v1/fetch/{job_id}")
def status(job_id: str) -> dict[str, Any]:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    return job.status()


@app.delete("/v1/fetch/{job_id}")
def cancel(job_id: str) -> dict[str, Any]:
    if (job := jobs.get(job_id)) is None:
        raise error(404, "No such job; the plugin may have restarted.", "unknown-job")
    job.cancelled.set()
    if job.state == "queued":
        job.state = "cancelled"
    return job.status()
