import html
import io
import json
import subprocess
import time
import zipfile

import httpx
import pytest
from fastapi.testclient import TestClient

from app import bandcamp, main

SETTINGS = {"identity": "7%09secret"}


@pytest.fixture(scope="session")
def flac(tmp_path_factory) -> bytes:
    path = tmp_path_factory.mktemp("audio") / "made.flac"
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "3", "-y", str(path)],
                   check=True)
    return path.read_bytes()


DUMMY = {"id": 101, "title": "Dummy", "artist": "Portishead", "url": "https://portishead.bandcamp.com/album/dummy",
         "tracks": [("Mysterons", 306), ("Sour Times", 254), ("Strangers", 238)]}
THIRD = {"id": 102, "title": "Third", "artist": "Portishead", "url": "https://portishead.bandcamp.com/album/third",
         "tracks": [("Silence", 299), ("Hunter", 237)]}
GLORY_BOX = {"id": 201, "title": "Glory Box", "artist": "Portishead", "album": "Dummy",
             "url": "https://portishead.bandcamp.com/track/glory-box", "tracks": [("Glory Box", 306)]}


class FakeBandcamp:
    """Bandcamp's site and download servers, answering from the purchases a test gives it."""

    def __init__(self, flac, collection=(DUMMY, GLORY_BOX), hidden=(THIRD,), formats=("flac", "mp3-320"),
                 zip_names=None):
        self.flac, self.formats, self.zip_names = flac, formats, zip_names
        self.collection, self.hidden = list(collection), list(hidden)
        self.pages, self.downloads = [], []

    def _items(self, purchases):
        items, links = [], {}
        for p in purchases:
            kind = "a" if p["url"].split("/")[-2] == "album" else "t"
            items.append({"tralbum_type": kind, "tralbum_id": p["id"], "item_type": "album" if kind == "a" else "track",
                          "item_title": p["title"], "band_name": p["artist"], "item_url": p["url"],
                          "album_title": p.get("album"), "sale_item_type": "p", "sale_item_id": 9000 + p["id"]})
            links[f"p{9000 + p['id']}"] = f"https://bandcamp.com/download?from=collection&payment_id={p['id']}&sig=x"
        return items, links

    def _find(self, number):
        return next(p for p in self.collection + self.hidden if p["id"] == number)

    def __call__(self, request: httpx.Request) -> httpx.Response:
        url, params = request.url, request.url.params
        if url.host == "bandcamp.com" and url.path.startswith("/api/"):
            if request.headers.get("cookie") != f"identity={SETTINGS['identity']}":
                return httpx.Response(200, json={"error": True, "error_message": "not logged in"})
            if url.path == "/api/fan/2/collection_summary":
                return httpx.Response(200, json={"fan_id": 7, "collection_summary": {"username": "listener"}})
            body = json.loads(request.content)
            if url.path.endswith("/hidden_items"):
                items, links = self._items(self.hidden)
                return httpx.Response(200, json={"items": items, "redownload_urls": links, "more_available": False})
            # One purchase a page, to check every page is read.
            page = 0 if body["older_than_token"].endswith("::a::") else int(body["older_than_token"])
            items, links = self._items(self.collection[page:page + 1])
            more = page + 1 < len(self.collection)
            return httpx.Response(200, json={"items": items, "redownload_urls": links, "more_available": more,
                                             "last_token": str(page + 1)})
        if url.host.endswith(".bandcamp.com") and url.host != "bandcamp.com" and "/download/" not in url.path \
                and "/statdownload/" not in url.path:
            self.pages.append(str(url))
            purchase = next(p for p in self.collection + self.hidden if p["url"] == str(url))
            tralbum = {"current": {"title": purchase["title"]},
                       "trackinfo": [{"title": t, "track_num": n, "duration": d + 0.4, "artist": None}
                                     for n, (t, d) in enumerate(purchase["tracks"], 1)]}
            return httpx.Response(200, text=f'<script data-tralbum="{html.escape(json.dumps(tralbum))}"></script>')
        if url.host == "bandcamp.com" and url.path == "/download":
            purchase = self._find(int(params["payment_id"]))
            kind = "album" if purchase["url"].split("/")[-2] == "album" else "track"
            downloads = {f: {"url": f"https://popplers5.bandcamp.com/download/{kind}?enc={f}&id={purchase['id']}"}
                         for f in self.formats}
            blob = {"digital_items": [{"title": purchase["title"], "downloads": downloads}]}
            return httpx.Response(200, text=f'<div id="pagedata" data-blob="{html.escape(json.dumps(blob))}"></div>')
        if url.path.startswith("/statdownload/"):
            ready = str(url).replace("popplers5.bandcamp.com/statdownload/", "p4.bcbits.com/download/")
            return httpx.Response(200, text=json.dumps({"result": "ok", "download_url": ready}))
        if url.host == "p4.bcbits.com":
            purchase = self._find(int(params["id"]))
            self.downloads.append(purchase["title"])
            if url.path.endswith("/album"):
                data = io.BytesIO()
                with zipfile.ZipFile(data, "w") as zipped:
                    names = self.zip_names or [f"{purchase['artist']} - {purchase['title']} - {n:02} {t}.flac"
                                               for n, (t, _) in enumerate(purchase["tracks"], 1)]
                    for name in names:
                        zipped.writestr(name, self.flac)
                    zipped.writestr("cover.jpg", b"jpeg")
                return httpx.Response(200, content=data.getvalue(), headers={"content-type": "application/zip"})
            name = f"{purchase['artist']} - {purchase['title']}.flac"
            return httpx.Response(200, content=self.flac, headers={"content-disposition": f'attachment; filename="{name}"'})
        return httpx.Response(404)


