import json
import shutil
import subprocess
import time
from pathlib import Path

import httpx
import pytest
from fastapi.testclient import TestClient

from app import archive, main


def flac_bytes(tmp_path: Path, seconds: float) -> bytes:
    path = tmp_path / "made.flac"
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", str(seconds),
                    "-y", str(path)], check=True)
    return path.read_bytes()


def concert(identifier="gd1991-04-04.flac16", restricted=False):
    """An item laid out the way the Live Music Archive's are: FLAC originals, each with MP3 and Ogg copies."""
    files = [{"name": "info.txt", "format": "Text", "source": "original"}]
    for number, (title, length) in enumerate([("Let The Good Times Roll ->", "250.5"), ("Feel Like A Stranger", "07:31")], 1):
        name = f"gd91-04-04d1t{number:02}"
        files += [
            {"name": f"{name}.flac", "format": "Flac", "source": "original", "title": title, "track": str(number),
             "length": length, "size": "4000000", "creator": "Grateful Dead", "album": "1991-04-04 - The Omni"},
            {"name": f"{name}.mp3", "format": "VBR MP3", "source": "derivative", "original": f"{name}.flac",
             "length": length, "size": "800000"},
            {"name": f"{name}.ogg", "format": "Ogg Vorbis", "source": "derivative", "original": f"{name}.flac", "size": "600000"},
        ]
    meta = {"identifier": identifier, "title": "Grateful Dead Live at The Omni on 1991-04-04", "creator": "Grateful Dead",
            "date": "1991-04-04", "collection": ["GratefulDead", "etree"]}
    if restricted:
        meta["access-restricted-item"] = "true"
    return {"metadata": meta, "files": files}


class FakeArchive:
    """archive.org's search, metadata and download, answering from what a test gives it."""

    def __init__(self, items, downloads=None):
        self.items, self.downloads, self.queries = items, downloads or {}, []

    def __call__(self, request: httpx.Request) -> httpx.Response:
        path = request.url.path
        if path == "/advancedsearch.php":
            self.queries.append(request.url.params["q"])
            rows = int(request.url.params["rows"])
            return httpx.Response(200, json={"response": {"docs": [{"identifier": i} for i in list(self.items)[:rows]]}})
        if path.startswith("/metadata/"):
            return httpx.Response(200, json=self.items.get(path.removeprefix("/metadata/"), {}))
        if path.startswith("/download/"):
            if (body := self.downloads.get(path.removeprefix("/download/"))) is None:
                return httpx.Response(403)
            return httpx.Response(200, content=body)
        return httpx.Response(404)


@pytest.fixture
def client(monkeypatch, tmp_path):
    (tmp_path / "staging").mkdir()
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    return TestClient(main.app)


def use(monkeypatch, fake: FakeArchive) -> FakeArchive:
    monkeypatch.setattr(archive, "client", lambda: httpx.Client(base_url=archive.API, transport=httpx.MockTransport(fake)))
    return fake


def wait(client, job_id):
    for _ in range(200):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.02)
    raise TimeoutError(status)


TRACK = {"kind": "track", "title": "Let the Good Times Roll", "artist": "Grateful Dead", "durationSeconds": 251}
ALBUM = {"kind": "album", "title": "1991‐04‐04: The Omni, Atlanta, GA", "artist": "Grateful Dead",
         "tracks": [{"disc": 1, "position": 1, "title": "Let the Good Times Roll", "durationSeconds": 251},
                    {"disc": 1, "position": 2, "title": "Feel Like a Stranger", "durationSeconds": 450}]}


def test_manifest_declares_the_contract(client):
    manifest = client.get("/v1/manifest").json()
    assert manifest["type"] == "retrieval"
    assert manifest["contractVersion"] == "1.0"
    assert set(manifest["capabilities"]) == {"fetch-track", "fetch-album", "cancel"}
    assert manifest["settingsSchema"]["properties"]["format"]["default"] == "flac"


@pytest.mark.parametrize("length, expected", [("250.5", 250.5), ("07:31", 451), ("1:02:03", 3723), ("", None), ("n/a", None)])
def test_lengths_read_as_seconds_or_minutes(length, expected):
    assert archive.seconds(length) == expected


def test_a_track_is_found_in_its_concert_as_flac(client, monkeypatch):
    fake = use(monkeypatch, FakeArchive({"gd1991-04-04.flac16": concert()}))
    candidates = client.post("/v1/search", json={"settings": {}, "target": TRACK}).json()["candidates"]
    best = candidates[0]
    assert best["title"] == "Let The Good Times Roll"
    assert best["format"] == "flac"
    assert best["url"] == "https://archive.org/details/gd1991-04-04.flac16"
    assert json.loads(best["candidateId"])["f"] == [[None, None, "gd91-04-04d1t01.flac", 4000000]]
    assert 'creator:("Grateful Dead")' in fake.queries[0] and "etree OR netlabels" in fake.queries[0]


def test_a_concert_is_found_by_the_date_in_the_albums_title(client, monkeypatch):
    fake = use(monkeypatch, FakeArchive({"gd1991-04-04.flac16": concert()}))
    candidates = client.post("/v1/search", json={"settings": {}, "target": ALBUM}).json()["candidates"]
    assert "date:1991-04-04" in fake.queries[0]
    assert len(candidates) == 1
    files = json.loads(candidates[0]["candidateId"])["f"]
    assert [(d, p, n) for d, p, n, _ in files] == [(1, 1, "gd91-04-04d1t01.flac"), (1, 2, "gd91-04-04d1t02.flac")]
    assert candidates[0]["score"] >= 0.9


