# Developing Melodex

## Layout

| Path | What |
| --- | --- |
| `src/Melodex.Contracts` | Plugin contracts: index, artwork, lyrics and media-server plugins, and the retrieval HTTP wire types |
| `src/Melodex.Core` | The core service: public API at `/api/v1`, data model, library scan and matching, task queue |
| `src/Melodex.Plugins.MusicBrainz` | Built-in catalogue plugins: MusicBrainz releases, at its one request a second, and Cover Art Archive covers |
| `src/Melodex.Plugins.Navidrome` | The built-in media-server connector for Navidrome, over the Subsonic API |
| `src/Melodex.Plugins.Wikimedia` | Built-in artwork plugin: artist photos from Wikimedia Commons, by MusicBrainz ID through Wikidata |
| `src/Melodex.Plugins.Deezer` | Built-in artwork plugin: artist pictures from Deezer's public catalogue, by name |
| `src/Melodex.Plugins.FanartTv` | Built-in artwork plugin: artist pictures from fanart.tv, with your own API key |
| `src/Melodex.Plugins.Lrclib` | Built-in lyrics plugin: synced and plain lyrics from LRCLIB |
| `src/Melodex.Client` | Typed client and models for the public API |
| `src/Melodex.Web` | The web app (Blazor WebAssembly), served by the core and talking only to the public API |
| `tests/Melodex.Core.Tests` | Tests, including the gate test against the test library |
| `beets-worker` | The import engine: files staged downloads into the library with beets ([docs](import-worker.md)) |
| `plugins/retriever-ytdlp` | Retrieval plugin: YouTube Music through yt-dlp |
| `plugins/retriever-inbox` | Retrieval plugin: a folder you drop files into |
| `tests/fixtures/musicbrainz` | Recorded MusicBrainz responses the gate test replays |
| `docs/guide` | The user guide, also shown in the web app under Settings → Help |
| `docs/contracts` | The plugin contracts, written down, including the retrieval OpenAPI spec |
| `tools/testlib` | Generates a test music library from real MusicBrainz data, with an answer key |
| `sandbox` | A throwaway stack (Navidrome, Melodex, the beets worker and both plugins) over a copy of the test library |

## Building and testing

With the .NET 10 SDK:

```bash
python3 tools/testlib/generate.py   # once: the gate test needs the test library, and is skipped without it
dotnet test
```

To try things out without a real library, see [testing.md](testing.md):

```bash
sandbox/sandbox.sh up
```

To build and run the whole stack from source with Docker:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up --build
```

Schema changes need a migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Melodex.Core -o Data/Migrations
```

## Releases

Versions follow [semantic versioning](https://semver.org). The version lives in `Directory.Build.props`. To release:

1. Move the *Unreleased* section of `CHANGELOG.md` under a heading for the new version, such as
   `## [1.0.0] - 2026-11-20`, and set `<Version>` in `Directory.Build.props` to match.
2. Tag the commit `v<version>` and push the tag.

The release workflow builds the images, publishes them to the GitHub Container Registry as `<version>`,
`<major>.<minor>` and `latest`, and makes a GitHub release with the changelog's notes.
