# Tidal retrieval plugin

A Melodex retrieval plugin (contract v1.0) that downloads albums and songs from [Tidal](https://tidal.com) with your
own subscription, in FLAC up to 24-bit/192 kHz.

This is how tools like tidal-dl-ng work, and it's against Tidal's terms of use, so there's a small risk of your
account being closed. Only streams Tidal sends unencrypted are downloaded: a track that comes encrypted fails with a
message saying so, and Melodex doesn't decrypt it.

It lives in the Melodex repository for now and will move to its own repository later.

## What it does

- **Sign in.** Until you've signed in, the plugin's health check gives a link to Tidal's sign-in page and the code
  to check there. Once you've signed in, test the plugin again. The sign-in is kept in the plugin's `/data` volume and
  renewed as it runs out.
- **Search.** For an album: Tidal's albums named like it. An album is offered only when it has a track for every
  requested one, matched by ISRC, or by title, position and length. The edition with the requested barcode scores
  highest. For a song: tracks named like it, and the one with the requested ISRC first.
- **Fetch.** Asks Tidal for each track's stream in the chosen quality. CD-quality FLAC comes as one file. Hi-res FLAC
  comes as segments, which are joined and put back in a FLAC file without losing anything (`ffmpeg` decodes and
  encodes it again, which gives the same samples and a header that knows the length). Files are named
  `<disc>-<position> <title>.flac`.
- **Cancel.** Stops a download between chunks.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `clientId`, `clientSecret` | | The client ID (and secret, if it has one) of a Tidal app you're allowed to use. Melodex doesn't come with one. Needed. |
| `refreshToken` | | Leave empty to sign in with a code. Or the refresh token of a Tidal app you're signed in to. |
| `quality` | `hires` | `hires` fetches the best Tidal has, up to 24-bit/192 kHz FLAC. `lossless` fetches 16-bit/44.1 kHz FLAC. `aac` fetches 320 kbps AAC. |
| `minScore` | `0.6` | Albums and songs that match less well than this aren't offered. |

## Running it

```yaml
retriever-tidal:
  build: plugins/retriever-tidal
  volumes:
    - staging:/staging
    - tidal:/data
```

Then add `http://retriever-tidal:8080` under Settings → Plugins in Melodex (it's one of the templates under Add),
fill in the client ID, and follow the sign-in link its status gives.

To sign in as someone else, remove the plugin's saved sign-in from `/data` and test it again.

`TIDAL_API` and `TIDAL_AUTH` point the plugin somewhere other than Tidal; the Melodex sandbox uses them for a stand-in.

## Tests

```bash
pip install -r requirements.txt -r requirements-dev.txt
pytest
```

The tests answer for Tidal with a fake, so they need no account. They need `ffmpeg`.
