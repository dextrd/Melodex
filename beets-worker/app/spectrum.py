"""Where a lossless file's highs stop, to catch lossy music passed off as lossless (#19).

Lossy encoders throw away the highest frequencies first, and cut them off sharply: MP3 at 128 kbps at about 16–17 kHz,
at 320 at about 20 kHz. Decoded and saved as FLAC, the music still stops there, with nothing but silence above, where
real CD audio goes on to the top of the range, or tails off gradually in an old or dull recording. A hi-res file made
from a CD stops the same way at about 22 kHz. So this finds a cliff: a fall of tens of dB within a few hundred Hz, with
nothing coming back above it. What the cliff means is the core's to say; it only reports where it is.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
import soundfile

# How many stretches of the music to look at, spread through it, and how far into it to start and stop: intros and
# fade-outs say little.
_WINDOWS = 32
_FROM, _TO = 0.08, 0.92
# Bands the spectrum is summed into, and how far below and above a band edge to compare.
_BAND_HZ = 100
_BELOW_HZ = 1000
_GAP_BANDS = 3
# A cliff falls at least this far within _GAP_BANDS, and the music below stands at least this far above anything over it.
_STEEP_DB = 20.0
_DROP_DB = 25.0
# Below this, the highs are too little of anything to tell: a lowpass this low is the recording, not an encoder.
_LOWEST_HZ = 10000


@dataclass(frozen=True)
class Spectrum:
    """What was found of a file: where its highs are cut off sharply, if they are; or why it couldn't be told."""

    cutoff_hz: int | None
    error: str | None = None


def analyse(path: str) -> Spectrum:
    try:
        info = soundfile.info(path)
    except Exception as e:  # noqa: BLE001 - libsndfile's errors are many and all mean "can't read it"
        return Spectrum(None, f"Couldn't read it: {e}")
    rate, frames = info.samplerate, info.frames
    # About 5 Hz a bin, whatever the sample rate.
    size = 2 ** round(math.log2(rate / 5))
    if frames < size * 4:
        return Spectrum(None, "It's too short to tell")

    window = np.hanning(size).astype(np.float32)
    total = np.zeros(size // 2 + 1)
    heard = 0
    starts = np.linspace(frames * _FROM, frames * _TO - size, _WINDOWS).astype(int)
    with soundfile.SoundFile(path) as f:
        for start in starts:
            f.seek(int(start))
            block = f.read(size, dtype="float32", always_2d=True)
            if len(block) < size:
                continue
            mono = block.mean(axis=1)
            # Near-silent stretches would only weigh the floor.
            if float(np.sqrt(np.mean(mono * mono))) < 1e-3:
                continue
            total += np.abs(np.fft.rfft(mono * window)) ** 2
            heard += 1
    if heard < 4:
        return Spectrum(None, "It's too quiet to tell")
    return Spectrum(cutoff(total / heard, rate))


def cutoff(power: np.ndarray, rate: int) -> int | None:
    """The frequency where a power spectrum (bins from 0 Hz to half the rate) falls off a cliff, or None."""
    hz_per_bin = rate / 2 / (len(power) - 1)
    per_band = max(1, round(_BAND_HZ / hz_per_bin))
    bands = len(power) // per_band
    db = 10 * np.log10(power[: bands * per_band].reshape(bands, per_band).mean(axis=1) + 1e-30)
    band_hz = per_band * hz_per_bin
    below = round(_BELOW_HZ / band_hz)

    best, steepest = None, 0.0
    # The top band or two can hold the resampler's own roll-off, so the search stops short of them.
    for i in range(max(below + 1, math.ceil(_LOWEST_HZ / band_hz)), bands - _GAP_BANDS - 2):
        under = float(db[i - below : i - 1].mean())
        over = float(db[i + _GAP_BANDS :].max())
        steep = float(db[i - 1] - db[i + _GAP_BANDS - 1])
        if under - over >= _DROP_DB and steep >= _STEEP_DB and steep > steepest:
            best, steepest = i, steep
    return None if best is None else int(round(best * band_hz))
