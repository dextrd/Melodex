import errno
import hashlib
import json
import shutil
import subprocess
import time
from pathlib import Path
from urllib.parse import parse_qs

import httpx
import pytest
from fastapi.testclient import TestClient

from app import clients, main, retrieve, torrent


def bencode(value) -> bytes:
    if isinstance(value, int):
        return b"i%de" % value
    if isinstance(value, str):
        value = value.encode()
    if isinstance(value, bytes):
        return b"%d:%s" % (len(value), value)
    if isinstance(value, list):
        return b"l" + b"".join(bencode(v) for v in value) + b"e"
    return b"d" + b"".join(bencode(k) + bencode(value[k]) for k in sorted(value)) + b"e"


def make_torrent(name: str, files: list[tuple[str, int]]) -> tuple[bytes, str]:
    info = {"name": name, "piece length": 16384, "pieces": b"\0" * 20,
            "files": [{"path": path.split("/"), "length": size} for path, size in files]}
    return bencode({"announce": "http://tracker.invalid/announce", "info": info}), hashlib.sha1(bencode(info)).hexdigest()


DUMMY = [("01 - Portishead - Mysterons.flac", 30_000_000), ("02 - Portishead - Sour Times.flac", 25_000_000),
         ("03 - Portishead - Strangers.flac", 24_000_000), ("folder.jpg", 50_000)]