@pytest.fixture
def client(monkeypatch, tmp_path):
    (tmp_path / "staging").mkdir()
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    monkeypatch.setattr(bandcamp, "_collections", {})
    return TestClient(main.app)


def use(monkeypatch, fake: FakeBandcamp) -> FakeBandcamp:
    transport = httpx.MockTransport(fake)
    monkeypatch.setattr(bandcamp, "http", lambda identity: httpx.Client(
        transport=transport, cookies={"identity": identity}, follow_redirects=True))
    return fake


def album_target(tracks=(("Mysterons", 306), ("Sour Times", 254), ("Strangers", 238))):
    return {"kind": "album", "title": "Dummy", "artist": "Portishead",
            "tracks": [{"disc": 1, "position": n, "title": t, "durationSeconds": d} for n, (t, d) in enumerate(tracks, 1)]}


def wait(client, job_id):
    for _ in range(300):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.02)
    raise TimeoutError(status)


def fetch(client, candidate, job_id, target=None):
    body = {"jobId": job_id, "target": target or album_target(), "candidate": candidate, "stagingDirectory": job_id,
            "settings": SETTINGS}
    assert client.post("/v1/fetch", json=body).status_code == 202
    return wait(client, job_id)


def test_manifest_keeps_the_cookie_write_only(client):
    body = client.get("/v1/manifest").json()
    assert body["id"] == "melodex.retrieval.bandcamp"
    assert body["settingsSchema"]["properties"]["identity"]["writeOnly"]
    assert "required" not in body["settingsSchema"]


def test_health_counts_every_purchase_including_hidden_ones(client, monkeypatch, flac):
    use(monkeypatch, FakeBandcamp(flac))
    assert client.post("/v1/health", json={"settings": {}}).json()["message"] == "The identity cookie is needed."
    refused = client.post("/v1/health", json={"settings": {"identity": "wrong"}}).json()
    assert refused["status"] == "unhealthy" and "identity cookie" in refused["message"]
    body = client.post("/v1/health", json={"settings": SETTINGS}).json()
    assert body == {"status": "healthy", "message": "Signed in as listener, with 2 albums and 1 song bought"}


def test_a_bought_album_is_found_and_fetched_track_by_track_from_its_zip(client, monkeypatch, tmp_path, flac):
    fake = use(monkeypatch, FakeBandcamp(flac))
    candidates = client.post("/v1/search", json={"target": album_target(), "settings": SETTINGS}).json()["candidates"]
    assert [c["title"] for c in candidates] == ["Dummy"]
    assert candidates[0]["source"] == "Bandcamp (FLAC)" and candidates[0]["format"] == "flac"
    assert candidates[0]["score"] > 0.9

    status = fetch(client, candidates[0], "job1")
    assert status["state"] == "completed", status
    assert [f["path"] for f in status["files"]] == ["1-01 Mysterons.flac", "1-02 Sour Times.flac", "1-03 Strangers.flac"]
    assert all(f["codec"] == "flac" for f in status["files"])
    # Only the tracks are left: no zip, cover or working folder.
    assert sorted(p.name for p in (tmp_path / "staging" / "job1").iterdir()) == [
        "1-01 Mysterons.flac", "1-02 Sour Times.flac", "1-03 Strangers.flac"]
    assert fake.downloads == ["Dummy"]


