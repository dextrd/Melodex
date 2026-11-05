"""The Tidal retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from . import streaming, tidal
from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)
from .tidal import Tidal

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "clientId": {"type": "string", "title": "Client ID",
                     "description": "The client ID of a Tidal app you're allowed to use. Melodex doesn't come with one."},
        "clientSecret": {"type": "string", "default": "", "title": "Client secret", "writeOnly": True,
                         "description": "That app's secret, if it has one."},
        "refreshToken": {"type": "string", "default": "", "title": "Refresh token", "writeOnly": True,
                         "description": "Leave empty to sign in with a code: the plugin's status gives the link. Or a "
                                        "refresh token from a Tidal app you're signed in to."},
        "quality": {"type": "string", "enum": tidal.QUALITIES, "default": "hires", "title": "Quality",
                    "description": "hires fetches the best Tidal has, up to 24-bit/192 kHz FLAC. lossless fetches "
                                   "16-bit/44.1 kHz FLAC. aac fetches 320 kbps AAC."},
        "minScore": {"type": "number", "minimum": 0, "maximum": 1, "default": 0.6, "title": "Lowest match score",
                     "description": "Albums and songs that match the request less well than this aren't offered."},
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.tidal",
    "name": "Tidal",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Downloads albums and songs from Tidal with your own subscription, in FLAC up to 24-bit/192 kHz. "
                   "Tracks Tidal sends encrypted are left alone.",
    "homepage": "https://github.com/joeatethebeans/Melodex/tree/main/plugins/retriever-tidal",
    "capabilities": ["fetch-track", "fetch-album", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": ["api.tidal.com", "auth.tidal.com", "*.tidal.com"], "files": ["staging (write)", "data (sign-in)"]},
    "maxRequestsPerSecond": 4,
    "retrieval": {"formats": ["flac", "aac"], "lossless": True},
}

app = FastAPI(title="Melodex Tidal retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


def check_settings(settings: dict[str, Any]) -> None:
    # Not marked required in the schema: Melodex adds a plugin before its settings can be filled in, and health says
    # what's missing until they are.
    if not settings.get("clientId"):
        raise error(400, "Client ID is needed.", "invalid-settings")
    if settings.get("quality", "hires") not in tidal.QUALITIES:
        raise error(400, f"quality must be one of {', '.join(tidal.QUALITIES)}.", "invalid-settings")


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
        with Tidal(body.settings) as service:
            if (session := service.signed_in()) is None:
                waiting = tidal.sign_in(body.settings)
                return {"status": "unhealthy",
                        "message": f"Sign in at {waiting['link']} (code {waiting['code']}), then test this again."}
            subscription = service.account()
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't sign in to Tidal: {e}"}
    plan = (subscription.get("subscription") or {}).get("type") or "no subscription"
    message = f"Signed in as {session.get('user') or 'you'}, {plan}"
    best = subscription.get("highestSoundQuality")
    quality = body.settings.get("quality", "hires")
    if quality != "aac" and best and best not in ("LOSSLESS", "HI_RES", "HI_RES_LOSSLESS"):
        return {"status": "degraded", "message": f"{message}, which doesn't stream FLAC."}
    if quality == "hires" and best == "LOSSLESS":
        return {"status": "healthy", "message": f"{message}; hi-res isn't in it, so files come at CD quality."}
    return {"status": "healthy", "message": message}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    try:
        with Tidal(body.settings) as service:
            candidates = streaming.search(service, body.target, max(1, min(body.limit, 25)), body.settings)
    except PermissionError as e:
        raise error(502, str(e), "source-unavailable")
    except Exception as e:
        raise error(502, f"Tidal search failed: {e}", "source-unavailable")
    return {"candidates": dump(candidates)}


def _fetch(job, candidate_id: str, directory, settings: dict[str, Any]) -> list[dict[str, Any]]:
    with Tidal(settings) as service, tidal.download() as download:
        return streaming.fetch(job, service, candidate_id, directory, download)


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
