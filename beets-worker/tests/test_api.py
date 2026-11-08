import base64
import errno
import os
import shutil
import subprocess
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from mediafile import MediaFile

from app import main
from app.importer import Importer

# Dummy by Portishead, and Glory Box on it.
RELEASE = {"musicBrainzId": "76df3287-6cda-33eb-8e9a-044b5e15ffdd", "title": "Dummy", "artist": "Portishead", "trackCount": 11}
GLORY_BOX = {"path": "glory.flac", "recordingId": "145f5c43-0ac2-4886-8b09-63d0e92ded5d", "title": "Glory Box",
             "artist": "Portishead", "disc": 1, "position": 11}


@pytest.fixture
def roots(monkeypatch, tmp_path):
    staging, library = tmp_path / "staging", tmp_path / "music"
    staging.mkdir()
    library.mkdir()
    monkeypatch.setattr(main, "STAGING", str(staging))
    monkeypatch.setattr(main, "LIBRARY", str(library))
    monkeypatch.setattr(main, "importer", Importer(str(staging), str(library)))
    return staging, library


@pytest.fixture
def client(roots):
    return TestClient(main.app)


def test_health_checks_both_volumes(client, roots, monkeypatch):
    assert client.get("/v1/health").json()["status"] == "healthy"
    monkeypatch.setattr(main, "LIBRARY", "/nowhere")
    assert "isn't mounted" in client.get("/v1/health").json()["message"]


def test_paths_outside_the_volumes_are_refused(client):
    body = {"stagingDirectory": "../x", "release": RELEASE, "files": [GLORY_BOX], "pathTemplate": "$album/$title"}
    response = client.post("/v1/import", json=body)
    assert response.status_code == 422
    assert "inside the staging volume" in response.json()["error"]

    body.update(stagingDirectory="job", destinationDirectory="../../etc")
    assert "inside the library" in client.post("/v1/import", json=body).json()["error"]


def test_a_missing_staged_file_fails_the_import(client, roots):
    (roots[0] / "job").mkdir()
    body = {"stagingDirectory": "job", "release": RELEASE, "files": [GLORY_BOX], "pathTemplate": "$album/$title"}
    assert "isn't in the staging directory" in client.post("/v1/import", json=body).json()["error"]


@pytest.mark.skipif(not os.environ.get("NETWORK_TESTS") or shutil.which("ffmpeg") is None,
                    reason="asks MusicBrainz; set NETWORK_TESTS=1 (needs ffmpeg)")
def test_a_file_is_tagged_and_filed_against_the_pinned_release(client, roots):
    staging, library = roots
    (staging / "job").mkdir()
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "306",
                    str(staging / "job" / "glory.flac")], check=True)
    body = {"stagingDirectory": "job", "release": RELEASE, "files": [GLORY_BOX],
            "pathTemplate": "$albumartist/$album%aunique{}/$track $title"}
    response = client.post("/v1/import", json=body)
    assert response.status_code == 200, response.json()
    imported = response.json()["files"][0]
    assert imported["path"] == "Portishead/Dummy/11 Glory Box.flac"
    assert imported["tags"]["mb_trackid"] == GLORY_BOX["recordingId"]
    assert imported["tags"]["mb_albumid"] == RELEASE["musicBrainzId"]
    assert (library / imported["path"]).exists()
    assert not (staging / "job" / "glory.flac").exists()


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
def test_an_edition_musicbrainz_doesnt_have_is_filed_as_tagged(client, roots):
    staging, library = roots
    (staging / "job").mkdir()
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "3",
                    str(staging / "job" / "one.flac")], check=True)
    release = {"title": "Roseland NYC Live 25", "artist": "Portishead", "date": "2024-03", "trackCount": 12}
    song = {"path": "one.flac", "title": "Mysterons", "artist": "Portishead", "disc": 1, "position": 1,
            "isrc": "GBUM72400001"}
    body = {"stagingDirectory": "job", "release": release, "files": [song],
            "pathTemplate": "$albumartist/$album%aunique{}/$track $title"}
    response = client.post("/v1/import", json=body)
    assert response.status_code == 200, response.json()
    imported = response.json()["files"][0]
    assert imported["path"] == "Portishead/Roseland NYC Live 25/01 Mysterons.flac"
    tags = imported["tags"]
    assert (tags["album"], tags["track"], tags["tracktotal"], tags["isrc"]) == ("Roseland NYC Live 25", "1", "12",
                                                                                "GBUM72400001")
    assert tags["year"] == "2024" and "mb_albumid" not in tags and "mb_trackid" not in tags
    media = MediaFile(library / imported["path"])
    assert (media.title, media.albumartist, media.isrc) == ("Mysterons", "Portishead", "GBUM72400001")
    assert not (staging / "job" / "one.flac").exists()


