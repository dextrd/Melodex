"""The Bandcamp retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from . import bandcamp, streaming
from .bandcamp import Bandcamp
from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "identity": {"type": "string", "title": "Identity cookie", "writeOnly": True,
                     "description": "The identity cookie of a browser signed in to Bandcamp, which lets the plugin "
                                    "see and download your purchases."},
        "format": {"type": "string", "enum": bandcamp.FORMATS, "default": "flac", "title": "Format",
                   "description": "Which of Bandcamp's download formats to fetch: flac, alac, wav and aiff-lossless "
                                  "are lossless."},
        "minScore": {"type": "number", "minimum": 0, "maximum": 1, "default": 0.6, "title": "Lowest match score",
                     "description": "Albums and songs that match the request less well than this aren't offered."},
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.bandcamp",
    "name": "Bandcamp",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Downloads albums and songs you've bought on Bandcamp, in FLAC or another format Bandcamp offers.",
    "homepage": "https://github.com/dextrd/Melodex/tree/main/plugins/retriever-bandcamp",
    "capabilities": ["fetch-track", "fetch-album", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": ["bandcamp.com", "*.bandcamp.com", "*.bcbits.com"], "files": ["staging (write)"]},
    "maxRequestsPerSecond": 2,
    "retrieval": {"formats": ["flac", "alac", "wav", "aiff", "mp3", "aac", "vorbis"], "lossless": True},
}

app = FastAPI(title="Melodex Bandcamp retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


def check_settings(settings: dict[str, Any]) -> None:
    # Not marked required in the schema: Melodex adds a plugin before its settings can be filled in, and health says
    # what's missing until they are.
    if not settings.get("identity"):
        raise error(400, "The identity cookie is needed.", "invalid-settings")
    if settings.get("format", "flac") not in bandcamp.FORMATS:
        raise error(400, f"format must be one of {', '.join(bandcamp.FORMATS)}.", "invalid-settings")


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
        with Bandcamp(body.settings) as service:
            summary = service.account()
            purchases = service.collection()
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't sign in to Bandcamp: {e}"}
    name = (summary.get("collection_summary") or {}).get("username") or "you"
    albums = sum(p.is_album for p in purchases)
    songs = len(purchases) - albums
    return {"status": "healthy", "message": f"Signed in as {name}, with {_count(albums, 'album')} and "
                                            f"{_count(songs, 'song')} bought"}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    try:
        with Bandcamp(body.settings) as service:
            candidates = streaming.search(service, body.target, max(1, min(body.limit, 25)), body.settings)
    except PermissionError as e:
        raise error(502, str(e), "source-unavailable")
    except Exception as e:
        raise error(502, f"Bandcamp search failed: {e}", "source-unavailable")
    return {"candidates": dump(candidates)}


def _fetch(job, candidate_id: str, directory, settings: dict[str, Any]) -> list[dict[str, Any]]:
    with Bandcamp(settings) as service:
        return service.fetch(job, json.loads(candidate_id)["t"], directory, Path(STAGING) / ".bandcamp")


@app.post("/v1/fetch", status_code=202)
def fetch(body: FetchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    directory = staging_directory(STAGING, body.staging_directory)
    job = jobs.start(body.job_id, lambda j: _fetch(j, body.candidate.candidate_id, directory, body.settings))
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


def _count(n: int, noun: str) -> str:
    return f"{n} {noun}{'' if n == 1 else 's'}"
