# Artwork plugins (contract v1.0)

Artwork plugins find pictures: artist photos, album covers, and the covers of particular editions. They're part of
the catalogue alongside index plugins, and sit under Catalogue in Settings → Plugins, but are a type of their own,
because a source of pictures usually can't answer the questions an index plugin must (fanart.tv can't search for
albums). Code: `Melodex.Contracts.Plugins.Artwork`.

## How the core uses them

The web app never loads artwork from a plugin or its upstream directly. Every picture is served by the core at
`GET /api/v1/artwork/{artist|album|edition}/{id}`:

1. The first time a picture is asked for, the core asks the enabled artwork plugins that declare the subject, in
   the user's order. Each returns candidates, best first; the core downloads them in turn and keeps the first that
   exists.
2. The image is stored under `data/artwork` and served from there from then on, so the browser never contacts the
   upstream, and pictures keep working offline.
3. When every plugin says it has nothing, the core remembers that for a fortnight and answers `404`, and the web
   app shows a placeholder. When a plugin fails (it throws), nothing is remembered, and the next request tries again.
4. Adding, removing, reordering, reconfiguring or switching an artwork plugin clears the cache, so the new order
   takes effect everywhere.

An album in the library is shown with the cover of the edition you have; one you don't have, with the album's.

## `IArtworkPlugin`

| Method | Purpose | Notes |
| --- | --- | --- |
| `FindAsync(query)` | Candidates for a subject, best first | Empty when there's none. Throw on failures, so the core retries later. |
| `DownloadAsync(candidate)` | The image's bytes and media type | `null` if the candidate turns out not to exist; the core tries the next. |
| `CheckHealthAsync()` | A `HealthReport` | Backs the Test button. |

`ArtworkQuery` carries the subject (`artist`, `album` or `edition`), its title or name, the artist credit, and the
IDs the core has for it, its album and its artist, as `ProviderId`s. For MusicBrainz those are the artist, release
group and release IDs. For an artist it also carries `albumTitles`, the titles of albums of theirs the core knows, so
a plugin that finds artists by name can tell two with the same name apart. A candidate's `key` is the plugin's own
reference, such as a URL; the core only hands it back.

## Capabilities

A plugin declares the subjects it can find pictures of, and is only asked about those:

| Capability | Subject |
| --- | --- |
| `artists` | Artist pictures |
| `albums` | An album's usual cover |
| `editions` | The cover of one edition |

## Borrowed settings

A plugin that works through a service another plugin already connects to can say so with `usesSettingsOf` in its
manifest, naming that plugin's ID. It then has no settings of its own: the core gives it the settings of the first
instance of that plugin, and only offers it once that plugin is set up.

## Built-in plugins

| Plugin | Subjects | Notes |
| --- | --- | --- |
| Cover Art Archive | albums, editions | Set up by default. Covers MusicBrainz users have uploaded, by release and release group. |
| Navidrome cover art | albums, editions | Not set up by default. The artwork in your own files, through Navidrome; borrows the Navidrome connection. Albums you have only. |
| Wikimedia Commons | artists | Set up by default; no account. Freely licensed photos, found through Wikidata by MusicBrainz artist ID, so never someone else of the same name. Mostly well-known artists. |
| Deezer | artists | Set up by default; no account. Deezer's artist pictures: by the artist's Deezer ID, or their Deezer ID on Wikidata found by their MusicBrainz ID, or else by exact name (ignoring case and accents) when only one Deezer artist has it or one has an album of theirs. Otherwise none, rather than someone else's. |
| fanart.tv | artists | Needs a free personal API key. Artist pictures by MusicBrainz artist ID. |

Defaults are set up once each, after the artwork plugins already there, so removing one sticks and a new default
reaches existing installs. The core also fetches the cover of every edition you have and a picture of every library
artist in the background whenever library data changes, so pictures are usually cached before a page asks for them.
