"""Seek tables added to FLAC files that have none."""

import numpy as np
import soundfile
from mutagen.flac import FLAC, SeekTable

from app import seektable
from app.formatter import write_tags

RATE = 44100


def flac(path, seconds=35):
    noise = np.random.default_rng(1).uniform(-0.3, 0.3, RATE * seconds)
    soundfile.write(path, np.stack([noise, noise], axis=1), RATE, subtype="PCM_16")
    return path


def table(path):
    return next((b for b in FLAC(path).metadata_blocks if isinstance(b, SeekTable)), None)


def test_a_flac_without_one_gets_a_point_every_ten_seconds_each_on_its_frame(tmp_path):
    path = flac(tmp_path / "song.flac")
    assert table(path) is None

    assert seektable.ensure(path)

    points = table(path).seekpoints
    assert [round(p.first_sample / RATE / 10) for p in points] == [0, 1, 2, 3]
    data = path.read_bytes()
    start = seektable._audio_start(data)
    info = FLAC(path).info
    for point in points:
        at = start + point.byte_offset
        assert seektable._header(data, at, data[at + 1] & 1, info.min_blocksize) == (point.first_sample, point.num_samples)
    # Once only, and the audio is untouched.
    assert not seektable.ensure(path)
    assert np.array_equal(soundfile.read(path)[0], soundfile.read(flac(tmp_path / "again.flac"))[0])


def test_writing_tags_adds_one(tmp_path):
    path = flac(tmp_path / "song.flac", 12)
    write_tags(path, {"title": "Song"}, None, False)
    assert FLAC(path)["title"] == ["Song"]
    assert len(table(path).seekpoints) == 2


def test_other_files_are_left_alone(tmp_path):
    other = tmp_path / "song.mp3"
    other.write_bytes(b"not a flac")
    assert not seektable.ensure(other)
    junk = tmp_path / "junk.flac"
    junk.write_bytes(b"not audio")
    assert not seektable.ensure(junk)
