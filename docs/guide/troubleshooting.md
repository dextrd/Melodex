# Troubleshooting

**The library is empty after a scan.** The core can't see your music. Check `MUSIC_PATH` points at the folder Navidrome
reads, and that the folder is readable by everyone or by `PUID`/`PGID`. On SELinux hosts, see
[Installing](installing.md#selinux-and-podman).

**Pages say "Updating catalogue" for a long time.** The first scan asks MusicBrainz about every album, at one request
a second. It carries on in the background; pages fill in as it goes.

**MusicBrainz is busy.** Melodex waits and tries again. To go faster, run your own MusicBrainz mirror and set its
address under **Settings → Plugins → MusicBrainz**.

**A request is stuck on Review.** No plugin found a copy that passed the checks. Open it from Activity to see why each
candidate failed, choose one by hand, or add another retrieval plugin.

**Imports fail with a permissions error.** The beets worker can't write your music folder. Run it as the folder's
owner: set `PUID` and `PGID`.

**Navidrome doesn't show new music.** Check **Settings → Plugins → Navidrome → Test**. Melodex needs a Navidrome user
that may start a scan (an admin).

**I'm locked out.** See [a lost admin password](users-and-permissions.md#a-lost-admin-password).

**I want to start again.** The admin can reset Melodex at the bottom of **Settings → General**, typing `reset` to
confirm. Every user and key, the settings, plugins, matches, playlists, requests, the cache, pictures, logs and Listen
Now files go; the music folder and anything plugins keep of their own stay. Melodex restarts (the compose file brings
it back) and asks for a new admin account, as on its first run.

**Something else.** **Settings → Log** shows what Melodex has been doing, including errors. Open an
[issue](https://github.com/dextrd/Melodex/issues) with what you saw.
