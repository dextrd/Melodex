# Getting started

## Setting up

The first time you open Melodex, it walks you through setting up, full screen, before anything else. The numbered
circles along the bottom show where you are. Once the admin account is made, any step can be gone back to or skipped
with them, and everything in it can be changed later under **Settings**. Until the admin finishes, nobody else gets in.

1. **People.** First the admin account. The admin may do everything, and only they can change their own account.
   Then add anyone else who'll use Melodex, with only the permissions they need. Everyone signs in, even at home; tick
   **Remember me** to stay signed in on that browser for 30 days.
2. **Music.** Melodex checks it can read your music folder and counts what's there; if it finds nothing, check
   `MUSIC_PATH` and the folder's permissions. Then your media server, Navidrome or Plex: Melodex tells it to scan when
   music arrives. For Navidrome, which also shows the covers from your own files, give its address and a Navidrome
   **admin** username and password (only admins can start scans). For Plex, give its address and a Plex token (see
   [Plex](plugins.md#plex)).
3. **Plugins.** These start as the MusicBrainz catalogue, YouTube Music (yt-dlp), Cover Art Archive, Wikimedia
   Commons and Deezer pictures, and LRCLIB lyrics. Remove any you don't want, or add others: see [Plugins](plugins.md).
4. **Apps and webhooks.** API keys for other apps, and webhooks to tell them when things happen. Most people can skip
   this.
5. **Look.** How Melodex looks for everyone. Each person can choose their own later, under **Settings → Appearance**.
6. **The first scan.** Melodex reads every file and matches it against the catalogue.

## The first scan

Matching is quick, but filling in the catalogue (each album's editions, to tell official albums from bootlegs and to
see which songs you have on other albums) costs about one MusicBrainz request per album, at one request a second. A
big library takes a while. Pages fill in as it goes, and later scans only refresh what's stale: discographies weekly
and editions monthly.

## What the library shows

Each artist's albums are one of:

| Status | Means |
| --- | --- |
| **Owned** | You have every song |
| **Partly owned** | You have some of the songs |
| **Covered** | You have every song, but on other albums, such as a compilation |
| **Missing** | You have none of it |

Live albums, compilations, remixes and the like are hidden by default; show them from the artist page. Files Melodex
couldn't match confidently wait on the **Review** page: see [Your library](library.md).

Next: [Requests and listening](requests-and-listening.md).
