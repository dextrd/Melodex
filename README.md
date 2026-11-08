<p align="center"><img src="docs/images/logo.svg" width="96" height="96" alt=""></p>

<h1 align="center">Melodex</h1>

<p align="center">Find what's missing from your music library, fetch it, and listen now.</p>

Melodex is a self-hosted companion to [Navidrome](https://www.navidrome.org), in the spirit of Sonarr and Radarr. It
reads your music folder, matches it against MusicBrainz, and shows each artist's albums as owned, partly owned,
covered by songs on other albums, or missing. Ask for what you want and it's fetched through plugins you choose,
tagged and filed by beets, and Navidrome is told. Melodex finds, fetches and imports; Navidrome keeps streaming.

- **See what's missing**: every artist's discography against what you have, with official albums told apart from
  bootlegs, and songs you have on compilations counted.
- **Fetch it**: request a song, an album or everything an artist is missing. Each request tries your retrieval
  plugins in turn, checks what they find, and files it.
- **Listen now**: play something before deciding to keep it, in a player beside the music you have.
- **Tidy up**: review files that didn't match, keep your own recordings as they are, and rewrite tags, covers and
  folders from the catalogue.
- **Share it**: accounts for everyone in the house, each with only the permissions they need, and their own theme.
- **Build on it**: a public API with scoped keys, server-sent events and signed webhooks.

## Quick start

```bash
curl -O https://raw.githubusercontent.com/joeatethebeans/Melodex/main/docker-compose.yml
MUSIC_PATH=/path/to/your/music PUID=$(id -u) PGID=$(id -g) docker compose up -d
```

Or paste `docker-compose.yml` into a compose dashboard, such as OpenMediaVault's, Portainer or Dockge, with
`MUSIC_PATH`, `PUID` and `PGID` as its environment. Open `http://<your server>:8787`, make the admin account, and
follow the setup steps. The
[installing guide](docs/guide/installing.md) has the details.

Coming from Navidex? It upgrades in place: see [upgrading](docs/guide/upgrading.md#from-navidex).

## Documentation

- [The guide](docs/guide/README.md): installing, getting started, requests, your library, users, plugins and more.
  It's in the web app too, under **Settings → Help**.
- [The client guide](docs/client-guide.md), for apps that use the API. The interactive reference is at `/api/docs` on
  your server.
- [Plugin contracts](docs/contracts/README.md), for writing plugins.
- [Developing Melodex](docs/development.md).
- [Changelog](CHANGELOG.md).

## Plugins and sources

Melodex ships no media sources. Retrieval plugins are separate services that speak an HTTP contract; the two in
`plugins/` (YouTube Music through yt-dlp, and an inbox folder) live here for now. You're responsible for the plugins
you run and for complying with each source's terms.

## Licence

[MIT](LICENSE)
