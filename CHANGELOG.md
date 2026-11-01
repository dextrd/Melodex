# Changelog

What changed in each release of Melodex. Versions follow [semantic versioning](https://semver.org).

## Unreleased

Everything on the way to 1.0.

### New

- **Melodex**: Navidex has a new name, a logo and a pink theme. Installs from before upgrade in place; see
  [upgrading](docs/guide/upgrading.md#from-navidex).
- **Themes**: pink, green, blue, grey, purple and amber, each light or dark or following your device, a black mode
  for OLED screens, and high contrast with colour-blind-safe status colours. Each user chooses their own under
  Settings → Appearance.
- **The guide**, in `docs/guide` and in the web app under Settings → Help.
- **Users**: accounts for everyone, with permissions to browse, request, listen, review, format, remove, scan, change
  settings, make API keys and manage users. Everyone signs in; Remember me keeps a browser signed in for 30 days.
- **API keys** belong to the user who made them and can do no more than they may.
- **Review**: exclude, defer or hide files, and put them back from the lists at the bottom. Sort the list by how sure
  the match is, name, path or date changed; each file shows its own picture.
- **Your own music**: excluded files show as their tags say, with their own artist and album pages and covers.
- **Formatting**: rewrite files' tags, covers and folders from the catalogue, for a track, album, artist or the whole
  library.
- **Scan menu**: match unmatched files again, or everything, and format the library.
- **Tasks** can be paused, cancelled and reordered; tracks have pages of their own.
- **About**, under Settings: the version, what it was built from, and links to GitHub.
- **Missing view**: each kind of release (live, compilations, remixes, demos, soundtracks, bootlegs and the rest) has
  its own choice of Show, Hide or **New songs only**, which lists a release just for the songs found nowhere else.
  An artist can have their own choices instead, from **Releases shown** on their page, and go back to the ones in
  Settings from there (`/api/v1/settings/artists/{id}/showing`).
- **File metadata**: your files' tags and their cover art can each be preferred on their own.
- **Library and Search remember where you were**, until you close the tab. Their links in the top bar take you back
  to the artist, album or song you were last on there; click again for the Library or your search as you left it, and
  again for a fresh search. Back and forward show pages you've seen at once, scrolled to where you were, and bring them
  up to date behind.
- **Choosing a cover**: an album's or song's cover can be picked from every release it's on, its files' own, or a picture
  you upload. It's written into the files straight away (and an album's folder gets it as `cover.jpg`), and Melodex
  shows it from then on.
- **Lyrics**: a new kind of plugin, with LRCLIB set up out of the box. Scans find lyrics for songs without them and
  write them as `.lrc` files beside each song, into its tags, or both. Each song's page says what lyrics it has and
  where from, and can look for them again, search LRCLIB and choose, edit them line by line with their times or as
  text, delete them, or exclude the song from lyrics matching. The lyrics plugin contract is 1.1, adding search.
- **The full-screen player**: the cover large over a blur of itself, a visualiser that moves with the music, and the
  lyrics lit line by line as they're sung (click one to play from it), or the queue. The player bar is laid out as
  music apps have it, with a volume control.
- **Playlists**: each user's own, of songs in the library and files of your own, in folders made by a separator in their
  names (`/` by default, set on your account page). Add to them from a song's or album's page or the player. With
  **Sync playlists** ticked in the Navidrome plugin, and each user's Navidrome login linked on their account page (kept
  encrypted), they're kept the same in Navidrome both ways, and formatting that moves files doesn't break them there.
  They're a tab of the Library page, as a list in folders or as squares, with search, sorting, and your pinned ones in a
  row at the top. Each can have a picture of its own, which goes to Navidrome and comes from it. As squares, folders
  come first and open to what's in them; each can have a picture of its own too. Songs Navidrome has are recognised
  however their names are written, and ones that weren't are looked for again.
- **Getting your library ready**: just after starting, the top bar and the Library page say the library's lists are
  being worked out, which takes a while the first time on a big library.

### Fixed

- The Library's colour key sits above the artists, with room beneath it, rather than under a "No artists match" box,
  and the Albums view has one too.
- An album's tracks arriving no longer move the page: rows on their way are as tall and as wide as those arrived.
- Songs matched on their own, rather than as part of an album, show the album their tags name (or their first) in
  playlists and the Library's Tracks view, rather than none. Formatting isn't needed for that.