DUMMY_FLAC, DUMMY_HASH = make_torrent("Portishead - Dummy (1994) [FLAC]", [(n, s) for n, s in DUMMY])
DUMMY_MP3, DUMMY_MP3_HASH = make_torrent("Portishead - Dummy (1994) [MP3 320]", [(n.replace(".flac", ".mp3"), s // 4) for n, s in DUMMY])
DISCOGRAPHY, DISCOGRAPHY_HASH = make_torrent("Portishead - Discography", [("Dummy/" + n, s) for n, s in DUMMY[:2]])

ALBUM = {"kind": "album", "title": "Dummy", "artist": "Portishead",
         "tracks": [{"disc": 1, "position": 1, "title": "Mysterons", "durationSeconds": 302},
                    {"disc": 1, "position": 2, "title": "Sour Times", "durationSeconds": 254},
                    {"disc": 1, "position": 3, "title": "Strangers", "durationSeconds": 238}]}
TRACK = {"kind": "track", "title": "Sour Times", "artist": "Portishead", "album": "Dummy", "durationSeconds": 254}
SETTINGS = {"prowlarrUrl": "http://prowlarr:9696", "prowlarrApiKey": "test-key", "qbittorrentUrl": "http://qbittorrent:8080"}


def release(title, link=None, magnet=None, info_hash=None, seeders=5, protocol="torrent"):
    return {"title": title, "indexer": "Test tracker", "seeders": seeders, "protocol": protocol, "infoHash": info_hash,
            "downloadUrl": link, "magnetUrl": magnet, "infoUrl": f"https://tracker.invalid/{title}"}


class FakeProwlarr:
    def __init__(self, results, torrents):
        self.results, self.torrents, self.queries, self.keys = results, torrents, [], []

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.keys.append(request.headers.get("X-Api-Key") or request.url.params.get("apikey"))
        if request.url.path == "/api/v1/search":
            self.queries.append(request.url.params["query"])
            return httpx.Response(200, json=self.results)
        if request.url.path == "/api/v1/system/status":
            return httpx.Response(200, json={"version": "2.6.5"})
        if request.url.path == "/api/v1/indexer":
            return httpx.Response(200, json=[{"name": "Test tracker", "enable": True}])
        if request.url.path.startswith("/1/download"):
            got = self.torrents.get(request.url.params["file"])
            if isinstance(got, str):
                return httpx.Response(301, headers={"location": got})
            return httpx.Response(200, content=got) if got else httpx.Response(404)
        return httpx.Response(404)


class FakeQBittorrent:
    """qBittorrent with torrents that finish downloading as soon as they're started."""

    def __init__(self, folder: Path, torrents: dict[str, bytes]):
        self.folder, self.known, self.added, self.deleted, self.priorities = folder, torrents, {}, [], {}

    def __call__(self, request: httpx.Request) -> httpx.Response:
        path = request.url.path.removeprefix("/api/v2")
        form = parse_qs(request.content.decode(errors="replace")) if request.method == "POST" and b"Content-Disposition" not in request.content else {}
        if path == "/app/version":
            return httpx.Response(200, text="v5.1.4")
        if path == "/torrents/add":
            body = request.content
            data = next(t for t in self.known.values() if t in body)
            info_hash, files = torrent.read(data)
            self.added[info_hash] = {"files": files, "started": False, "body": body}
            return httpx.Response(200, text="Ok.")
        if path == "/torrents/info":
            h = request.url.params["hashes"]
            t = self.added.get(h)
            return httpx.Response(200, json=[{"hash": h, "state": "uploading" if t and t["started"] else "stoppedDL",
                                              "save_path": "/downloads/melodex"}] if t else [])
        if path == "/torrents/files":
            t = self.added.get(request.url.params["hash"])
            if not t:
                return httpx.Response(404)
            prio = self.priorities.get(request.url.params["hash"], {})
            return httpx.Response(200, json=[{"index": f.index, "name": f.path, "size": f.size, "priority": prio.get(f.index, 1),
                                              "progress": 1 if t["started"] and prio.get(f.index, 1) else 0} for f in t["files"]])
        if path == "/torrents/filePrio":
            h = form["hash"][0]
            for i in form["id"][0].split("|"):
                self.priorities.setdefault(h, {})[int(i)] = int(form["priority"][0])
            return httpx.Response(200)
        if path == "/torrents/start":
            t = self.added[form["hashes"][0]]
            t["started"] = True
            for f in t["files"]:
                if self.priorities.get(form["hashes"][0], {}).get(f.index, 1):
                    (self.folder / f.path).parent.mkdir(parents=True, exist_ok=True)
                    (self.folder / f.path).write_bytes(b"audio " + f.path.encode())
            return httpx.Response(200)
        if path == "/torrents/delete":
            self.deleted.append((form["hashes"][0], form["deleteFiles"][0]))
            self.added.pop(form["hashes"][0], None)
            return httpx.Response(200)
        return httpx.Response(404)


@pytest.fixture
def fakes(monkeypatch, tmp_path):
    (tmp_path / "staging").mkdir()
    monkeypatch.setattr(main, "STAGING", str(tmp_path / "staging"))
    monkeypatch.setattr(retrieve, "POLL_SECONDS", 0.01)
    # The plugin probes what it copies; the fake downloads aren't real audio.
    monkeypatch.setattr(retrieve, "probe", lambda path, directory, disc, position: {"path": path.name, "disc": disc, "position": position})
    torrents = {"dummy-flac": DUMMY_FLAC, "dummy-mp3": DUMMY_MP3, "discography": DISCOGRAPHY}
    prowlarr = FakeProwlarr([
        release("Portishead - Dummy (1994) [MP3 320]", "http://prowlarr:9696/1/download?apikey=test-key&file=dummy-mp3", seeders=40),
        release("Portishead - Dummy (1994) [FLAC]", "http://prowlarr:9696/1/download?apikey=test-key&file=dummy-flac", seeders=12),
        release("Portishead - Discography", "http://prowlarr:9696/1/download?apikey=test-key&file=discography", seeders=3),
        release("Portishead - Dummy (1994) [usenet]", "http://prowlarr:9696/1/download?file=nzb", protocol="usenet"),
        release("Portishead - Dummy (1994) [dead]", "http://prowlarr:9696/1/download?file=dummy-flac", seeders=0),
    ], torrents)
    qb = FakeQBittorrent(tmp_path / "downloads", torrents)

    def fake_http(base_url, headers):
        handler = qb if "qbittorrent" in base_url else prowlarr
        return httpx.Client(base_url=base_url, headers=headers, transport=httpx.MockTransport(handler))

    monkeypatch.setattr(clients, "http", fake_http)
    settings = {**SETTINGS, "downloadsFolder": str(tmp_path / "downloads")}
    return TestClient(main.app), prowlarr, qb, settings


@pytest.mark.parametrize("answer, error", [
    (httpx.Response(200, text="Ok."), None),
    (httpx.Response(204), None),
    (httpx.Response(200, text="Fails."), "refused the username and password"),
    (httpx.Response(401), "refused the username and password"),
    (httpx.Response(403), "too many failed sign-ins"),
])
def test_signing_in_to_qbittorrent_understands_old_and_new_answers(monkeypatch, answer, error):
    # qBittorrent 5.2 answers a sign-in with 204 and nothing else, where earlier versions said "Ok.".
    def handler(request):
        return answer if request.url.path.endswith("/auth/login") else httpx.Response(200, text="v5.2.0")
    monkeypatch.setattr(clients, "http", lambda base_url, headers: httpx.Client(base_url=base_url, transport=httpx.MockTransport(handler)))
    settings = {"qbittorrentUrl": "http://qbittorrent:8080", "qbittorrentUsername": "admin", "qbittorrentPassword": "secret"}
    if error is None:
        assert clients.QBittorrent(settings).version() == "v5.2.0"
    else:
        with pytest.raises(PermissionError, match=error):
            clients.QBittorrent(settings)


def wait(client, job_id):
    for _ in range(300):
        status = client.get(f"/v1/fetch/{job_id}").json()
        if status["state"] not in ("queued", "running"):
            return status
        time.sleep(0.01)
    raise TimeoutError(status)


def test_manifest_declares_the_contract():
    manifest = TestClient(main.app).get("/v1/manifest").json()
    assert manifest["type"] == "retrieval"
    assert manifest["contractVersion"] == "1.0"
    secrets = [k for k, v in manifest["settingsSchema"]["properties"].items() if v.get("writeOnly")]
    assert secrets == ["prowlarrApiKey", "qbittorrentPassword"]


def test_settings_without_prowlarr_are_refused():
    response = TestClient(main.app).post("/v1/search", json={"settings": {}, "target": ALBUM})
    assert response.status_code == 400
    assert response.json()["code"] == "invalid-settings"


def test_an_album_is_offered_only_from_torrents_with_every_track_flac_first(fakes):
    client, prowlarr, _, settings = fakes
    candidates = client.post("/v1/search", json={"settings": settings, "target": ALBUM}).json()["candidates"]
    titles = [c["title"] for c in candidates]
    # The discography holds two of the three tracks; usenet and unseeded releases aren't torrents to fetch.
    assert titles == ["Portishead - Dummy (1994) [FLAC]", "Portishead - Dummy (1994) [MP3 320]"]
    chosen = json.loads(candidates[0]["candidateId"])
    assert chosen["h"] == DUMMY_HASH
    assert [f[:3] for f in chosen["f"]] == [[1, 1, 0], [1, 2, 1], [1, 3, 2]]
    assert "apikey" not in chosen["t"]
    assert candidates[0]["format"] == "flac"
    assert prowlarr.queries == ["Portishead Dummy"]


def test_the_format_setting_keeps_to_one_format(fakes):
    client, _, _, settings = fakes
    candidates = client.post("/v1/search", json={"settings": {**settings, "format": "mp3"}, "target": ALBUM}).json()["candidates"]
    assert [c["format"] for c in candidates] == ["mp3"]


def test_a_song_is_found_inside_an_album_torrent(fakes):
    client, prowlarr, _, settings = fakes
    candidates = client.post("/v1/search", json={"settings": settings, "target": TRACK}).json()["candidates"]
    best = candidates[0]
    assert best["title"] == "Sour Times"
    assert json.loads(best["candidateId"])["f"] == [[None, None, 1, "Portishead - Dummy (1994) [FLAC]/02 - Portishead - Sour Times.flac"]]
    assert prowlarr.queries == ["Portishead Dummy", "Portishead Sour Times"]


def test_a_concert_is_searched_for_by_its_date(fakes):
    client, prowlarr, _, settings = fakes
    target = {**ALBUM, "title": "1977‐05‐08: Barton Hall, Cornell University", "artist": "Grateful Dead"}
    client.post("/v1/search", json={"settings": settings, "target": target})
    assert prowlarr.queries == ["Grateful Dead 1977-05-08"]


def test_a_magnet_only_release_is_offered_lower_on_its_name(fakes):
    client, prowlarr, _, settings = fakes
    magnet = "magnet:?xt=urn:btih:" + "ab" * 20 + "&dn=Portishead+-+Dummy"
    prowlarr.results = [release("Portishead - Dummy [FLAC]", "http://prowlarr:9696/1/download?file=magnet", seeders=8)]
    prowlarr.torrents["magnet"] = magnet
    candidates = client.post("/v1/search", json={"settings": settings, "target": ALBUM}).json()["candidates"]
    chosen = json.loads(candidates[0]["candidateId"])
    assert chosen == {"h": "ab" * 20, "m": magnet, "f": None}
    assert 0.6 <= candidates[0]["score"] < 0.9


def test_an_album_is_downloaded_with_just_its_tracks_and_left_seeding(fakes):
    client, prowlarr, qb, settings = fakes
    candidate = client.post("/v1/search", json={"settings": settings, "target": ALBUM}).json()["candidates"][0]
    started = client.post("/v1/fetch", json={"settings": settings, "jobId": "job-1", "target": ALBUM, "candidate": candidate,
                                             "stagingDirectory": "job-1"})
    assert started.status_code == 202
    status = wait(client, "job-1")

    assert status["state"] == "completed", status
    assert [(f["path"], f["position"]) for f in status["files"]] == [
        ("01 - Portishead - Mysterons.flac", 1), ("02 - Portishead - Sour Times.flac", 2), ("03 - Portishead - Strangers.flac", 3)]
    assert qb.priorities[DUMMY_HASH] == {3: 0}  # the cover isn't downloaded
    assert DUMMY_HASH in qb.added and qb.deleted == []
    assert b"melodex-job-1" in qb.added[DUMMY_HASH]["body"]
    assert "test-key" in prowlarr.keys[-1]  # the key is put back to fetch the .torrent


def test_a_song_downloads_only_its_file_and_can_be_removed_afterwards(fakes):
    client, _, qb, settings = fakes
    settings = {**settings, "afterwards": "remove"}
    candidate = client.post("/v1/search", json={"settings": settings, "target": TRACK}).json()["candidates"][0]
    client.post("/v1/fetch", json={"settings": settings, "jobId": "job-2", "target": TRACK, "candidate": candidate, "stagingDirectory": "job-2"})
    status = wait(client, "job-2")

    assert status["state"] == "completed", status
    assert [f["path"] for f in status["files"]] == ["02 - Portishead - Sour Times.flac"]
    assert qb.priorities[DUMMY_HASH] == {0: 0, 2: 0, 3: 0}
    assert qb.deleted == [(DUMMY_HASH, "true")]


def test_a_torrent_that_was_already_there_is_kept_when_the_fetch_fails(fakes, tmp_path):
    client, _, qb, settings = fakes
    info_hash, files = torrent.read(DUMMY_FLAC)
    qb.added[info_hash] = {"files": files, "started": True, "body": b""}
    settings = {**settings, "downloadsFolder": str(tmp_path / "elsewhere")}  # where the files aren't
    candidate = client.post("/v1/search", json={"settings": settings, "target": TRACK}).json()["candidates"][0]
    client.post("/v1/fetch", json={"settings": settings, "jobId": "job-3", "target": TRACK, "candidate": candidate, "stagingDirectory": "job-3"})
    status = wait(client, "job-3")

    assert status["state"] == "failed"
    assert "downloads folder" in status["error"]
    assert qb.deleted == []


def test_health_reports_both_services(fakes):
    client, _, _, settings = fakes
    health = client.post("/v1/health", json={"settings": {**settings, "savePath": "/downloads/melodex"}}).json()
    assert health["status"] == "healthy"
    assert health["message"] == "Prowlarr 2.6.5 with 1 indexer, qBittorrent v5.1.4"


@pytest.mark.parametrize("path, artist, expected", [
    ("Dummy/01 - Portishead - Mysterons.flac", "Portishead", (None, 1, "Mysterons")),
    ("Dummy/CD2/03 - Title.flac", "", (2, 3, "Title")),
    ("Dummy/1-07 Song.flac", "", (1, 7, "Song")),
    ("gd77/gd77-05-08d2t03.flac", "Grateful Dead", (2, 3, "")),
    ("wh285/wh285_05EugeneKha_-_Norder_Remix2014.mp3", "EugeneKha", (None, 5, "Norder Remix 2014")),
    ("Dummy/Glory Box.flac", "", (None, None, "Glory Box")),
])
def test_file_names_give_disc_number_and_title(path, artist, expected):
    assert retrieve.describe(path, artist) == expected


def test_magnet_links_give_their_hash():
    assert torrent.magnet_hash("magnet:?xt=urn:btih:" + "AB" * 20) == "ab" * 20
    assert torrent.magnet_hash("magnet:?xt=urn:btih:VPVPVPVPVPVPVPVPVPVPVPVPVPVPVPVP") == "abeaf" * 8  # the same hash in base32


def test_fetched_files_are_copied_as_reflinks_unless_turned_off(fakes, monkeypatch):
    client, prowlarr, qb, settings = fakes
    from app import reflinks
    cloned = []
    real = reflinks.clone
    monkeypatch.setattr(reflinks, "clone", lambda source, target: cloned.append(source.name) or real(source, target))
    candidate = client.post("/v1/search", json={"settings": settings, "target": ALBUM}).json()["candidates"][0]
    for job, on in [("job-r1", True), ("job-r2", False)]:
        client.post("/v1/fetch", json={"settings": {**settings, "reflinks": on}, "jobId": job, "target": ALBUM,
                                       "candidate": candidate, "stagingDirectory": job})
        assert wait(client, job)["state"] == "completed"
        assert len(cloned) == 3  # the second fetch copied as usual


def test_the_reflink_check_tries_a_downloaded_file_and_says_why_not(fakes, monkeypatch, tmp_path):
    client, prowlarr, qb, settings = fakes
    from app import reflinks
    check = lambda: client.post("/v1/checks/reflinks", json={"settings": settings}).json()
    (tmp_path / "downloads").mkdir(exist_ok=True)
    assert check() == {"passed": False, "message": "There's nothing in qBittorrent's downloads folder to try it with "
                                                   "yet; check again once something has downloaded."}
    (tmp_path / "downloads" / "melodex").mkdir()
    (tmp_path / "downloads" / "melodex" / "01.flac").write_bytes(b"fLaC" * 100)

    def different_disks(fd, request, source):
        raise OSError(errno.EXDEV, "Invalid cross-device link")

    monkeypatch.setattr(reflinks.fcntl, "ioctl", different_disks)
    assert check()["message"] == ("From qBittorrent's downloads to staging, files can't share their disk space: "
                                  "they're on different filesystems.")
    monkeypatch.setattr(reflinks.fcntl, "ioctl", lambda fd, request, source: None)
    assert check()["passed"] is True
    assert not list((tmp_path / "staging").iterdir())
    assert client.post("/v1/checks/nothing", json={"settings": settings}).status_code == 404


def test_the_manifest_offers_the_reflink_check():
    manifest = TestClient(main.app).get("/v1/manifest").json()
    assert manifest["checks"][0]["id"] == "reflinks"
    assert manifest["settingsSchema"]["properties"]["reflinks"]["default"] is True


def test_a_release_name_says_its_quality_until_the_files_are_known():
    named = retrieve.quality_of_name
    assert named("Portishead - Dummy (1994) [FLAC 24bit-96kHz]") == {"format": "flac", "bit_depth": 24, "sample_rate_hz": 96000}
    assert named("Portishead - Dummy 1994 FLAC 24-96 WEB") == {"format": "flac", "bit_depth": 24, "sample_rate_hz": 96000}
    assert named("Portishead - Dummy [Hi-Res]") == {"format": "flac", "bit_depth": 24}
    assert named("Portishead - Dummy (1994) [FLAC] {CD}") == {"format": "flac"}
    assert named("Portishead - Dummy 1994 MP3 320kbps") == {"format": "mp3", "bitrate_kbps": 320}
    assert named("Portishead - Dummy [V0]") == {"format": "mp3", "bitrate_kbps": 245}
    assert named("Portishead - Dummy") == {}
