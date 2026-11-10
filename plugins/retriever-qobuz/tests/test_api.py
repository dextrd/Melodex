import hashlib
import subprocess
import time
from pathlib import Path

import httpx
import pytest
from fastapi.testclient import TestClient

from app import main, qobuz

APP_ID, SECRET = "100000001", "0123456789abcdef0123456789abcdef"
SETTINGS = {"appId": APP_ID, "appSecret": SECRET, "email": "listener@example.com", "password": "hunter2"}


@pytest.fixture(scope="session")
def flac(tmp_path_factory) -> bytes:
    path = tmp_path_factory.mktemp("audio") / "made.flac"
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "3", "-y", str(path)],
                   check=True)
    return path.read_bytes()


def make_album(album_id, title, upc, tracks, artist="Portishead", bits=24, rate=96.0, version=None):
    items = [{"id": f"{album_id}{n:02}", "title": t, "duration": d, "isrc": i, "track_number": n,
              "media_number": 1, "performer": {"name": artist}, "streamable": True,
              "maximum_bit_depth": bits, "maximum_sampling_rate": rate}
             for n, (t, d, i) in enumerate(tracks, 1)]
    return {"id": album_id, "title": title, "version": version, "upc": upc, "artist": {"name": artist},
            "tracks_count": len(items), "maximum_bit_depth": bits, "maximum_sampling_rate": rate, "streamable": True,
            "tracks": {"items": items}}


DUMMY = make_album("dummy1", "Dummy", "0042282855321", [
    ("Mysterons", 306, "GBAAN9400001"), ("Sour Times", 254, "GBAAN9400002"), ("Strangers", 238, "GBAAN9400003")])
DELUXE = make_album("dummy2", "Dummy", "0602547000001", [
    ("Mysterons", 306, "GBAAN9400001"), ("Sour Times", 254, "GBAAN9400002"), ("Strangers", 238, "GBAAN9400003"),
    ("Sour Times (Live)", 260, "GBAAN9400010")], version="Deluxe Edition")
SHORT = make_album("dummy3", "Dummy", "0602547000002", [("Mysterons", 306, "GBAAN9400001")], version="Sampler")


class FakeQobuz:
    """Qobuz's API and file server, answering from what a test gives it."""

    def __init__(self, albums, flac=b"", lossless=True, hires=True, samples=()):
        self.albums = {a["id"]: a for a in albums}
        self.flac, self.samples = flac, set(samples)
        self.lossless, self.hires = lossless, hires
        self.format_ids: list[int] = []
        self.logins, self.token, self.queries, self.files = 0, None, [], []

    def tracks(self):
        for album in self.albums.values():
            for track in album["tracks"]["items"]:
                yield track, album

    def __call__(self, request: httpx.Request) -> httpx.Response:
        path, params = request.url.path.removeprefix("/api.json/0.2"), request.url.params
        if request.url.host == "streaming.qobuz.test":
            self.files.append(path)
            return httpx.Response(200, content=self.flac, headers={"content-length": str(len(self.flac))})
        if request.headers.get("X-App-Id") != APP_ID:
            return httpx.Response(400, json={"message": "Invalid or missing app_id parameter"})
        if path == "/user/login":
            if params["password"] != hashlib.md5(b"hunter2").hexdigest():
                return httpx.Response(401, json={"message": "Invalid username/email and password combination"})
            self.logins += 1
            self.token = f"token{self.logins}"
            return httpx.Response(200, json={"user_auth_token": self.token, "user": {
                "display_name": "listener", "credential": {"parameters": {
                    "short_label": "Studio", "lossless_streaming": self.lossless, "hires_streaming": self.hires}}}})
        if request.headers.get("X-User-Auth-Token") != self.token:
            return httpx.Response(401, json={"message": "User authentication is required."})
        if path == "/album/search":
            self.queries.append(params["query"])
            q = params["query"]
            found = [a for a in self.albums.values() if q == a["upc"] or a["title"].lower() in q.lower()]
            return httpx.Response(200, json={"albums": {"items": [{k: v for k, v in a.items() if k != "tracks"} for a in found]}})
        if path == "/album/get":
            return httpx.Response(200, json=self.albums[params["album_id"]])
        if path == "/track/search":
            self.queries.append(params["query"])
            q = params["query"]
            found = [{**t, "album": {k: v for k, v in a.items() if k != "tracks"}} for t, a in self.tracks()
                     if q == t["isrc"] or t["title"].lower() in q.lower()]
            return httpx.Response(200, json={"tracks": {"items": found}})
        if path == "/track/getFileUrl":
            wanted = hashlib.md5(f"trackgetFileUrlformat_id{params['format_id']}intentstreamtrack_id{params['track_id']}"
                                 f"{params['request_ts']}{SECRET}".encode()).hexdigest()
            if params["request_sig"] != wanted:
                return httpx.Response(400, json={"message": "Invalid Request Signature parameter (request_sig)"})
            self.format_ids.append(int(params["format_id"]))
            if params["track_id"] in self.samples:
                return httpx.Response(200, json={"sample": True, "url": "https://streaming.qobuz.test/sample"})
            return httpx.Response(200, json={"url": f"https://streaming.qobuz.test/file/{params['track_id']}",
                                             "mime_type": "audio/flac", "format_id": int(params["format_id"])})
        if path == "/user/get":
            return httpx.Response(200, json={"display_name": "listener", "credential": {"parameters": {
                "short_label": "Studio", "lossless_streaming": self.lossless, "hires_streaming": self.hires}}})
        return httpx.Response(404)


