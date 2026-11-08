# Requests and listening

## Asking for music

Press the download button beside a missing track or album, **Get album** or **Get missing tracks** on an album, or
**Get everything missing** on an artist, to request it. Each request goes through
your retrieval plugins in turn, highest priority first:

1. The plugin searches its source and offers candidates.
2. Melodex checks each one: the right length, a real audio file, the quality you asked for under **Settings →
   General → Fetching**.
3. The beets worker tags it from the MusicBrainz release Melodex chose and files it in your music folder.
4. Navidrome is told to scan.

The **Activity** page shows every request and each attempt. A request no plugin could satisfy waits on **Review**:
retry it later, add another plugin, or pick one of the candidates found.

Requests run one at a time, in order. Move them up and down the queue, pause them or cancel them from Activity.

**Ignore** an album or song to stop counting it as missing.

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