- The To review chip in a scan's file list on Activity is the size of the others.
- On a phone, buttons meant to give way to the title (add to playlist, queue and ignore on an album's tracks) do.
- Browsers could keep an old copy of the web app's page, showing *Loading Navidex* and failing to change the theme.
- The ownership meters and legend use brighter colours in light themes.
- The Log page's Info, Warnings and Errors toggles have tick boxes in their own colours, not black.
- Status pills on Downloads, Activity and elsewhere have room to breathe.
- The Library page's controls wait while a view loads, so changes don't pile up behind it.
- Choosing a cover offers each album, EP or single once, not every edition of it, and no picture twice.
- In the full-screen player, where it's playing from is in the middle of the page, and the lyrics sit beside the cover
  with no box, fading at the top and bottom, as tall as the cover and controls.
- The Navidrome plugin asks plainly for an admin's username and password; the account page only mentions Navidrome
  when it can sync playlists.
- Album, song and playlist pages line the cover up with the buttons, and an album's Get button is the size of the rest.
  A song's page shows each album it's on with its status, as the artist's page does.
- *Fetch once, keep it with the smallest release* now does that: a song fetched on its own goes with a single before an
  album. It used to behave as *keep it with the main album*.

### Changed

- Settings is a gear beside your account button, top right, instead of a link in the bar.
- On a phone, the top bar is two lines: Melodex, Settings and your account on the first, the pages and Scan on the
  second. Wherever the pages' names don't fit on their line, at any width, they're shown as icons instead of wrapping.
- The scan button says just **Scan**, and its menu is greyed out with it while a task is running.
- A scan's files on the Activity page open at the file being read or matched, and follow the scan along while you
  leave the list alone.
- The media-server connector contract is 1.2, adding `IPlaylistSync` for each user's playlists, and in 1.2 their
  pictures. Connectors built for 1.0 and 1.1 still load.

- Artist and album pages open at once with what Melodex knows, and fill in as MusicBrainz answers, rather than waiting
  for it. What they fetch goes ahead of catalogue updates running in the background. `GET /artists/{id}` and
  `GET /albums/{id}` take `quick=true` to answer this way, with `fetching` set while the rest is on its way.
- Album, artist and song pages opened from a list show what that list knew straight away: an album's cover, title and
  its tracks in order, the ones you have filled in and the rest shimmering until they arrive; an artist's name and the
  albums you have; a song's title and cover. Buttons that need the whole page, such as Get missing tracks and Get
  everything missing, are greyed until it's there, and while MusicBrainz is still being asked. Changing an album's
  edition shows its tracks shimmering until they arrive.
- The Library's Tracks view is laid out as a playlist's songs are, with the album in a column of its own, and marks the
  song playing. A playlist's page keeps the Library heading and tabs.
- A playlist's songs each show their album's cover, and a long playlist shows 60 songs and more as you scroll, as the
  Library does. How many is under Settings → Appearance, for both (`listPage` in `Appearance`). A song only on the media
  server that looks to be a file waiting in Review links to it there.
- Covers show from the browser's cache at once, and are checked for changes behind, rather than asked for again first;
  a playlist's four covers appear together.
- Album and song pages, and search results, work out what you own from just their own songs, not the whole library
  and every album the artist has, so they answer several times faster on a big library.
- The library's lists of artists, albums and tracks are kept on disk, so they show at once after a restart, and carry
  an ETag, so switching between them only downloads what changed. Views already loaded show at once.
- Settings → General reads more easily: songs on more than one release are described for every kind of release, and
  numbers have − and + buttons that keep every line the same height.
- `LibrarySettings` has `showing`, `preferFileTags` and `preferFileCovers` in place of `shownTypes`,
  `hideSecondaryTypes`, `hideUnofficial` and `preferFileMetadata`; saved settings carry over.
- API keys start `mdx_`; `ndx_` keys still work.
- Webhook headers are `Melodex-Signature`, `Melodex-Event` and `Melodex-Delivery`.
- The local network is no longer let in without signing in.
- The Appearance setting for how long a search is kept has gone, with `keepSearchMinutes` in `Appearance`: it's kept for
  the session now.
