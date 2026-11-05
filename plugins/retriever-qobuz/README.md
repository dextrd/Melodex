# Qobuz retrieval plugin

A Melodex retrieval plugin (contract v1.0) that downloads albums and songs from [Qobuz](https://www.qobuz.com) with
your own subscription, in FLAC up to 24-bit/192 kHz.

This is how tools like streamrip work, and it's against Qobuz's terms of use, so there's a small risk of your account
being closed. It needs a subscription that streams FLAC (Studio, or Sublime); with one that doesn't, Qobuz only gives
30-second samples, and the plugin says so rather than fetch them.

It lives in the Melodex repository for now and will move to its own repository later.

## What it does

- **Search.** For an album: Qobuz's albums with the requested barcode, then those named like it. An album is offered
  only when it has a track for every requested one, matched by ISRC, or by title, position and length. The edition
  with the requested barcode scores highest; another edition with every track scores a little lower. For a song: the
  track with the requested ISRC, then tracks named like it.
- **Fetch.** Asks Qobuz for each track's file in the chosen quality and downloads it into the staging directory as
  `<disc>-<position> <title>.flac`, reporting progress as it goes.
- **Cancel.** Stops a download between chunks.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `appId`, `appSecret` | | The ID and secret of a Qobuz app you're allowed to use. Melodex doesn't come with one. Qobuz checks every request for a file is signed with the secret. Needed. |
| `email`, `password` | | Your Qobuz account. The password is sent as Qobuz expects it, as its MD5. |
| `userAuthToken` | | Instead of an email and password: the sign-in token of a Qobuz app you're signed in to. |
| `quality` | `hires` | `hires` fetches the best Qobuz has, up to 24-bit/192 kHz FLAC. `cd` fetches 16-bit/44.1 kHz FLAC. `mp3` fetches 320 kbps MP3s. |
| `minScore` | `0.6` | Albums and songs that match less well than this aren't offered. |

The plugin's health check signs in and says what your subscription streams.

## Running it

```yaml
retriever-qobuz:
  build: plugins/retriever-qobuz
  volumes:
    - staging:/staging
```

Then add `http://retriever-qobuz:8080` under Settings → Plugins in Melodex (it's one of the templates under Add), and
fill in its settings.

`QOBUZ_API` points the plugin somewhere other than Qobuz's API; the Melodex sandbox uses it for a stand-in.

## Tests

```bash
pip install -r requirements.txt -r requirements-dev.txt
pytest
```

The tests answer for Qobuz with a fake, so they need no account. They make audio with `ffmpeg`.