def test_deleting_removes_the_files_and_folders_left_with_only_artwork(client, roots):
    library = roots[1]
    album = library / "Portishead" / "Dummy"
    album.mkdir(parents=True)
    for name in ("01 Mysterons.flac", "02 Sour Times.flac", "cover.jpg"):
        (album / name).write_bytes(b"x")
    (library / "Portishead" / "Third").mkdir()
    (library / "Portishead" / "Third" / "01 Silence.flac").write_bytes(b"x")

    response = client.post("/v1/delete", json={"paths": ["Portishead/Dummy/01 Mysterons.flac"]})
    assert response.json() == {"deleted": ["Portishead/Dummy/01 Mysterons.flac"], "failed": []}
    assert (album / "02 Sour Times.flac").exists()

    # The last song goes, and the folder with it; the artist's folder stays for the other album.
    client.post("/v1/delete", json={"paths": ["Portishead/Dummy/02 Sour Times.flac"]})
    assert not album.exists()
    assert (library / "Portishead" / "Third").exists()


def test_deleting_outside_the_library_is_refused(client, roots):
    (roots[0] / "keep.flac").write_bytes(b"x")
    response = client.post("/v1/delete", json={"paths": ["../staging/keep.flac", "gone.flac"]})
    assert response.json()["deleted"] == ["gone.flac"]
    assert "inside the library" in response.json()["failed"][0]["error"]
    assert (roots[0] / "keep.flac").exists()


def silent_flac(path: Path, **tags: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    metadata = [arg for name, value in tags.items() for arg in ("-metadata", f"{name}={value}")]
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "1", *metadata,
                    str(path)], check=True)


# The smallest PNG there is: one transparent pixel.
PIXEL = base64.b64encode(bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010806000000"
    "1f15c4890000000d49444154789c6300010000050001"
    "0d0a2db40000000049454e44ae426082")).decode()


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
def test_formatting_tags_a_file_embeds_the_cover_and_files_it_by_the_template(client, roots):
    library = roots[1]
    old = library / "incoming" / "glory box.flac"
    silent_flac(old, title="glory box", artist="portishead")
    (old.parent / "cover.jpg").write_bytes(b"jpeg")

    body = {"files": [{"path": "incoming/glory box.flac", "artwork": "dummy",
                       "tags": {"title": "Glory Box", "artist": "Portishead", "album": "Dummy",
                                "albumartist": "Portishead", "track": "11", "tracktotal": "11", "date": "1994-08-22",
                                "mb_trackid": GLORY_BOX["recordingId"]}}],
            "artwork": {"dummy": {"data": PIXEL}},
            "pathTemplate": "$albumartist/$album/$track $title"}
    response = client.post("/v1/format", json=body)
    assert response.status_code == 200, response.json()
    result = response.json()["files"][0]
    assert result["newPath"] == "Portishead/Dummy/11 Glory Box.flac"

    media = MediaFile(library / result["newPath"])
    assert (media.title, media.album, media.track, media.year, media.month, media.day) == ("Glory Box", "Dummy", 11, 1994, 8, 22)
    assert media.mb_trackid == GLORY_BOX["recordingId"]
    assert len(media.images) == 1
    # The old folder held only the song and its cover: the cover goes with the song, and the folder goes.
    assert (library / "Portishead" / "Dummy" / "cover.jpg").read_bytes() == b"jpeg"
    assert not (library / "incoming").exists()


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
def test_formatting_without_a_template_leaves_files_where_they_are(client, roots):
    library = roots[1]
    silent_flac(library / "a" / "song.flac", title="old", isrc="GBAAA9400001")
    response = client.post("/v1/format", json={"files": [{"path": "a/song.flac", "tags": {"title": "New", "isrc": ""},
                                                           "removeArtwork": True}]})
    assert response.json()["files"][0]["newPath"] == "a/song.flac"
    media = MediaFile(library / "a" / "song.flac")
    assert media.title == "New"
    assert not media.isrc


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
def test_formatting_writes_a_copy_that_takes_the_files_place(client, roots):
    library = roots[1]
    song = library / "a" / "song.flac"
    silent_flac(song, title="old")
    # A song playing from the file keeps reading what it opened: the new file is a different one.
    playing = song.open("rb")
    response = client.post("/v1/format", json={"files": [{"path": "a/song.flac", "tags": {"title": "New"}}]})
    assert response.json()["files"][0]["newPath"] == "a/song.flac"
    assert MediaFile(song).title == "New"
    assert os.fstat(playing.fileno()).st_ino != song.stat().st_ino
    playing.close()

    # One that can't be written is left as it was, with no copy beside it.
    broken = library / "a" / "broken.flac"
    broken.write_bytes(b"not really a flac")
    response = client.post("/v1/format", json={"files": [{"path": "a/broken.flac", "tags": {"title": "New"}}]})
    assert "error" in response.json()["files"][0]
    assert broken.read_bytes() == b"not really a flac"
    assert sorted(p.name for p in (library / "a").iterdir()) == ["broken.flac", "song.flac"]


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
def test_a_chosen_cover_is_embedded_and_replaces_the_folder_image(client, roots):
    library = roots[1]
    silent_flac(library / "Dummy" / "01 Mysterons.flac", title="Mysterons")
    silent_flac(library / "Dummy" / "02 Sour Times.flac", title="Sour Times")
    (library / "Dummy" / "Folder.jpg").write_bytes(b"old")
    body = {"files": [{"path": "Dummy/01 Mysterons.flac", "artwork": "chosen"}, {"path": "Dummy/02 Sour Times.flac", "artwork": "chosen"}],
            "artwork": {"chosen": {"data": PIXEL}}, "folderArtwork": "chosen"}
    response = client.post("/v1/format", json=body)
    assert response.status_code == 200, response.json()
    assert [f["newPath"] for f in response.json()["files"]] == ["Dummy/01 Mysterons.flac", "Dummy/02 Sour Times.flac"]
    assert MediaFile(library / "Dummy" / "02 Sour Times.flac").images[0].data == base64.b64decode(PIXEL)
    # Under the name it had, as the PNG it now is.
    assert sorted(p.name for p in (library / "Dummy").iterdir() if p.suffix != ".flac") == ["Folder.png"]

    missing = client.post("/v1/format", json={**body, "folderArtwork": "other"})
    assert missing.status_code == 400


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")
def test_lyrics_go_beside_the_song_and_into_it_and_move_and_go_with_it(client, roots):
    library = roots[1]
    silent_flac(library / "in" / "roads.flac", title="Roads")
    synced = "[00:01.00] Oh, can't anybody see\n[00:05.50] We've got a war to fight"
    body = {"files": [{"path": "in/roads.flac", "lrc": synced, "tags": {"lyrics": synced}}]}
    assert client.post("/v1/format", json=body).json()["files"][0]["newPath"] == "in/roads.flac"
    assert (library / "in" / "roads.lrc").read_text(encoding="utf-8") == synced + "\n"
    assert MediaFile(library / "in" / "roads.flac").lyrics == synced

    # Moved, the lyrics go too, under the song's new name; the old folder goes.
    moved = client.post("/v1/format", json={"files": [{"path": "in/roads.flac", "tags": {"albumartist": "Portishead", "album": "Dummy"}}],
                                             "pathTemplate": "$albumartist/$album/$title"}).json()["files"][0]
    assert moved["newPath"] == "Portishead/Dummy/Roads.flac"
    assert (library / "Portishead" / "Dummy" / "Roads.lrc").exists()
    assert not (library / "in").exists()

    # Empty, they're taken away: the .lrc goes and the tag is cleared.
    cleared = {"files": [{"path": "Portishead/Dummy/Roads.flac", "lrc": "", "tags": {"lyrics": ""}}]}
    assert client.post("/v1/format", json=cleared).json()["files"][0]["newPath"] == "Portishead/Dummy/Roads.flac"
    assert not (library / "Portishead" / "Dummy" / "Roads.lrc").exists()
    assert not MediaFile(library / "Portishead" / "Dummy" / "Roads.flac").lyrics

    # Deleted, they go with it, and so does the folder.
    client.post("/v1/delete", json={"paths": ["Portishead/Dummy/Roads.flac"]})
    assert not (library / "Portishead").exists()


