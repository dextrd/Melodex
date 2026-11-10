# Requests and listening

## Asking for music

Press the download button beside a missing track or album, **Get album** or **Get missing tracks** on an album, or
**Get everything missing** on an artist, to request it. Each request searches all your retrieval plugins at once:

1. Each plugin searches its source and offers candidates, saying what quality each is.
2. Melodex tries the best first, whichever plugin it's from: the right song, in the quality your
   [profile](#quality) likes best. It checks each one: the right length, a real audio file, and a quality the
   profile allows, read from the file itself.
3. The beets worker tags it from the MusicBrainz release Melodex chose and files it in your music folder.
4. Navidrome is told to scan.

The **Activity** page shows every request and each attempt. A request no plugin could satisfy waits on **Review**:
retry it later, add another plugin, or pick one of the candidates found.

Requests run one at a time, in order. Move them up and down the queue, pause them or cancel them from Activity.

**Ignore** an album or song to stop counting it as missing.

## Quality

**Settings → Quality** is the quality profile: every codec at every level, best first, such as FLAC hi-res, FLAC CD,
ALAC CD, Opus high, MP3 high, down to MP3 low. Move them up and down to say which you'd rather have, and turn off any
you never want. By default lossless comes first, then good lossy, and fair and low lossy are off.

- **Of the candidates that are the right song, the highest in the list is fetched.** A likelier match always comes
  first: a better quality never beats a better chance of the right recording.
- **A quality turned off is never kept.** A file's quality is read from it once it's fetched, so a "FLAC" that's
  really an MP3 is treated as an MP3. Activity says what it was said to be and what it is.
- **The cutoff** is the quality that's good enough (FLAC CD by default). Music below it can be upgraded.
- **Listen now has its own target**, small by default (Opus fair), so a song starts soon: the best quality at or
  under it, or the smallest above if there's none. A listened song you add is kept if the profile allows its quality,
  and fetched again if not.

An artist, album or song can have its own setting, from **Quality** on its page: its own cutoff, or never upgrade it.
The narrowest wins (a song's, then its album's, then its artist's, then the profile's), and the dialog says where what
applies comes from.

### Lossless that seems lossy

Lossy music is sometimes passed off as lossless: an MP3 saved as FLAC is labelled FLAC and is the size of one, but it
sounds no better than the MP3. Lossy encoders cut off the highest frequencies where they give themselves away, about
16.5 kHz for an MP3 at 128 kbps and 20 kHz at 320, so every lossless download is checked for that before it's kept.
One that's cut off is **suspect**, and counts as the quality it seems to be: a FLAC cut at 16.5 kHz is low-quality
lossy. A hi-res file that stops where a CD does was made from one, and counts as CD quality.

- **What happens to a suspect download** is a setting under **Settings → Quality**: refuse it and try the next
  candidate (the default; Activity says **Seems lossy**, and where its highs stop), or keep it as what it seems, if
  the profile allows that.
- **Lossless with reason to think it's real is tried first**, of candidates otherwise alike: straight from a store
  such as Qobuz or Tidal, a torrent with its rip's log and cue, or one from a tracker you've set as known for lossless
  in the Prowlarr plugin's settings.
- **The library's lossless files are checked too**, after each scan, for any new or changed. Suspect ones show their
  quality with a ⚠ and a dashed edge, count as what they seem, below the cutoff, for upgrades, and are listed under
  **Settings → Quality**, which can check them all again. Files are only read.

It's a suspicion, not proof. A master can be cut off on purpose, an old recording may have little in the highs, and
an encode that keeps them (MP3 V0, AAC at 256) can't be told this way. FLAC, WAV and AIFF can be checked; ALAC, APE
and WavPack can't.

## Listen now

Press **Listen** to hear something before deciding. It's fetched to a separate store, never your music folder, plays
in the player at the bottom of the page beside the music you have, and is deleted a while after you last play it (an
hour by default; change it under **Settings → General → Fetching**). **Add to library** files the same file without fetching it
again.

The **Downloads** page lists what you've fetched to listen to.

## The player

Play, queue and skip from any album, artist or track. While something's playing, the queue button asks where: **Play
next**, straight after the song playing, or at the end. Songs you don't have are fetched as the queue reaches them.

The queue is kept for each person in this browser: after a reload, or the next time, it's there paused where you
left it, and Play carries on. Drag songs by the six dots on their left to reorder them. The button at the top of the queue opens it as
a page of its own, to play from, move songs about, take them out, or save the queue as a playlist.

**Shuffle**, beside the controls, plays what's to come in a random order; turning it off puts the queue back in its
order, carrying on from the same song. **Repeat** goes round the queue again at the end, and pressed again, plays the
same song again. Both are kept in this browser.

The bar along the bottom has what's playing on the left, the controls and seek bar in the middle, and lyrics, the
queue, the volume, the full-screen player and stop on the right. Stop ends playing and clears the queue. In a narrow
window the volume becomes a button that opens its slider, and the playlist and lyrics buttons are left to the
full-screen player.

On a phone the bar is one line: previous, play, next and stop, with a line along its top showing how far the song has
got. Tap anywhere else on it to open the full-screen player, which has everything else.

**The full-screen player** opens from the cover in the bar, or its expand button. It shows the cover large, over a
blur of itself, with a visualiser that moves with the music, and beside it, either:

- **Lyrics**: synced lyrics light up line by line as they're sung, and stay in view. Click a line to play from it.
  Scroll them yourself and they wait a few seconds before following again. Plain lyrics are shown as they are. A song
  without lyrics can be looked up from here, if you may format; see [lyrics](library.md#lyrics).
- **The queue**.

The visualiser can be turned off, the browser's own full screen turned on, and playing stopped, from the buttons at the
top. Escape
closes it. The volume and whether the visualiser shows are kept in this browser.

The playlist button in the bar, or at the top of the full-screen player, adds what's playing to a playlist.

**Keyboard shortcuts** work anywhere outside a text box:

| Key | Does |
| --- | --- |
| Space or K | Play or pause |
| → and ← | Forward or back ten seconds |
| Shift+→ and Shift+←, or N and P | Next or previous song |
| S | Shuffle on or off |
| R | Repeat: the queue, the song, or off |
| / | Search |

Next: [Playlists](playlists.md).
