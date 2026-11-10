import base64
import json
import subprocess
import time

import httpx
import pytest
from fastapi.testclient import TestClient

from app import main, tidal

SETTINGS = {"clientId": "client1", "clientSecret": "secret1"}


def run_ffmpeg(*args):
    subprocess.run(["ffmpeg", "-v", "error", "-y", *args], check=True)


@pytest.fixture(scope="session")
def audio(tmp_path_factory):
    folder = tmp_path_factory.mktemp("audio")
    run_ffmpeg("-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "3", str(folder / "made.flac"))
    run_ffmpeg("-i", str(folder / "made.flac"), "-c:a", "flac", "-strict", "-2",
               "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "500000",
               str(folder / "made.mp4"))
    return {"flac": (folder / "made.flac").read_bytes(), "mp4": (folder / "made.mp4").read_bytes()}


def make_album(album_id, title, upc, tracks, artist="Portishead", tags=("LOSSLESS", "HIRES_LOSSLESS")):
    items = [{"id": album_id * 100 + n, "title": t, "duration": d, "isrc": i, "trackNumber": n, "volumeNumber": 1,
              "artists": [{"name": artist}], "album": {"id": album_id, "title": title}, "allowStreaming": True,
              "audioQuality": "LOSSLESS", "mediaMetadata": {"tags": list(tags)}}
             for n, (t, d, i) in enumerate(tracks, 1)]
    return {"id": album_id, "title": title, "upc": upc, "artists": [{"name": artist}], "numberOfTracks": len(items),
            "allowStreaming": True, "audioQuality": "LOSSLESS", "mediaMetadata": {"tags": list(tags)}, "items": items}


DUMMY = make_album(11, "Dummy", "042282855321", [
    ("Mysterons", 306, "GBAAN9400001"), ("Sour Times", 254, "GBAAN9400002"), ("Strangers", 238, "GBAAN9400003")])


class FakeTidal:
    """Tidal's sign-in, API and stream servers, answering from what a test gives it."""

    def __init__(self, albums, audio=None, stream="bts", encrypted=False, preview=False):
        self.albums = {a["id"]: a for a in albums}
        self.audio, self.kind, self.encrypted, self.preview = audio or {}, stream, encrypted, preview
        self.approved, self.access, self.refreshes, self.segments = False, None, 0, []
        self.qualities: list[str | None] = []

    def _token(self, number):
        self.access = f"access{number}"
        return httpx.Response(200, json={"access_token": self.access, "refresh_token": "refresh1", "expires_in": 3600,
                                         "user": {"userId": 7, "countryCode": "GB", "username": "listener"}})

    def __call__(self, request: httpx.Request) -> httpx.Response:
        host, path = request.url.host, request.url.path
        if host == "auth.tidal.com":
            form = dict(httpx.QueryParams(request.content.decode()))
            if form.get("client_id") != "client1":
                return httpx.Response(401, json={"error": "invalid_client", "error_description": "Client not found"})
            if path.endswith("/device_authorization"):
                return httpx.Response(200, json={"deviceCode": "device1", "userCode": "ABCDE",
                                                 "verificationUriComplete": "link.tidal.com/ABCDE", "expiresIn": 300,
                                                 "interval": 0.01})
            if form["grant_type"] == "refresh_token":
                self.refreshes += 1
                return self._token(10 + self.refreshes)
            if not self.approved:
                return httpx.Response(400, json={"error": "authorization_pending"})
            return self._token(1)
        if host == "streams.tidal.test":
            if path.startswith("/seg/"):
                self.segments.append(path)
                data, n = self.audio["mp4"], int(path.rsplit("/", 1)[1])
                third = len(data) // 3
                return httpx.Response(200, content=data[n * third:(n + 1) * third if n < 2 else len(data)])
            return httpx.Response(200, content=self.audio["flac"])
        if request.headers.get("Authorization") != f"Bearer {self.access}":
            return httpx.Response(401, json={"userMessage": "The token has expired."})
        path = path.removeprefix("/v1")
        if path == "/users/7/subscription":
            return httpx.Response(200, json={"subscription": {"type": "HIFI_PLUS"}, "highestSoundQuality": "HI_RES_LOSSLESS"})
        assert request.url.params["countryCode"] == "GB"
        if path == "/search":
            q = request.url.params["query"].lower()
            if request.url.params["types"] == "ALBUMS":
                found = [{k: v for k, v in a.items() if k != "items"} for a in self.albums.values() if a["title"].lower() in q]
                return httpx.Response(200, json={"albums": {"items": found}})
            found = [t for a in self.albums.values() for t in a["items"] if t["title"].lower() in q]
            return httpx.Response(200, json={"tracks": {"items": found}})
        if path.startswith("/albums/"):
            album = self.albums[int(path.split("/")[2])]
            if path.endswith("/tracks"):
                return httpx.Response(200, json={"items": album["items"], "totalNumberOfItems": len(album["items"])})
            return httpx.Response(200, json={k: v for k, v in album.items() if k != "items"})
        if path.endswith("/playbackinfopostpaywall"):
            track = path.split("/")[2]
            self.qualities.append(request.url.params.get("audioquality"))
            if self.kind == "bts":
                manifest = {"mimeType": "audio/flac", "codecs": "flac", "urls": [f"https://streams.tidal.test/{track}.flac"],
                            "encryptionType": "OLD_AES" if self.encrypted else "NONE"}
                body, kind = json.dumps(manifest).encode(), "application/vnd.tidal.bts"
            else:
                protection = '<ContentProtection schemeIdUri="urn:uuid:edef8ba9"/>' if self.encrypted else ""
                body = f'''<?xml version="1.0"?><MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period><AdaptationSet>
                    {protection}<Representation id="FLAC,96000,24" codecs="flac" bandwidth="4000000">
                    <SegmentTemplate initialization="https://streams.tidal.test/seg/{track}/0"
                        media="https://streams.tidal.test/seg/{track}/$Number$" startNumber="1">
                    <SegmentTimeline><S d="100" r="1"/></SegmentTimeline></SegmentTemplate>
                    </Representation></AdaptationSet></Period></MPD>'''.encode()
                kind = "application/dash+xml"
            return httpx.Response(200, json={"trackId": int(track), "assetPresentation": "PREVIEW" if self.preview else "FULL",
                                             "manifestMimeType": kind, "manifest": base64.b64encode(body).decode()})
        return httpx.Response(404)


@pytest.fixture
def client(monkeypatch, tmp_path):
    (tmp_path / "staging").mkdir()
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    monkeypatch.setattr(tidal, "DATA", tmp_path / "data")
    monkeypatch.setattr(tidal, "_waiting", {})
    monkeypatch.setattr(tidal, "_failed", {})
    return TestClient(main.app)


def use(monkeypatch, fake: FakeTidal) -> FakeTidal:
    transport = httpx.MockTransport(fake)
    monkeypatch.setattr(tidal, "http", lambda base_url: httpx.Client(base_url=base_url, transport=transport))
    monkeypatch.setattr(tidal, "download", lambda: httpx.Client(transport=transport))
    return fake


def signed_in(client, fake):
    client.post("/v1/health", json={"settings": SETTINGS})
    fake.approved = True
    for _ in range(200):
        if tidal.saved("client1"):
            return
        time.sleep(0.01)
    raise TimeoutError("never signed in")


def album_target():
    tracks = [("Mysterons", 306, "GBAAN9400001"), ("Sour Times", 254, "GBAAN9400002"), ("Strangers", 238, "GBAAN9400003")]
    return {"kind": "album", "title": "Dummy", "artist": "Portishead",
            "tracks": [{"disc": 1, "position": n, "title": t, "durationSeconds": d, "ids": [{"provider": "isrc", "value": i}]}
                       for n, (t, d, i) in enumerate(tracks, 1)]}


def fetched(client, job_id, candidate):
    client.post("/v1/fetch", json={"settings": SETTINGS, "jobId": job_id, "target": album_target(),
                                   "candidate": candidate, "stagingDirectory": job_id})
    for _ in range(500):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.02)
    raise TimeoutError(status)