def test_an_album_you_havent_bought_isnt_offered(client, monkeypatch, flac):
    use(monkeypatch, FakeBandcamp(flac, collection=(GLORY_BOX,), hidden=()))
    target = {**album_target(), "title": "Dummy"}
    assert client.post("/v1/search", json={"target": target, "settings": SETTINGS}).json()["candidates"] == []


def test_a_song_bought_on_its_own_comes_as_one_file(client, monkeypatch, flac):
    use(monkeypatch, FakeBandcamp(flac))
    target = {"kind": "track", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "durationSeconds": 306}
    candidates = client.post("/v1/search", json={"target": target, "settings": SETTINGS}).json()["candidates"]
    assert candidates[0]["title"] == "Glory Box"
    status = fetch(client, candidates[0], "job2", target)
    assert status["state"] == "completed", status
    assert [f["path"] for f in status["files"]] == ["1-01 Glory Box.flac"]


def test_a_song_on_a_bought_album_is_taken_from_the_albums_download(client, monkeypatch, tmp_path, flac):
    fake = use(monkeypatch, FakeBandcamp(flac))
    target = {"kind": "track", "title": "Sour Times", "artist": "Portishead", "album": "Dummy", "durationSeconds": 254}
    candidates = client.post("/v1/search", json={"target": target, "settings": SETTINGS}).json()["candidates"]
    assert candidates[0]["title"] == "Sour Times" and candidates[0]["album"] == "Dummy"
    status = fetch(client, candidates[0], "job3", target)
    assert status["state"] == "completed", status
    assert [f["path"] for f in status["files"]] == ["1-02 Sour Times.flac"]
    assert [p.name for p in (tmp_path / "staging" / "job3").iterdir()] == ["1-02 Sour Times.flac"]
    assert fake.downloads == ["Dummy"]


def test_the_songs_of_an_album_asked_for_one_at_a_time_download_it_once(client, monkeypatch, tmp_path, flac):
    fake = use(monkeypatch, FakeBandcamp(flac))
    for job, (title, length) in [("job6", ("Mysterons", 306)), ("job7", ("Strangers", 238))]:
        target = {"kind": "track", "title": title, "artist": "Portishead", "album": "Dummy", "durationSeconds": length}
        candidate = client.post("/v1/search", json={"target": target, "settings": SETTINGS}).json()["candidates"][0]
        status = fetch(client, candidate, job, target)
        assert status["state"] == "completed", status
        assert [p.name for p in (tmp_path / "staging" / job).iterdir()] == [status["files"][0]["path"]]
    assert fake.downloads == ["Dummy"]

    # Once it's been kept long enough, it's downloaded again.
    monkeypatch.setattr(bandcamp, "_KEEP_DOWNLOADS_SECONDS", -1)
    fetch(client, candidate, "job8", target)
    assert fake.downloads == ["Dummy", "Dummy"]


def test_a_format_bandcamp_doesnt_offer_fails_the_fetch_and_says_what_it_does(client, monkeypatch, flac):
    use(monkeypatch, FakeBandcamp(flac, formats=("mp3-320",)))
    candidates = client.post("/v1/search", json={"target": album_target(), "settings": SETTINGS}).json()["candidates"]
    status = fetch(client, candidates[0], "job4")
    assert status["state"] == "failed"
    assert "doesn't offer Dummy as flac (it offers mp3-320)" in status["error"]


def test_files_in_the_zip_can_only_land_in_the_jobs_folder(client, monkeypatch, tmp_path, flac):
    names = ["../../01 escape.flac", "/etc/Portishead - Dummy - 02 Sour Times.flac",
             "Portishead - Dummy - 03 Strangers.flac", "Portishead - Dummy - 01 Mysterons.flac"]
    use(monkeypatch, FakeBandcamp(flac, zip_names=names))
    candidates = client.post("/v1/search", json={"target": album_target(), "settings": SETTINGS}).json()["candidates"]
    status = fetch(client, candidates[0], "job5")
    assert status["state"] == "completed", status
    assert not (tmp_path / "01 escape.flac").exists() and not (tmp_path / "staging" / "01 escape.flac").exists()
    assert len(status["files"]) == 3
