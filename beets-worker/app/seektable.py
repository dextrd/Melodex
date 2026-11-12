"""Seek tables for FLAC files that have none.

A FLAC file's seek table says where in the file each point in time is. Without one, a player seeking has to guess and
correct itself, which Safari does slowly and imprecisely: after a seek the song repeats a moment, and what it reports as
the time, which the lyrics follow, is off from what's heard. Many files come without one. One is added, with a point
every few seconds, whenever a file is written.
"""

from __future__ import annotations

from pathlib import Path

from mutagen.flac import FLAC, SeekPoint, SeekTable

# Seconds between seek points: close enough that a seek lands near where it's asked for.
EVERY = 10


def ensure(path: Path) -> bool:
    """Adds a seek table to a FLAC file without one. Whether it added one; other files, and FLAC files it can't make
    sense of, are left as they are."""
    if path.suffix.lower() != ".flac":
        return False
    try:
        flac = FLAC(path)
    except Exception:
        return False
    if any(isinstance(block, SeekTable) for block in flac.metadata_blocks):
        return False
    info = flac.info
    if not info.sample_rate or not info.total_samples:
        return False
    data = path.read_bytes()
    start = _audio_start(data)
    if start is None:
        return False
    frames = list(_frames(data, start, info.sample_rate, getattr(info, "min_blocksize", 0)))
    if not frames:
        return False
    table = SeekTable(None)
    table.seekpoints = points(frames, info.sample_rate, info.total_samples)
    flac.metadata_blocks.insert(1, table)
    flac.save()
    return True


def points(frames: list[tuple[int, int, int]], rate: int, total: int) -> list[SeekPoint]:
    """A point every EVERY seconds: the frame each time falls in, as (first sample, byte offset from the first frame,
    samples in it). Frames are (first sample, byte offset, samples), in order."""
    result = []
    i = 0
    for target in range(0, total, rate * EVERY):
        while i + 1 < len(frames) and frames[i + 1][0] <= target:
            i += 1
        sample, offset, count = frames[i]
        if not result or result[-1].first_sample != sample:
            result.append(SeekPoint(sample, offset, count))
    return result


def _audio_start(data: bytes) -> int | None:
    """Where the first frame is: after an ID3 tag, if any, the fLaC marker and the metadata blocks."""
    pos = 0
    if data[:3] == b"ID3" and len(data) >= 10:
        size = 0
        for b in data[6:10]:
            size = (size << 7) | (b & 0x7F)
        pos = 10 + size
    if data[pos:pos + 4] != b"fLaC":
        return None
    pos += 4
    while pos + 4 <= len(data):
        header = data[pos]
        length = int.from_bytes(data[pos + 1:pos + 4], "big")
        pos += 4 + length
        if header & 0x80:
            return pos
    return None


def _frames(data: bytes, start: int, rate: int, fixed: int):
    """Each frame's (first sample, byte offset from the first frame, samples), found by its sync code and checked by
    its header's CRC, so audio that happens to look like a sync code isn't taken for one."""
    pos = start
    end = len(data)
    last = -1
    while pos < end - 16:
        pos = data.find(b"\xff", pos)
        if pos < 0 or pos >= end - 16:
            return
        second = data[pos + 1]
        if second in (0xF8, 0xF9) and (frame := _header(data, pos, second & 1, fixed)) is not None:
            sample, count = frame
            if sample > last:
                last = sample
                yield sample, pos - start, count
                # A frame is never shorter than its header; the next is further on.
                pos += 4
                continue
        pos += 1


BLOCK_SIZES = {1: 192, 2: 576, 3: 1152, 4: 2304, 5: 4608, 8: 256, 9: 512, 10: 1024, 11: 2048, 12: 4096, 13: 8192,
               14: 16384, 15: 32768}


def _header(data: bytes, pos: int, variable: int, fixed: int) -> tuple[int, int] | None:
    """The first sample and sample count of the frame whose header starts here, or None if it isn't one."""
    size_code = data[pos + 2] >> 4
    rate_code = data[pos + 2] & 0x0F
    if size_code == 0 or rate_code == 15 or data[pos + 3] & 1 or (data[pos + 3] >> 4) > 10:
        return None
    # The frame or sample number, coded like UTF-8.
    i = pos + 4
    first = data[i]
    if first < 0x80:
        number, extra = first, 0
    elif first >= 0xFE or first & 0xC0 == 0x80:
        return None
    else:
        extra = 0
        mask = 0x40
        while first & mask:
            extra += 1
            mask >>= 1
        number = first & (mask - 1)
        for k in range(1, extra + 1):
            b = data[i + k]
            if b & 0xC0 != 0x80:
                return None
            number = (number << 6) | (b & 0x3F)
    i += 1 + extra
    if size_code == 6:
        count = data[i] + 1
        i += 1
    elif size_code == 7:
        count = int.from_bytes(data[i:i + 2], "big") + 1
        i += 2
    else:
        count = BLOCK_SIZES[size_code]
    if rate_code == 12:
        i += 1
    elif rate_code in (13, 14):
        i += 2
    if i >= len(data) or _crc8(data[pos:i]) != data[i]:
        return None
    if variable:
        return number, count
    if not fixed:
        return None
    return number * fixed, count


def _crc8(data: bytes) -> int:
    crc = 0
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if crc & 0x80 else (crc << 1) & 0xFF
    return crc
