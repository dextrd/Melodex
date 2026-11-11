"""The Prowlarr and qBittorrent retrieval plugin's HTTP API (Melodex retrieval contract v1.0)."""

from __future__ import annotations

from pathlib import Path
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.responses import JSONResponse

from . import retrieve
from .clients import Prowlarr, QBittorrent
from .contract import (CONTRACT_VERSION, FetchRequest, HealthRequest, Jobs, SearchRequest, dump, env, error,
                       staging_directory)

VERSION = "0.1.0"
STAGING = env("STAGING_ROOT", "/staging")

SETTINGS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "prowlarrUrl": {"type": "string", "default": "http://prowlarr:9696", "title": "Prowlarr address"},
        "prowlarrApiKey": {"type": "string", "title": "Prowlarr API key", "writeOnly": True,
                           "description": "In Prowlarr under Settings → General."},
        "qbittorrentUrl": {"type": "string", "default": "http://qbittorrent:8080", "title": "qBittorrent address",
                           "description": "The address of qBittorrent's Web UI."},
        "qbittorrentUsername": {"type": "string", "default": "", "title": "qBittorrent username",
                                "description": "Leave empty when qBittorrent lets this plugin in without logging in."},
        "qbittorrentPassword": {"type": "string", "default": "", "title": "qBittorrent password", "writeOnly": True},
        "category": {"type": "string", "default": "melodex", "title": "Category",
                     "description": "The qBittorrent category torrents are added with."},
        "savePath": {"type": "string", "default": "/downloads/melodex", "title": "Save folder",
                     "description": "Where qBittorrent saves torrents added for Melodex, as qBittorrent sees it."},
        "downloadsFolder": {"type": "string", "default": "", "title": "Save folder for this plugin",
                            "description": "The same folder as this plugin's container sees it, when it's mounted "
                                           "somewhere else. Leave empty when it's the same path."},
        "format": {"type": "string", "enum": retrieve.FORMATS, "default": "best", "title": "Format",
                   "description": "best prefers FLAC and takes MP3 and other formats otherwise. flac takes only "
                                  "lossless files; mp3 only MP3s."},
        "minSeeders": {"type": "integer", "minimum": 0, "default": 1, "title": "Fewest seeders",
                       "description": "Torrents with fewer seeders aren't offered."},
        "losslessTrackers": {"type": "string", "default": "", "title": "Trackers known for lossless",
                             "description": "Indexers, by their names in Prowlarr and separated by commas, that check "
                                            "lossless uploads are really lossless. Their lossless releases are tried "
                                            "before others alike, as are those with a rip's log and cue."},
        "minScore": {"type": "number", "minimum": 0, "maximum": 1, "default": 0.6, "title": "Lowest match score",
                     "description": "Torrents that match the request less well than this aren't offered."},
        "afterwards": {"type": "string", "enum": retrieve.AFTER, "default": "keep", "title": "Once copied",
                       "description": "keep leaves the torrent in qBittorrent to seed. remove takes it out, with "
                                      "its files, once Melodex has a copy."},
        "reflinks": {"type": "boolean", "default": True, "title": "Share disk space with the seeding files",
                     "description": "Copies files as reflinks, which share their data on disk with the files "
                                    "qBittorrent seeds, so music you keep seeding isn't stored twice. Tagging the "
                                    "library's copy leaves the seeding one alone. Where the filesystem can't make "
                                    "them, files are copied as usual; Check reflinks says which."},
    },
}

MANIFEST: dict[str, Any] = {
    "id": "melodex.retrieval.prowlarr",
    "name": "Prowlarr",
    "version": VERSION,
    "type": "retrieval",
    "contractVersion": CONTRACT_VERSION,
    "description": "Searches the torrent indexers you've set up in Prowlarr and downloads with your qBittorrent, "
                   "taking only the files of the album or song asked for.",
    "homepage": "https://github.com/dextrd/Melodex/tree/main/plugins/retriever-prowlarr",
    "capabilities": ["fetch-track", "fetch-album", "cancel"],
    "settingsSchema": SETTINGS_SCHEMA,
    "access": {"network": ["your Prowlarr", "your qBittorrent"], "files": ["staging (write)", "qBittorrent's downloads (read)"]},
    "maxRequestsPerSecond": 2,
    "retrieval": {"formats": ["flac", "mp3", "m4a", "ogg", "opus"], "lossless": True},
    "checks": [{"id": "reflinks", "title": "Check reflinks",
                "description": "Whether files can share their disk space from qBittorrent's downloads all the way to "
                               "your music library."}],
}

