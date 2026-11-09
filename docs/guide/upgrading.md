# Upgrading

## To a new version

The compose file runs the release named by `MELODEX_VERSION` (`latest` by default). To upgrade:

```bash
docker compose pull
docker compose up -d
```

The database is migrated when the core starts. Back up the `config` volume first if you want a way back: an older
version can't open a database a newer one has migrated. What changed in each release is in the
[changelog](https://github.com/dextrd/Melodex/blob/main/CHANGELOG.md).

## From 1.0.2 or earlier: the images' new home

Melodex moved to the [dextrd](https://github.com/dextrd) organisation after 1.0.2. Releases since are published as
`ghcr.io/dextrd/melodex…`, where they were `ghcr.io/joeatethebeans/melodex…`. The old images stay where they are, but
get no new releases, so an install still naming them stays on 1.0.2.

To move over, change `joeatethebeans` to `dextrd` in each `image:` line of your compose file (in a dashboard such as
OpenMediaVault's, edit the stack's compose there), or take the new
[`docker-compose.yml`](https://github.com/dextrd/Melodex/blob/main/docker-compose.yml) and keep your `.env`. Then pull
and start it as above. Nothing else changes: your settings, library and data carry on.

## From a source build

If you ran Melodex from a checkout of the repository with `docker-compose.build.yml`, switch to the released images:

- **To keep your data**, stay in the checkout's folder: `git pull`, then `docker compose pull` and
  `docker compose up -d`, leaving out `-f docker-compose.build.yml` and `--build` from now on. Set `MELODEX_VERSION`
  to the release, as the images you built locally were also called `latest`.
- **To start afresh**, stop the old stack and remove it with its volumes (`docker compose down -v` in its folder),
  then set Melodex up anew, from a folder or a compose dashboard, as in [installing](installing.md). Setup runs again,
  and the first scan matches your music again. Your music folder isn't touched either way.

## From Navidex

Melodex was called Navidex. An install from then upgrades in place, with nothing to do by hand:

- The database file, `navidex.db`, is renamed `melodex.db` on first start.
- Plugins carry on: their IDs, once `navidex.*`, are rewritten.
- API keys starting `ndx_` keep working. New keys start `mdx_`.
- `Navidex__…` environment variables are still read.
- Everyone signs in once more, as the session cookie has a new name.
- Webhook deliveries now carry `Melodex-Signature`, `Melodex-Event` and `Melodex-Delivery` headers; update any
  receiver that checks the old `Navidex-` names.

To move your compose setup over:

1. Stop the old stack: `docker compose down` (this keeps its volumes).
2. Replace `docker-compose.yml` with the new one, **in the same folder**. Compose names volumes after the folder, so
   keeping it keeps your `config` volume and its database. If you must use another folder, set
   `COMPOSE_PROJECT_NAME` to the old folder's name.
3. Start it: `docker compose up -d`.

The core's log says *Moved the database from Navidex* on that first start.
