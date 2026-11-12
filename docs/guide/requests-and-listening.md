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

The **Activity** page shows every request and each attempt. A request that ended without a copy to keep waits under **Review → Couldn't fetch**:
**Look again** later or after adding another plugin, open its **Details** to pick one of the candidates found, or, when
a copy was turned away only for its quality, **Keep it anyway**, as it is.

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
  the profile allows that. A refused copy is left in staging: if nothing better is found, **Keep it anyway** under
  **Review → Couldn't fetch** files it, counted as what it seems.
- **Lossless with reason to think it's real is tried first**, of candidates otherwise alike: straight from a store
  such as Qobuz or Tidal, a torrent with its rip's log and cue, or one from a tracker you've set as known for lossless
  in the Prowlarr plugin's settings.
- **The library's lossless files are checked too**, after each scan, for any new or changed. Suspect ones show their
  quality with a ⚠ and a dashed edge, count as what they seem, below the cutoff, for upgrades, and wait under
  **Review → Seems lossy**: **Replace** looks for a better copy, as an upgrade, and **Trust it** says it's lossless,
  so it counts as that and isn't checked again (until an upgrade replaces it). **Settings → Quality** can check them
  all again. Files are only read.

It's a suspicion, not proof. A master can be cut off on purpose, an old recording may have little in the highs, and
an encode that keeps them (MP3 V0, AAC at 256) can't be told this way. FLAC, WAV and AIFF can be checked; ALAC, APE
and WavPack can't.

### Upgrades

A song below its cutoff is **upgraded** when a better copy is found. Like Sonarr, Melodex keeps looking until the
cutoff is met.

- **Only matched songs are upgraded**, never deferred, excluded or hidden files, nor a song set never to be upgraded.
  A suspect lossless file counts as what it seems, so it's below the cutoff too.
- **Look for upgrades** from **Quality** on a song's, album's or artist's page, or for the whole library under
  **Settings → Quality**. Each song is its own request on the Activity page, marked **Upgrade**; an album is upgraded a
  song at a time, so it can be of mixed quality for a while.
- **On a schedule** the whole library is looked through: on the days you pick (Mondays to start with), between two
  times (01:00 and 06:00 to start with, in your browser's time zone; a window can run past midnight). In the window a
  few songs are looked for at a time, as many as **Settings → Fetching** runs at once, more as each finishes, until
  the limit (25 to start with, or **No limit**), the last song or the end of the window; any not started by then are
  cancelled and wait for the next. Those looked for longest ago go first, and the worst of those. It's on from the
  start, and can be turned off. Upgrades go after anything you ask for, and **Look for upgrades now** ignores the
  window.
- **Only a better copy is kept.** Sources are only asked for copies that may be better than what you have, and a
  copy's quality is read and its spectrum checked once it's here: one no better is refused (Activity says **No
  better**). A song nothing better is found for is left as it is (**Unchanged**), and looked for again in a later window.
- **The better copy takes the old file's place**: the same path with its own extension (`10 Voyager.mp3` becomes
  `10 Voyager.flac`), and exactly the old file's tags, MusicBrainz IDs and embedded cover included. Its lyrics file
  stays with it. Only its loudness (ReplayGain) is its own.
- **The old file is held for 14 days** (a setting), outside the music folder in the staging volume; or it can be
  deleted straight away. While it's held the upgrade waits under **Review → Upgraded** (and on the song's page):
  **Approve** keeps it and deletes the old file now, and **Roll back** deletes the better copy and puts the old file
  back, where a later upgrade may replace it again; **Roll back, never upgrade** also sets the song never to be
  upgraded. An upgrade left alone is auto-approved once its time is up.

#### What your player keeps

An upgrade is a new file, so whether the app you listen with keeps a song's stars, ratings, play counts, playlists and
shares depends on how it tells songs apart.

- **Navidrome 0.55 or later keeps them all.** It gives each song an ID from its tags, so a file with the same tags at
  the same path less the extension is the same song. Tested on 0.55.0 and 0.64.2: an MP3 replaced by a FLAC kept its
  star, rating, plays and playlists, with or without MusicBrainz tags, and even when the path changed. A file tagged
  differently became a new song, which is why the better copy takes the old file's tags rather than its own.
- **Navidrome before 0.55 loses them**: it gives the upgraded song a new ID. Tested on 0.54.5.
- **Other players** may treat an upgraded song as new, or match it by path or tags as Navidrome does. Melodex can't
  tell which.

When the media server isn't Navidrome 0.55 or later, or none is connected, **Settings → Quality** says so above the
upgrade settings, and while scheduled upgrades are on the web app says so once each time it starts, until you tick
**Don't show this again**. Upgrades still run: whether that matters is yours to decide.

## Listen now

Press **Listen** to hear something before deciding. It's fetched to a separate store, never your music folder, plays
in the player at the bottom of the page beside the music you have, and is deleted a while after you last play it (an
hour by default; change it under **Settings → Playback**). **Add to library** files the same file without fetching it
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

The visualiser can be hidden, the browser's own full screen turned on, and playing stopped, from the buttons at the
top. Escape closes it. The volume and whether the visualiser shows are kept in this browser.

### The visualiser

The visualiser's bars are worked out from each song by Melodex, not your browser, so the song is downloaded once: they
come as a small file of their own, about 300 KB for a four-minute song. **Settings → Playback** says when they're made:

- **On demand** (the default): for the song playing while the visualiser shows, and the next in the queue ahead of it,
  in a second or so. They're kept in Melodex's data folder, up to about 250 MB, the least lately played forgotten
  first.
- **When matching**: for every song in the library after each scan, kept beside it in a `.bars` file with the song's
  name, as [lyrics](library.md#lyrics) can be. Choosing it says about how much room that takes before anything is
  made, about 1.4 KB for each second of music. They move and go with their songs, and media servers pass them by. A
  song without one yet has its bars made on demand.
- **Off**: there's no visualiser, and its button is gone from the full-screen player.

Leaving **When matching** asks whether to delete the `.bars` files; kept, they're still used rather than making bars
again. The bars aren't written into songs' tags.

In the full-screen player, the player bar and the playing song's row of a track list, a name too long to fit scrolls
round. It pauses while you point at it, and stays still if your device asks for reduced motion.

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