app = FastAPI(title="Melodex Prowlarr retriever", version=VERSION)
jobs = Jobs(int(env("PARALLEL_FETCHES", "2")))


@app.exception_handler(HTTPException)
async def contract_error(_: Request, e: HTTPException) -> JSONResponse:
    body = e.detail if isinstance(e.detail, dict) else {"error": str(e.detail)}
    return JSONResponse(body, status_code=e.status_code)


# Not marked required in the schema: Melodex adds a plugin before its settings can be filled in, and health says
# what's missing until they are.
NEEDED = ["prowlarrUrl", "prowlarrApiKey", "qbittorrentUrl"]


def check_settings(settings: dict[str, Any]) -> None:
    for name in NEEDED:
        if not settings.get(name):
            raise error(400, f"{SETTINGS_SCHEMA['properties'][name]['title']} is needed.", "invalid-settings")
    if settings.get("format", "best") not in retrieve.FORMATS:
        raise error(400, f"format must be one of {', '.join(retrieve.FORMATS)}.", "invalid-settings")
    if settings.get("afterwards", "keep") not in retrieve.AFTER:
        raise error(400, f"afterwards must be one of {', '.join(retrieve.AFTER)}.", "invalid-settings")


@app.get("/v1/manifest")
def manifest() -> dict[str, Any]:
    return MANIFEST


@app.post("/v1/checks/{check}")
def run_check(check: str, body: HealthRequest) -> dict[str, Any]:
    if check != "reflinks":
        raise error(404, f"This plugin has no check called {check}.", "unknown-check")
    passed, message = retrieve.check_reflinks(body.settings, Path(STAGING))
    return {"passed": passed, "message": message}


@app.post("/v1/health")
def health(body: HealthRequest) -> dict[str, Any]:
    settings = body.settings
    try:
        check_settings(settings)
    except HTTPException as e:
        return {"status": "unhealthy", "message": e.detail["error"]}
    try:
        with Prowlarr(settings) as prowlarr:
            version, indexers = prowlarr.status().get("version"), len(prowlarr.indexers())
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't reach Prowlarr: {e}"}
    try:
        with QBittorrent(settings) as qb:
            qb_version = qb.version()
    except Exception as e:
        return {"status": "unhealthy", "message": f"Couldn't reach qBittorrent: {e}"}
    message = f"Prowlarr {version} with {indexers} indexer{'s' if indexers != 1 else ''}, qBittorrent {qb_version}"
    folder = Path(str(settings.get("downloadsFolder") or settings.get("savePath") or "/downloads/melodex"))
    if not folder.parent.is_dir():
        return {"status": "degraded", "message": f"{message}, but this plugin can't see {folder.parent}; mount qBittorrent's downloads folder there."}
    if indexers == 0:
        return {"status": "degraded", "message": f"{message}; add indexers in Prowlarr to find anything."}
    return {"status": "healthy", "message": message}


@app.post("/v1/search")
def search(body: SearchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    try:
        candidates = retrieve.search(body.target, max(1, min(body.limit, 25)), body.settings)
    except PermissionError as e:
        raise error(502, str(e), "source-unavailable")
    except Exception as e:
        raise error(502, f"Prowlarr search failed: {e}", "source-unavailable")
    return {"candidates": dump(candidates)}


@app.post("/v1/fetch", status_code=202)
def fetch(body: FetchRequest) -> dict[str, Any]:
    check_settings(body.settings)
    directory = staging_directory(STAGING, body.staging_directory)
    job = jobs.start(body.job_id, lambda j: retrieve.fetch(j, body.candidate.candidate_id, body.target, directory, body.settings))
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
