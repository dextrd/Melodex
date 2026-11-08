"""Prowlarr's and qBittorrent's HTTP APIs, as far as this plugin uses them."""

from __future__ import annotations

from typing import Any
from urllib.parse import parse_qsl, urlencode, urlparse, urlunparse

import httpx

USER_AGENT = "Melodex-retriever-prowlarr/0.1 (+https://github.com/joeatethebeans/Melodex)"
AUDIO_CATEGORY = 3000


def http(base_url: str, headers: dict[str, str]) -> httpx.Client:
    """The HTTP client every call goes through; tests swap in a fake one."""
    return httpx.Client(base_url=base_url, headers={"User-Agent": USER_AGENT, **headers}, timeout=30)


class Prowlarr:
    def __init__(self, settings: dict[str, Any]):
        self.url = str(settings.get("prowlarrUrl") or "").rstrip("/")
        self.key = str(settings.get("prowlarrApiKey") or "")
        self.client = http(self.url, {"X-Api-Key": self.key})

    def __enter__(self) -> Prowlarr:
        return self

    def __exit__(self, *_: Any) -> None:
        self.client.close()

    def status(self) -> dict[str, Any]:
        return self._get("/api/v1/system/status")

    def indexers(self) -> list[dict[str, Any]]:
        return [i for i in self._get("/api/v1/indexer") if i.get("enable")]

    def search(self, query: str) -> list[dict[str, Any]]:
        params = {"query": query, "type": "search", "categories": AUDIO_CATEGORY, "limit": 100}
        response = self.client.get("/api/v1/search", params=params, timeout=90)
        response.raise_for_status()
        return [r for r in response.json() if r.get("protocol") == "torrent"]

    def torrent(self, link: str) -> bytes | str:
        """The .torrent file behind a release's download link, or the magnet link it redirects to."""
        response = self.client.get(with_key(link, self.key))
        if response.is_redirect and (location := response.headers.get("location", "")).startswith("magnet:"):
            return location
        response.raise_for_status()
        return response.content

    def _get(self, path: str) -> Any:
        response = self.client.get(path)
        if response.status_code == 401:
            raise PermissionError("Prowlarr refused the API key.")
        response.raise_for_status()
        return response.json()


def without_key(link: str) -> str:
    """Prowlarr puts its API key in download links; it's left out of what Melodex stores and added back to fetch."""
    parts = urlparse(link)
    return urlunparse(parts._replace(query=urlencode([(k, v) for k, v in parse_qsl(parts.query) if k != "apikey"])))


def with_key(link: str, key: str) -> str:
    parts = urlparse(link)
    query = [(k, v) for k, v in parse_qsl(parts.query) if k != "apikey"]
    return urlunparse(parts._replace(query=urlencode([("apikey", key), *query]))) if key else link


class QBittorrent:
    def __init__(self, settings: dict[str, Any]):
        self.url = str(settings.get("qbittorrentUrl") or "").rstrip("/")
        # qBittorrent turns away requests whose Referer isn't its own address.
        self.client = http(self.url + "/api/v2", {"Referer": self.url})
        username = str(settings.get("qbittorrentUsername") or "")
        if username:
            response = self.client.post("/auth/login", data={"username": username,
                                                             "password": str(settings.get("qbittorrentPassword") or "")})
            if response.status_code == 403:
                raise PermissionError("qBittorrent has turned this address away after too many failed sign-ins. Restart "
                                      "qBittorrent, or wait (an hour, unless it's set otherwise), then try again.")
            # Up to 5.1 it answers "Ok." or "Fails."; from 5.2, 204 with nothing, or 401.
            if response.status_code == 401 or response.text.strip() == "Fails.":
                raise PermissionError("qBittorrent refused the username and password.")
            response.raise_for_status()

    def __enter__(self) -> QBittorrent:
        return self

    def __exit__(self, *_: Any) -> None:
        self.client.close()

    def version(self) -> str:
        response = self.client.get("/app/version")
        if response.status_code == 403:
            raise PermissionError("qBittorrent wants a username and password.")
        response.raise_for_status()
        return response.text.strip()

    def add(self, torrent: bytes | str, save_path: str, category: str, tags: str) -> None:
        # qBittorrent 5 calls it stopped, 4 paused; each ignores the other.
        data = {"savepath": save_path, "category": category, "tags": tags, "stopped": "true", "paused": "true"}
        if isinstance(torrent, str):
            response = self.client.post("/torrents/add", data={**data, "urls": torrent})
        else:
            response = self.client.post("/torrents/add", data=data,
                                        files={"torrents": ("melodex.torrent", torrent, "application/x-bittorrent")})
        response.raise_for_status()
        if response.text.strip() == "Fails.":
            raise RuntimeError("qBittorrent wouldn't add the torrent.")

    def info(self, info_hash: str) -> dict[str, Any] | None:
        response = self.client.get("/torrents/info", params={"hashes": info_hash})
        response.raise_for_status()
        found = response.json()
        return found[0] if found else None

    def files(self, info_hash: str) -> list[dict[str, Any]]:
        response = self.client.get("/torrents/files", params={"hash": info_hash})
        if response.status_code == 404:
            return []
        response.raise_for_status()
        return response.json()

    def priority(self, info_hash: str, indexes: list[int], priority: int) -> None:
        if indexes:
            self.client.post("/torrents/filePrio", data={"hash": info_hash, "id": "|".join(map(str, indexes)),
                                                         "priority": priority}).raise_for_status()

    def start(self, info_hash: str) -> None:
        response = self.client.post("/torrents/start", data={"hashes": info_hash})
        if response.status_code == 404:  # qBittorrent 4
            response = self.client.post("/torrents/resume", data={"hashes": info_hash})
        response.raise_for_status()

    def delete(self, info_hash: str, files: bool) -> None:
        self.client.post("/torrents/delete", data={"hashes": info_hash, "deleteFiles": str(files).lower()}).raise_for_status()