def test_an_album_missing_a_track_isnt_offered(client, monkeypatch):
    use(monkeypatch, FakeArchive({"gd1991-04-04.flac16": concert()}))
    target = {**ALBUM, "tracks": ALBUM["tracks"] + [{"disc": 1, "position": 3, "title": "Shakedown Street", "durationSeconds": 900}]}
    assert client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"] == []


def test_without_a_tracklist_only_a_close_title_is_the_album(client, monkeypatch):
    item = concert("gv499")
    item["metadata"].update(title="[GV-499] EugeneKha - Wolf Sky", creator="EugeneKha", date="2014")
    use(monkeypatch, FakeArchive({"gv499": item}))
    def offered(title):
        target = {"kind": "album", "title": title, "artist": "EugeneKha"}
        return client.post("/v1/search", json={"settings": {}, "target": target}).json()["candidates"]
    assert offered("Low Sky") == []
    assert len(offered("Wolf Sky")) == 1


def test_the_format_setting_picks_the_archives_copy(client, monkeypatch):
    use(monkeypatch, FakeArchive({"gd1991-04-04.flac16": concert()}))
    candidates = client.post("/v1/search", json={"settings": {"format": "mp3"}, "target": TRACK}).json()["candidates"]
    assert candidates[0]["format"] == "mp3"
    assert json.loads(candidates[0]["candidateId"])["f"][0][2] == "gd91-04-04d1t01.mp3"


def test_flac_only_skips_items_without_lossless_files(client, monkeypatch):
    item = concert()
    item["files"] = [f for f in item["files"] if f["format"] != "Flac"] + [
        {"name": "gd91-04-04d1t01.mp3", "format": "VBR MP3", "source": "original", "title": "Let The Good Times Roll", "length": "250"}]
    use(monkeypatch, FakeArchive({"gd1991-04-04.mp3": item}))
    assert client.post("/v1/search", json={"settings": {}, "target": TRACK}).json()["candidates"] == []
    best = client.post("/v1/search", json={"settings": {"format": "best"}, "target": TRACK}).json()["candidates"]
    assert best[0]["format"] == "mp3"


def test_stream_only_items_are_left_out(client, monkeypatch):
    use(monkeypatch, FakeArchive({"gd1991-04-04.stream": concert("gd1991-04-04.stream", restricted=True)}))
    assert client.post("/v1/search", json={"settings": {}, "target": TRACK}).json()["candidates"] == []


def test_a_bad_format_is_refused(client):
    response = client.post("/v1/search", json={"settings": {"format": "wav"}, "target": TRACK})
    assert response.status_code == 400
    assert response.json()["code"] == "invalid-settings"


def test_the_archive_being_down_is_reported(client, monkeypatch):
    monkeypatch.setattr(archive, "client", lambda: httpx.Client(base_url=archive.API, transport=httpx.MockTransport(lambda r: httpx.Response(503))))
    response = client.post("/v1/search", json={"settings": {}, "target": TRACK})
    assert response.status_code == 502
    assert response.json()["code"] == "source-unavailable"
    assert client.post("/v1/health", json={"settings": {}}).json()["status"] == "unhealthy"


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg to make test files")
def test_an_album_is_downloaded_to_staging_with_its_positions(client, monkeypatch, tmp_path):
    item = concert()
    body = flac_bytes(tmp_path, 3)
    use(monkeypatch, FakeArchive({"gd1991-04-04.flac16": item}, {
        "gd1991-04-04.flac16/gd91-04-04d1t01.flac": body, "gd1991-04-04.flac16/gd91-04-04d1t02.flac": body}))
    candidate = client.post("/v1/search", json={"settings": {}, "target": ALBUM}).json()["candidates"][0]

    started = client.post("/v1/fetch", json={"settings": {}, "jobId": "job-1", "target": ALBUM, "candidate": candidate,
                                             "stagingDirectory": "job-1"})
    assert started.status_code == 202
    status = wait(client, "job-1")

    assert status["state"] == "completed", status
    files = sorted(status["files"], key=lambda f: f["position"])
    assert [(f["path"], f["disc"], f["position"], f["codec"]) for f in files] == [
        ("gd91-04-04d1t01.flac", 1, 1, "flac"), ("gd91-04-04d1t02.flac", 1, 2, "flac")]
    assert (tmp_path / "staging" / "job-1" / "gd91-04-04d1t02.flac").read_bytes() == body


def test_a_file_the_archive_refuses_fails_the_job(client, monkeypatch):
    use(monkeypatch, FakeArchive({"gd1991-04-04.flac16": concert()}))
    candidate = client.post("/v1/search", json={"settings": {}, "target": TRACK}).json()["candidates"][0]
    client.post("/v1/fetch", json={"settings": {}, "jobId": "job-2", "target": TRACK, "candidate": candidate, "stagingDirectory": "job-2"})
    status = wait(client, "job-2")
    assert status["state"] == "failed"
    assert "doesn't allow" in status["error"]


def test_an_unknown_job_is_reported(client):
    assert client.get("/v1/fetch/nope").json()["code"] == "unknown-job"
    assert client.delete("/v1/fetch/nope").status_code == 404
