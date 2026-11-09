"""The Qobuz retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from . import qobuz, streaming
from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)
from .qobuz import Qobuz

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "appId": {"type": "string", "title": "App ID",
                  "description": "The ID of a Qobuz app you're allowed to use. Melodex doesn't come with one."},
        "appSecret": {"type": "string", "title": "App secret", "writeOnly": True,
                      "description": "That app's secret, which signs requests for files."},
        "email": {"type": "string", "default": "", "title": "Email"},
        "password": {"type": "string", "default": "", "title": "Password", "writeOnly": True},
        "userAuthToken": {"type": "string", "default": "", "title": "Sign-in token", "writeOnly": True,
                          "description": "Instead of an email and password: a user_auth_token from a Qobuz app "
                                         "you're signed in to."},
        "quality": {"type": "string", "enum": qobuz.QUALITIES, "default": "hires", "title": "Quality",
                    "description": "hires fetches the best Qobuz has, up to 24-bit/192 kHz FLAC. cd fetches 16-bit/44.1 "
                                   "kHz FLAC. mp3 fetches 320 kbps MP3s."},
        "minScore": {"type": "number", "minimum": 0, "maximum": 1, "default": 0.6, "title": "Lowest match score",
                     "description": "Albums and songs that match the request less well than this aren't offered."},
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.qobuz",
    "name": "Qobuz",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Downloads albums and songs from Qobuz with your own subscription, in FLAC up to 24-bit/192 kHz.",
    "homepage": "https://github.com/dextrd/Melodex/tree/main/plugins/retriever-qobuz",
    "capabilities": ["fetch-track", "fetch-album", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": ["www.qobuz.com", "*.qobuz.com"], "files": ["staging (write)"]},
    "maxRequestsPerSecond": 4,
    "retrieval": {"formats": ["flac", "mp3"], "lossless": True},
}

app = FastAPI(title="Melodex Qobuz retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


def check_settings(settings: dict[str, Any]) -> None:
    # Not marked required in the schema: Melodex adds a plugin before its settings can be filled in, and health says
    # what's missing until they are.
    for name in ["appId", "appSecret"]:
        if not settings.get(name):
            raise error(400, f"{SETTINGS_SCHEMA['properties'][name]['title']} is needed.", "invalid-settings")
    if not settings.get("userAuthToken") and not (settings.get("email") and settings.get("password")):
        raise error(400, "An email and password, or a sign-in token, is needed.", "invalid-settings")
    if settings.get("quality", "hires") not in qobuz.QUALITIES:
        raise error(400, f"quality must be one of {', '.join(qobuz.QUALITIES)}.", "invalid-settings")


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
        with Qobuz(body.settings) as service:
            user = service.account()
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't sign in to Qobuz: {e}"}
    parameters = (user.get("credential") or {}).get("parameters") or {}
    plan = parameters.get("short_label") or (user.get("credential") or {}).get("label") or "no subscription"
    message = f"Signed in as {user.get('display_name') or user.get('login') or 'you'}, {plan}"
    quality = body.settings.get("quality", "hires")
    if quality != "mp3" and not parameters.get("lossless_streaming"):
        return {"status": "degraded", "message": f"{message}, which doesn't stream FLAC; Qobuz will only give samples."}
    if quality == "hires" and not parameters.get("hires_streaming"):
        return {"status": "healthy", "message": f"{message}; hi-res isn't in it, so files come at CD quality."}
    return {"status": "healthy", "message": message}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    try:
        with Qobuz(body.settings) as service:
            candidates = streaming.search(service, body.target, max(1, min(body.limit, 25)), body.settings)
    except PermissionError as e:
        raise error(502, str(e), "source-unavailable")
    except Exception as e:
        raise error(502, f"Qobuz search failed: {e}", "source-unavailable")
    return {"candidates": dump(candidates)}


def _fetch(job, candidate_id: str, directory, settings: dict[str, Any]) -> list[dict[str, Any]]:
    with Qobuz(settings) as service, qobuz.download() as download:
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