@pytest.fixture
def client(monkeypatch, tmp_path):
    (tmp_path / "staging").mkdir()
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    monkeypatch.setattr(qobuz, "_tokens", {})
    return TestClient(main.app)


def use(monkeypatch, fake: FakeQobuz) -> FakeQobuz:
    transport = httpx.MockTransport(fake)
    monkeypatch.setattr(qobuz, "http", lambda: httpx.Client(base_url=qobuz.API, transport=transport))
    monkeypatch.setattr(qobuz, "download", lambda: httpx.Client(transport=transport))
    return fake


def album_target(upc=None, tracks=(("Mysterons", 306, "GBAAN9400001"), ("Sour Times", 254, "GBAAN9400002"),
                                   ("Strangers", 238, "GBAAN9400003"))):
    return {"kind": "album", "title": "Dummy", "artist": "Portishead",
            "ids": [{"provider": "upc", "value": upc}] if upc else [],
            "tracks": [{"disc": 1, "position": n, "title": t, "durationSeconds": d, "ids": [{"provider": "isrc", "value": i}]}
                       for n, (t, d, i) in enumerate(tracks, 1)]}


def wait(client, job_id):
    for _ in range(300):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.02)
    raise TimeoutError(status)


def test_manifest_keeps_secrets_write_only(client):
    body = client.get("/v1/manifest").json()
    assert body["id"] == "melodex.retrieval.qobuz"
    properties = body["settingsSchema"]["properties"]
    assert all(properties[name]["writeOnly"] for name in ["appSecret", "password", "userAuthToken"])
    assert "required" not in body["settingsSchema"]


def test_health_says_what_is_missing_then_who_is_signed_in(client, monkeypatch):
    use(monkeypatch, FakeQobuz([DUMMY]))
    assert client.post("/v1/health", json={"settings": {}}).json()["message"] == "App ID is needed."
    no_password = {k: v for k, v in SETTINGS.items() if k != "password"}
    assert "email and password" in client.post("/v1/health", json={"settings": no_password}).json()["message"]
    body = client.post("/v1/health", json={"settings": SETTINGS}).json()
    assert body == {"status": "healthy", "message": "Signed in as listener, Studio"}
    wrong = client.post("/v1/health", json={"settings": {**SETTINGS, "password": "nope"}}).json()
    assert wrong["status"] == "unhealthy" and "Invalid username" in wrong["message"]


def test_health_warns_when_the_subscription_does_not_stream_flac(client, monkeypatch):
    use(monkeypatch, FakeQobuz([DUMMY], lossless=False, hires=False))
    body = client.post("/v1/health", json={"settings": SETTINGS}).json()
    assert body["status"] == "degraded" and "samples" in body["message"]


def test_album_with_the_requested_barcode_comes_first_and_one_missing_a_track_is_not_offered(client, monkeypatch):
    fake = use(monkeypatch, FakeQobuz([DELUXE, SHORT, DUMMY]))
    body = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target(upc="042282855321")}).json()
    # Each album in each quality it streams in.
    offered = list(dict.fromkeys((c["title"], c["score"]) for c in body["candidates"]))
    assert [t for t, _ in offered] == ["Dummy", "Dummy (Deluxe Edition)"]
    assert offered[0][1] >= 0.99 and offered[1][1] < offered[0][1]
    first = body["candidates"][0]
    assert first["source"] == "Qobuz (24-bit/96 kHz)" and first["format"] == "flac"
    assert first["url"] == "https://open.qobuz.com/album/dummy1"
    assert fake.queries[0] == "042282855321"


