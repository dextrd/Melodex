import shutil
import subprocess

import pytest
from fastapi.testclient import TestClient
from mediafile import Image, ImageType, MediaFile

from app import main
from app.importer import Importer

pytestmark = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="needs ffmpeg")

PNG = bytes.fromhex("89504e470d0a1a0a0000000d4948445200000001000000010806000000"
                    "1f15c4890000000d49444154789c6360000000020001e221bc330000000049454e44ae426082")


def audio(path, *metadata):
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=f=440:r=44100", "-t", "2", *metadata, str(path)],
                   check=True)


@pytest.fixture
def roots(monkeypatch, tmp_path):
    staging, library = tmp_path / "staging", tmp_path / "music"
    (staging / "job").mkdir(parents=True)
    (library / "Portishead" / "Dummy").mkdir(parents=True)
    monkeypatch.setattr(main, "STAGING", str(staging))
    monkeypatch.setattr(main, "LIBRARY", str(library))
    monkeypatch.setattr(main, "importer", Importer(str(staging), str(library)))
    old = library / "Portishead" / "Dummy" / "11 Glory Box.mp3"
    audio(old)
    media = MediaFile(old)
    media.title, media.artist, media.album, media.albumartist = "Glory Box", "Portishead", "Dummy", "Portishead"
    media.track, media.tracktotal, media.disc = 11, 11, 1
    media.mb_trackid = "145f5c43-0ac2-4886-8b09-63d0e92ded5d"
    media.mb_albumid = "76df3287-6cda-33eb-8e9a-044b5e15ffdd"
    media.images = [Image(PNG, type=ImageType.front)]
    media.save()
    (old.parent / "11 Glory Box.lrc").write_text("[00:01.00]Give me a reason\n")
    # A better copy, tagged as the shop had it.
    audio(staging / "job" / "track.flac", "-metadata", "title=Glory Box (Remastered)", "-metadata",
          "MUSICBRAINZ_TRACKID=00000000-0000-0000-0000-000000000000", "-metadata", "REPLAYGAIN_TRACK_GAIN=-6.50 dB")
    return staging, library


def test_the_better_copy_takes_the_old_ones_tags_and_place_and_the_old_one_is_held(roots):
    staging, library = roots
    body = {"staged": "job/track.flac", "path": "Portishead/Dummy/11 Glory Box.mp3", "hold": "held/abc"}
    reply = TestClient(main.app).post("/v1/replace", json=body).json()

    assert reply["path"] == "Portishead/Dummy/11 Glory Box.flac"
    assert reply["held"] == "held/abc/11 Glory Box.mp3"
    assert reply["format"] == "FLAC" and reply["size"] > 0
    assert not (library / "Portishead" / "Dummy" / "11 Glory Box.mp3").exists()
    assert (staging / "held" / "abc" / "11 Glory Box.mp3").is_file()
    assert not (staging / "job" / "track.flac").exists()
    new = MediaFile(library / reply["path"])
    assert (new.title, new.album, new.track, new.tracktotal) == ("Glory Box", "Dummy", 11, 11)
    assert new.mb_trackid == "145f5c43-0ac2-4886-8b09-63d0e92ded5d"
    assert new.mb_albumid == "76df3287-6cda-33eb-8e9a-044b5e15ffdd"
    assert new.images[0].data == PNG
    # Its loudness is its own.
    assert new.rg_track_gain == -6.5
    # Its lyrics have the same name, so stay with it.
    assert (library / "Portishead" / "Dummy" / "11 Glory Box.lrc").exists()


def test_it_can_be_put_back(roots):
    staging, library = roots
    client = TestClient(main.app)
    client.post("/v1/replace", json={"staged": "job/track.flac", "path": "Portishead/Dummy/11 Glory Box.mp3", "hold": "held/abc"})
    reply = client.post("/v1/restore", json={"held": "held/abc/11 Glory Box.mp3", "path": "Portishead/Dummy/11 Glory Box.mp3",
                                             "remove": "Portishead/Dummy/11 Glory Box.flac"}).json()
    assert reply["path"] == "Portishead/Dummy/11 Glory Box.mp3"
    assert MediaFile(library / reply["path"]).mb_trackid == "145f5c43-0ac2-4886-8b09-63d0e92ded5d"
    assert not (library / "Portishead" / "Dummy" / "11 Glory Box.flac").exists()
    assert not (staging / "held" / "abc").exists()


def test_without_hold_the_old_file_is_deleted(roots):
    staging, library = roots
    reply = TestClient(main.app).post("/v1/replace", json={"staged": "job/track.flac", "path": "Portishead/Dummy/11 Glory Box.mp3"}).json()
    assert "held" not in reply
    assert not (library / "Portishead" / "Dummy" / "11 Glory Box.mp3").exists()
    assert not (staging / "held").exists()


def test_a_file_in_the_way_or_outside_the_roots_is_refused_and_nothing_changes(roots):
    staging, library = roots
    client = TestClient(main.app)
    (library / "Portishead" / "Dummy" / "11 Glory Box.flac").write_bytes(b"someone else's")
    reply = client.post("/v1/replace", json={"staged": "job/track.flac", "path": "Portishead/Dummy/11 Glory Box.mp3", "hold": "held/abc"})
    assert reply.status_code == 422 and "already a file" in reply.json()["error"]
    assert (library / "Portishead" / "Dummy" / "11 Glory Box.mp3").exists()
    assert (staging / "job" / "track.flac").exists()
    reply = client.post("/v1/replace", json={"staged": "../music/x.flac", "path": "Portishead/Dummy/11 Glory Box.mp3"})
    assert reply.status_code == 422 and "isn't inside the staging volume" in reply.json()["error"]