def test_formatting_refuses_files_outside_the_library_and_unknown_tags(client, roots):
    library = roots[1]
    (library / "song.flac").write_bytes(b"x")
    body = {"files": [{"path": "../staging/x.flac"}, {"path": "song.flac", "tags": {"comment": "hi"}},
                      {"path": "gone.flac"}]}
    results = client.post("/v1/format", json=body).json()["files"]
    assert "inside the library" in results[0]["error"]
    assert "doesn't write the tag comment" in results[1]["error"]
    assert "isn't in the music folder" in results[2]["error"]
    assert (library / "song.flac").read_bytes() == b"x"


def test_the_reflink_check_says_whether_staging_and_the_library_can_share_files(client, roots, monkeypatch):
    from app import reflinks
    calls = []

    def ioctl(fd, request, source):
        calls.append(request)
        raise OSError(errno.EXDEV, "Invalid cross-device link")

    monkeypatch.setattr(reflinks.fcntl, "ioctl", ioctl)
    assert client.post("/v1/reflinks").json() == {"works": False, "reason": "they're on different filesystems"}
    monkeypatch.setattr(reflinks.fcntl, "ioctl", lambda fd, request, source: None)
    assert client.post("/v1/reflinks").json() == {"works": True}
    # The files it tried with are gone again.
    staging, library = roots
    assert not list(library.iterdir()) and not list((staging / ".import").iterdir())


def test_a_copy_where_reflinks_cant_be_made_is_an_ordinary_copy(tmp_path, monkeypatch):
    from app import reflinks

    def ioctl(fd, request, source):
        raise OSError(errno.EOPNOTSUPP, "Operation not supported")

    monkeypatch.setattr(reflinks.fcntl, "ioctl", ioctl)
    (tmp_path / "a.flac").write_bytes(b"fLaC" * 1000)
    assert reflinks.clone(tmp_path / "a.flac", tmp_path / "b.flac") is False
    assert (tmp_path / "b.flac").read_bytes() == b"fLaC" * 1000
    assert "can't make them" in reflinks.reason(OSError(errno.EOPNOTSUPP, "x"))