def test_each_quality_up_to_the_settings_is_offered_and_fetched_as_chosen(client, monkeypatch, flac):
    fake = use(monkeypatch, FakeQobuz([DUMMY], flac=flac))
    found = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"]
    assert [(c["source"], c["format"], c.get("bitDepth"), c.get("sampleRateHz"), c.get("bitrateKbps")) for c in found] == [
        ("Qobuz (24-bit/96 kHz)", "flac", 24, 96000, None), ("Qobuz (16-bit/44.1 kHz)", "flac", 16, 44100, None),
        ("Qobuz (MP3 320)", "mp3", None, None, 320)]
    client.post("/v1/fetch", json={"settings": SETTINGS, "jobId": "cd", "target": album_target(), "candidate": found[1],
                                   "stagingDirectory": "cd"})
    assert wait(client, "cd")["state"] == "completed"
    assert set(fake.format_ids) == {6}


def test_cd_quality_and_mp3_are_labelled_as_what_they_fetch(client, monkeypatch):
    use(monkeypatch, FakeQobuz([DUMMY]))
    cd = client.post("/v1/search", json={"settings": {**SETTINGS, "quality": "cd"}, "target": album_target()}).json()
    assert cd["candidates"][0]["source"] == "Qobuz (16-bit/44.1 kHz)"
    mp3 = client.post("/v1/search", json={"settings": {**SETTINGS, "quality": "mp3"}, "target": album_target()}).json()
    assert mp3["candidates"][0]["source"] == "Qobuz (MP3 320)" and mp3["candidates"][0]["format"] == "mp3"


def test_song_found_by_its_isrc(client, monkeypatch):
    use(monkeypatch, FakeQobuz([DUMMY, DELUXE]))
    target = {"kind": "track", "title": "Sour Times", "artist": "Portishead", "album": "Dummy", "durationSeconds": 254,
              "ids": [{"provider": "isrc", "value": "GBAAN9400002"}]}
    body = client.post("/v1/search", json={"settings": SETTINGS, "target": target}).json()
    best = body["candidates"][0]
    assert best["title"] == "Sour Times" and best["score"] == 1.0 and best["album"] == "Dummy"
    # The live take has the words of the title in it, but isn't offered above the song.
    assert all(c["title"] != "Sour Times (Live)" or c["score"] < 0.9 for c in body["candidates"])


def test_album_download_signs_each_request_and_names_the_files(client, monkeypatch, flac):
    fake = use(monkeypatch, FakeQobuz([DUMMY], flac=flac))
    candidate = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"][0]
    started = client.post("/v1/fetch", json={"settings": SETTINGS, "jobId": "j1", "target": album_target(),
                                              "candidate": candidate, "stagingDirectory": "j1"}).json()
    assert started["jobId"] == "j1"
    done = wait(client, "j1")
    assert done["state"] == "completed", done
    assert [(f["path"], f["disc"], f["position"], f["codec"]) for f in done["files"]] == [
        ("1-01 Mysterons.flac", 1, 1, "flac"), ("1-02 Sour Times.flac", 1, 2, "flac"),
        ("1-03 Strangers.flac", 1, 3, "flac")]
    assert fake.files == ["/file/dummy101", "/file/dummy102", "/file/dummy103"]
    assert fake.logins == 1


def test_a_wrong_app_secret_and_a_sample_fail_the_fetch_with_why(client, monkeypatch, flac):
    use(monkeypatch, FakeQobuz([DUMMY], flac=flac, samples={"dummy102"}))
    candidate = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"][0]
    client.post("/v1/fetch", json={"settings": SETTINGS, "jobId": "j2", "target": album_target(),
                                   "candidate": candidate, "stagingDirectory": "j2"})
    assert "30-second sample" in wait(client, "j2")["error"]
    wrong = {**SETTINGS, "appSecret": "f" * 32}
    client.post("/v1/fetch", json={"settings": wrong, "jobId": "j3", "target": album_target(),
                                   "candidate": candidate, "stagingDirectory": "j3"})
    assert "app secret" in wait(client, "j3")["error"]


def test_a_sign_in_that_ran_out_is_made_again(client, monkeypatch):
    fake = use(monkeypatch, FakeQobuz([DUMMY]))
    client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()})
    fake.token = "expired elsewhere"
    body = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()
    assert body["candidates"] and fake.logins == 2
