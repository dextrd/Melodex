"""How closely something a source offers matches what Melodex asked for, from 0 to 1."""

from __future__ import annotations

import re
import unicodedata
from difflib import SequenceMatcher

# Versions of a song that are rarely what was asked for, unless the title asks for them.
VERSION_WORDS = ["live", "remix", "cover", "karaoke", "instrumental", "acoustic", "sped up", "slowed", "reverb",
                 "nightcore", "8d", "demo", "edit", "extended", "reprise", "tribute", "lyrics"]

_NOISE = re.compile(r"[\(\[](official|audio|video|music video|lyric video|visualizer|hd|hq|4k|remaster(ed)?( \d{4})?)"
                    r"[^\)\]]*[\)\]]", re.IGNORECASE)


def normalise(text: str | None) -> str:
    if not text:
        return ""
    text = _NOISE.sub(" ", text)
    text = unicodedata.normalize("NFKD", text)
    text = "".join(c for c in text if not unicodedata.combining(c)).casefold()
    text = text.replace("&", " and ")
    text = re.sub(r"\b(feat|ft|featuring)\b.*", " ", text)
    text = re.sub(r"[^\w\s]", " ", text)
    return " ".join(text.split())


def similarity(wanted: str | None, offered: str | None) -> float:
    a, b = normalise(wanted), normalise(offered)
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    ratio = SequenceMatcher(None, a, b).ratio()
    # "Title" against "Artist - Title" and the like: the wanted words are all there.
    if set(a.split()) <= set(b.split()):
        ratio = max(ratio, 0.9)
    return ratio


def duration_score(wanted: float | None, offered: float | None) -> float:
    """1 within two seconds, falling to 0 at half a minute off; 0.5 when either length is unknown."""
    if not wanted or not offered:
        return 0.5
    return max(0.0, 1 - max(0.0, abs(wanted - offered) - 2) / 28)


def version_penalty(wanted: str, offered: str) -> float:
    """Halves the score of a live take, remix, cover and so on when the wanted title isn't one."""
    w, o = f" {normalise(wanted)} ", f" {normalise(offered)} "
    return 0.5 if any(f" {v} " in o and f" {v} " not in w for v in VERSION_WORDS) else 1.0


def track_score(title: str, artist: str, duration: float | None, offered_title: str, offered_artists: list[str],
                offered_duration: float | None) -> float:
    artist_match = max([similarity(artist, a) for a in offered_artists] + [similarity(artist, " ".join(offered_artists))]
                       + [0.0])
    # Uploads by a channel often put the artist in the title instead.
    if artist_match < 0.6 and normalise(artist) in normalise(offered_title):
        artist_match = 0.8
    score = 0.45 * similarity(title, offered_title) + 0.25 * artist_match + 0.3 * duration_score(duration, offered_duration)
    # Someone else's song of the same name is most likely a cover; verification only checks the length, so it has to
    # rank well below the real thing.
    if artist_match < 0.5:
        score *= 0.6
    return round(score * version_penalty(title, offered_title), 3)
