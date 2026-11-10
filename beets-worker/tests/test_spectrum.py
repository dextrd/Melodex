import numpy as np
import pytest
import soundfile
from fastapi.testclient import TestClient

from app import main
from app.spectrum import analyse

RATE = 44100


def music(rate=RATE, seconds=20, seed=1):
    """Noise shaped as music's spectrum is, falling with frequency, with a beat and a few tones."""
    rng = np.random.default_rng(seed)
    n = rate * seconds
    freqs = np.fft.rfftfreq(n, 1 / rate)
    spectrum = np.fft.rfft(rng.standard_normal(n))
    spectrum[1:] /= np.sqrt(freqs[1:] / 100) * (1 + freqs[1:] / 3000)
    spectrum[0] = 0
    x = np.fft.irfft(spectrum, n)
    t = np.arange(n) / rate
    x = x / np.abs(x).max() + sum(0.15 * np.sin(2 * np.pi * hz * t) for hz in (110, 220, 440, 880))
    x *= 0.4 + 0.6 * np.exp(-((t * 2) % 1) * 8)
    return x / np.abs(x).max() * 0.7


def cut(x, hz, rate=RATE):
    """As a lossy encoder leaves it: nothing above hz, then a little dither, as decoding to 16 bits adds."""
    spectrum = np.fft.rfft(x)
    spectrum[np.fft.rfftfreq(len(x), 1 / rate) > hz] = 0
    return np.fft.irfft(spectrum, len(x)) + np.random.default_rng(2).standard_normal(len(x)) * 1e-5


def gentle(x, hz, rate=RATE):
    """As an old or dull recording: the highs tail off, a few dB an octave, into tape hiss."""
    freqs = np.fft.rfftfreq(len(x), 1 / rate)
    spectrum = np.fft.rfft(x) / np.sqrt(1 + (freqs / hz) ** 4)
    return np.fft.irfft(spectrum, len(x)) + np.random.default_rng(3).standard_normal(len(x)) * 3e-4


def write(path, x, rate=RATE, subtype="PCM_16"):
    soundfile.write(path, np.stack([x, x], axis=1), rate, subtype=subtype)
    return str(path)


@pytest.mark.parametrize("hz", [16000, 17500, 19500, 20000])
def test_highs_cut_off_as_a_lossy_encoder_does_are_found_where_they_stop(tmp_path, hz):
    found = analyse(write(tmp_path / "fake.flac", cut(music(), hz))).cutoff_hz
    assert found is not None and abs(found - hz) <= 300


def test_real_lossless_has_no_cutoff_bright_or_dull(tmp_path):
    assert analyse(write(tmp_path / "real.flac", music())).cutoff_hz is None
    assert analyse(write(tmp_path / "dull.flac", gentle(music(), 6000))).cutoff_hz is None
    assert analyse(write(tmp_path / "hires.flac", music(96000, 10), 96000, "PCM_24")).cutoff_hz is None


def test_a_hi_res_file_made_from_a_cd_stops_where_the_cd_did(tmp_path):
    x = cut(music(96000, 10), 22050, 96000)
    found = analyse(write(tmp_path / "upsampled.flac", x, 96000, "PCM_24")).cutoff_hz
    assert found is not None and abs(found - 22050) <= 300


def test_silence_and_unreadable_files_cant_be_told(tmp_path):
    assert analyse(write(tmp_path / "silent.flac", np.zeros(RATE * 10))).error == "It's too quiet to tell"
    (tmp_path / "junk.flac").write_bytes(b"not audio")
    assert analyse(str(tmp_path / "junk.flac")).error.startswith("Couldn't read it")


def test_the_api_reads_staging_and_the_library_and_nothing_outside(monkeypatch, tmp_path):
    staging, library = tmp_path / "staging", tmp_path / "music"
    (staging / "job").mkdir(parents=True)
    library.mkdir()
    monkeypatch.setattr(main, "STAGING", str(staging))
    monkeypatch.setattr(main, "LIBRARY", str(library))
    write(staging / "job" / "a.flac", cut(music(), 16000))
    write(library / "b.flac", music())
    body = {"files": [{"root": "staging", "path": "job/a.flac"}, {"root": "library", "path": "b.flac"},
                      {"root": "library", "path": "../staging/job/a.flac"}, {"root": "home", "path": "x.flac"}]}
    files = TestClient(main.app).post("/v1/spectrum", json=body).json()["files"]
    assert abs(files[0]["cutoffHz"] - 16000) <= 300
    assert files[1] == {"root": "library", "path": "b.flac"}
    assert files[2]["error"] == "It isn't inside the library."
    assert files[3]["error"] == "The root is staging or library."
