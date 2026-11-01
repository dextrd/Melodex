# Your library

## Finding your way around

**Library** and **Search**, in the top bar, remember where you were in them until you close the tab. Open an artist from
your search, go to Activity, and **Search** takes you back to that artist; click it again for your search as you left
it, and once more for a fresh one. **Library** works the same way. Going back and forward shows pages you've already
seen at once, scrolled to where you were, while Melodex checks them for anything new.

An album, artist or song opened from a list shows what that list already knew while the rest arrives: an album's tracks
in order, with the ones you have filled in and the others shimmering, or an artist's albums that you have. Buttons that
need the whole page stay greyed until it's there, and while MusicBrainz is still being asked about it.

## Scanning

**Scan**, at the top right, reads new and changed files and matches them. Its menu has more:

- **Scan for changes**: the same as the button.
- **Match unmatched files again**: files under review and deferred ones, as well as changes.
- **Match everything again**: every file from scratch, except those you matched, excluded or hid yourself. Over a big
  library this takes a while.
- **Format the whole library**: see [Formatting](#formatting).

Scans and other long jobs show on **Activity**, where they can be paused, cancelled or moved in the queue.

## Review

Files Melodex couldn't match confidently wait on **Review**. They don't count as owned until they're matched. Each file
has a button for each of these; hover over one to see what it does. The buttons at the top, with their names, do the
same for every file the filter shows. On a narrow screen the filter becomes a button that opens it, and the buttons
keep only their icons.

Each file shows its own picture, from its tags or its folder, beside the song suggested for it. Sort the list by how
sure the match is, the files' names, their paths or when they last changed; the sort is remembered in that browser.

- **Approve** the suggested match, or **Search** for the right one.
- **Exclude** it: it's never matched, and the library shows it as its own tags say. Use this for music no catalogue
  knows, such as your own recordings.
- **Defer** it: it leaves the list, and each scan tries it again, matching it only when it's confident.
- **Hide** it: it leaves the library altogether.

Every file stays in your music folder whichever you choose. The lists at the bottom of Review (Excluded, Incomplete
metadata, Deferred and Hidden) put any file back.

## Your own music

Excluded files with a title and an artist are listed as their tags say: under the artist the library already has by
that name, or one of their own, with the picture embedded in the file or beside it (such as `folder.jpg`). Excluded
files without them are listed under *Incomplete metadata* on Review.

**Settings → General → File metadata** shows matched music that way too, wherever its files have them: **Use your
files' tags** for titles, artists and albums, and **Use your files' cover art** for covers. Each is on its own.

## What counts as missing

**Settings → General → Missing view** has a choice for each kind of release: the main types (albums, EPs, singles,
broadcasts, other), the kinds a release can also be (live, compilations, remixes, demos, soundtracks, DJ mixes,
mixtapes, spoken word, interviews, audiobooks, audio dramas, field recordings), and bootlegs. Each can be:

- **Show**: listed as missing, with every song on it.
- **New songs only**: listed just for the songs that aren't on a release shown in full or one you have (the same
  recording, or a song of the same title by the same artist, leaving aside notes such as "(Live)" or "- 2011
  Remaster"), and only those count as missing. A live album shows for
  the songs never released in the studio; a compilation for its unreleased tracks. On the album page, the other songs
  are marked *Elsewhere*, and fetching the album leaves them out. A kind's songs are looked up at the next scan, so its
  releases show from then.
- **Hide**: not listed.

A release can be more than one kind, such as a live compilation, and then follows the strictest. By default albums,
EPs and singles are shown in full and nothing else is.

**Songs on more than one release** decides whether a song that's on several (an album and its singles, a standard and
a deluxe edition, a compilation) is fetched once or for each, and whether owning it on one counts for the rest.

## Formatting

**Format** rewrites files' tags and covers from the catalogue, and moves them to where your naming scheme says. It's on
each artist, album and track page, and for the whole library in the Scan menu. It shows what will change first, and
nothing is written until you confirm. Set the folder layout under **Settings → General**.

## Choosing a cover

**Cover**, on an album or track page, picks the picture for it. The choices are the usual cover and each edition's
cover for every release it's on (for a song, that includes its singles, EPs and compilations), the picture your files
already have, or one you upload (JPEG, PNG, WebP or GIF, up to 10 MB).

The cover you choose is written into the files straight away, replacing what they had. An album's folder gets it as its
cover image too: it replaces a `cover`, `folder` or `front` image already there, or goes in as `cover.jpg`. Melodex then
shows it in place of what the artwork plugins found, and keeps it when artwork is fetched afresh. A song's own cover goes
only into that song's files, so the rest of the album keeps the album's cover. Choosing needs the **Format** permission.

## Lyrics

After each scan, Melodex looks for lyrics for every song you have that has none, through the lyrics plugins (LRCLIB
out of the box). Each song's page says whether it has synced lyrics (each line with when it's sung, so the player can
follow along), plain lyrics, or none, and where they're read from: an `.lrc` file beside the song, or its lyrics tag.
**View** shows them line by line. With the **Format** permission, the page can also:

- **Edit** them, from View: change any line or its time, add and remove lines, or edit them as text, for example to
  paste lyrics in. Tick **Timed** for synced lyrics; times are minutes and seconds, such as `1:02.50`. A song without
  lyrics has **Write** instead.
- **Search** the lyrics plugins, for the song's own title and artist or words you type, and choose the lyrics yourself.
  Those the length of your file come first, and each can be previewed before you use it.
- **Find again**: look now, taking the best match, as scans do.
- **Delete** them: the `.lrc` file and the lyrics tag go.
- **Exclude** the song from lyrics matching, so scans never look for its lyrics. **Include it again** undoes it.

Whatever you save is written where the settings below say, replacing what was there.

Set it up under **Settings → General → Lyrics**: whether scans look for lyrics, and where they go.

- **Beside each song** (the default): an `.lrc` file with the song's name. Navidrome and most players read these, and
  the music files aren't changed.
- **In each song's file**: its lyrics tag.
- **Both**.

A song that already has lyrics, in an `.lrc` file or its tags, keeps them. One no source has lyrics for is asked about
again after a month. Lyrics move with their song when formatting moves it, and go when it's removed.

## Removing music

The Library page can remove an artist, album or track. Either the files are deleted (with any folder left holding
only artwork) and Navidrome is told, or they stay and are hidden, so Melodex stops counting them until they're put
back on Review.

Next: [Users and permissions](users-and-permissions.md).