def test_manifest_keeps_secrets_write_only(client):
    properties = client.get("/v1/manifest").json()["settingsSchema"]["properties"]
    assert properties["clientSecret"]["writeOnly"] and properties["refreshToken"]["writeOnly"]


def test_health_gives_the_link_to_sign_in_and_then_who_is_signed_in(client, monkeypatch):
    fake = use(monkeypatch, FakeTidal([DUMMY]))
    assert client.post("/v1/health", json={"settings": {}}).json()["message"] == "Client ID is needed."
    waiting = client.post("/v1/health", json={"settings": SETTINGS}).json()
    assert waiting["status"] == "unhealthy"
    assert "https://link.tidal.com/ABCDE" in waiting["message"] and "ABCDE" in waiting["message"]
    # Asking again gives the same code rather than starting over.
    assert client.post("/v1/health", json={"settings": SETTINGS}).json() == waiting
    signed_in(client, fake)
    body = client.post("/v1/health", json={"settings": SETTINGS}).json()
    assert body == {"status": "healthy", "message": "Signed in as listener, HIFI_PLUS"}


def test_a_refused_client_id_says_so(client, monkeypatch):
    use(monkeypatch, FakeTidal([DUMMY]))
    body = client.post("/v1/health", json={"settings": {"clientId": "other"}}).json()
    assert body["status"] == "unhealthy" and "Client not found" in body["message"]


def test_searches_before_signing_in_say_how(client, monkeypatch):
    use(monkeypatch, FakeTidal([DUMMY]))
    body = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()})
    assert body.status_code == 502 and "Not signed in" in body.json()["error"]


def test_album_offered_with_its_quality_and_an_expired_token_refreshed(client, monkeypatch):
    fake = use(monkeypatch, FakeTidal([DUMMY]))
    signed_in(client, fake)
    fake.access = "rotated elsewhere"
    body = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()
    best = body["candidates"][0]
    assert (best["title"], best["score"], best["source"]) == ("Dummy", 1.0, "Tidal (hi-res FLAC)")
    assert fake.refreshes == 1 and tidal.saved("client1")["access"] == fake.access


def test_each_quality_up_to_the_settings_is_offered_and_asked_for_when_fetched(client, monkeypatch, audio):
    fake = use(monkeypatch, FakeTidal([DUMMY], audio))
    signed_in(client, fake)
    found = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"]
    assert [(c["source"], c["format"], c.get("bitDepth"), c.get("bitrateKbps")) for c in found] == [
        ("Tidal (hi-res FLAC)", "flac", 24, None), ("Tidal (16-bit/44.1 kHz FLAC)", "flac", 16, None),
        ("Tidal (AAC 320)", "aac", None, 320)]
    assert fetched(client, "cd", found[1])["state"] == "completed"
    assert set(fake.qualities) == {"LOSSLESS"}


def test_a_refresh_token_in_the_settings_needs_no_sign_in(client, monkeypatch):
    fake = use(monkeypatch, FakeTidal([DUMMY]))
    body = client.post("/v1/health", json={"settings": {**SETTINGS, "refreshToken": "refresh1"}}).json()
    assert body["status"] == "healthy" and fake.refreshes == 1


def test_album_download_from_single_files(client, monkeypatch, audio):
    fake = use(monkeypatch, FakeTidal([DUMMY], audio))
    signed_in(client, fake)
    candidate = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"][0]
    done = fetched(client, "j1", candidate)
    assert done["state"] == "completed", done
    assert [(f["path"], f["codec"]) for f in done["files"]] == [
        ("1-01 Mysterons.flac", "flac"), ("1-02 Sour Times.flac", "flac"), ("1-03 Strangers.flac", "flac")]


def test_hi_res_segments_joined_into_flac(client, monkeypatch, audio, tmp_path):
    fake = use(monkeypatch, FakeTidal([DUMMY], audio, stream="dash"))
    signed_in(client, fake)
    candidate = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"][0]
    done = fetched(client, "j2", candidate)
    assert done["state"] == "completed", done
    assert done["files"][0]["codec"] == "flac" and abs(done["files"][0]["durationSeconds"] - 3) < 0.1
    assert fake.segments[:3] == ["/seg/1101/0", "/seg/1101/1", "/seg/1101/2"]
    assert sorted(p.name for p in (tmp_path / "staging" / "j2").iterdir()) == [
        "1-01 Mysterons.flac", "1-02 Sour Times.flac", "1-03 Strangers.flac"]


@pytest.mark.parametrize("stream", ["bts", "dash"])
def test_encrypted_streams_are_left_alone(client, monkeypatch, audio, stream):
    fake = use(monkeypatch, FakeTidal([DUMMY], audio, stream=stream, encrypted=True))
    signed_in(client, fake)
    candidate = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"][0]
    done = fetched(client, f"enc-{stream}", candidate)
    assert done["state"] == "failed" and "doesn't decrypt" in done["error"]
    assert not fake.segments


def test_a_preview_fails_the_fetch(client, monkeypatch, audio):
    fake = use(monkeypatch, FakeTidal([DUMMY], audio, preview=True))
    signed_in(client, fake)
    candidate = client.post("/v1/search", json={"settings": SETTINGS, "target": album_target()}).json()["candidates"][0]
    assert "30-second preview" in fetched(client, "j3", candidate)["error"]
