#!/usr/bin/env bash
# A throwaway Navidrome + Melodex stack over a copy of the generated test library, so nothing touches a real library.
#
#   sandbox/sandbox.sh up       copy the test library, build what's missing, start everything, configure Melodex, scan
#   sandbox/sandbox.sh build    rebuild the Melodex images
#   sandbox/sandbox.sh status   show what Navidrome has indexed and how Melodex's plugins are doing
#   sandbox/sandbox.sh scan     ask Navidrome to rescan and wait for it
#   sandbox/sandbox.sh down     stop and remove the containers and their data
#   sandbox/sandbox.sh reset    down, and throw away the library copy and the inbox
#
# Works with podman or docker. Navidrome's and Melodex's credentials are generated on first run into sandbox/.env.
# Prowlarr and qBittorrent come too, for the Prowlarr plugin, with PandaCD (Creative Commons music) as the indexer.
# The Qobuz, Tidal and Bandcamp plugins talk to stand-ins for those services (sandbox/standins), which play tones.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SANDBOX="$ROOT/sandbox"
TESTLIB="$ROOT/tools/testlib/out/music"
# A writable copy: the beets worker files fetched music into it.
MUSIC="$SANDBOX/music"
INBOX="$SANDBOX/inbox"
ENV_FILE="$SANDBOX/.env"
# Melodex's session, for the calls this script makes.
COOKIES="$SANDBOX/.cookies"

NAVIDROME_IMAGE="docker.io/deluan/navidrome:0.64.2"
PROWLARR_IMAGE="lscr.io/linuxserver/prowlarr:2.6.5.5623-ls165"
QBITTORRENT_IMAGE="lscr.io/linuxserver/qbittorrent:5.1.4-r3-ls451"
NETWORK="melodex-sandbox"
NAVIDROME_PORT="${NAVIDROME_PORT:-4533}"
MELODEX_PORT="${MELODEX_PORT:-8787}"
PROWLARR_PORT="${PROWLARR_PORT:-9696}"
QBITTORRENT_PORT="${QBITTORRENT_PORT:-8090}"
STANDIN_PORT="${STANDIN_PORT:-8095}"
CONTAINERS=(melodex-sandbox-navidrome melodex-sandbox-core melodex-sandbox-beets melodex-sandbox-ytdlp melodex-sandbox-inbox melodex-sandbox-archive
  melodex-sandbox-prowlarr melodex-sandbox-qbittorrent melodex-sandbox-torrents melodex-sandbox-standin melodex-sandbox-qobuz
  melodex-sandbox-tidal melodex-sandbox-bandcamp)
VOLUMES=(melodex-sandbox-navidrome melodex-sandbox-config melodex-sandbox-staging melodex-sandbox-prowlarr
  melodex-sandbox-qbittorrent melodex-sandbox-downloads melodex-sandbox-tidal)

# Image, then build context and Dockerfile.
IMAGES=(
  "localhost/melodex/core:dev|$ROOT|$ROOT/docker/core.Dockerfile"
  "localhost/melodex/beets-worker:dev|$ROOT/beets-worker|$ROOT/beets-worker/Dockerfile"
  "localhost/melodex/retriever-ytdlp:dev|$ROOT/plugins/retriever-ytdlp|$ROOT/plugins/retriever-ytdlp/Dockerfile"
  "localhost/melodex/retriever-inbox:dev|$ROOT/plugins/retriever-inbox|$ROOT/plugins/retriever-inbox/Dockerfile"
  "localhost/melodex/retriever-archive:dev|$ROOT/plugins/retriever-archive|$ROOT/plugins/retriever-archive/Dockerfile"
  "localhost/melodex/retriever-prowlarr:dev|$ROOT/plugins/retriever-prowlarr|$ROOT/plugins/retriever-prowlarr/Dockerfile"
  "localhost/melodex/retriever-qobuz:dev|$ROOT/plugins/retriever-qobuz|$ROOT/plugins/retriever-qobuz/Dockerfile"
  "localhost/melodex/retriever-tidal:dev|$ROOT/plugins/retriever-tidal|$ROOT/plugins/retriever-tidal/Dockerfile"
  "localhost/melodex/retriever-bandcamp:dev|$ROOT/plugins/retriever-bandcamp|$ROOT/plugins/retriever-bandcamp/Dockerfile"
)

ENGINE="$(command -v podman || command -v docker || true)"
[[ -n "$ENGINE" ]] || { echo "needs podman or docker" >&2; exit 1; }

# The beets worker and the plugins run as you, so the library copy and the inbox stay yours.
if [[ "$(basename "$ENGINE")" == podman ]]; then
  AS_YOU=(--userns=keep-id)
else
  AS_YOU=(--user "$(id -u):$(id -g)")
fi

# Files a container writes carry its SELinux categories. Downloads pass from a plugin through beets to the core and
# Navidrome, so on SELinux hosts every container shares one level (elsewhere the option is ignored).
SHARED=(--network "$NETWORK" --security-opt label=level:s0:c100,c200)

if [[ ! -f "$ENV_FILE" ]]; then
  printf 'NAVIDROME_USER=admin\nNAVIDROME_PASSWORD=%s\n' "$(head -c 18 /dev/urandom | base64 | tr -d '/+=')" > "$ENV_FILE"
fi
if ! grep -q '^MELODEX_PASSWORD=' "$ENV_FILE"; then
  printf 'MELODEX_USER=admin\nMELODEX_PASSWORD=%s\n' "$(head -c 18 /dev/urandom | base64 | tr -d '/+=')" >> "$ENV_FILE"
fi
# shellcheck source=/dev/null
source "$ENV_FILE"

navidrome_url="http://localhost:$NAVIDROME_PORT"
melodex_url="http://localhost:$MELODEX_PORT/api/v1"

# Calls the Subsonic API with token auth and prints the JSON response.
subsonic() {
  local method="$1"; shift
  local salt token
  salt="$(head -c 8 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  token="$(printf '%s%s' "$NAVIDROME_PASSWORD" "$salt" | md5sum | cut -d' ' -f1)"
  curl -sf -G "$navidrome_url/rest/$method" \
    --data-urlencode "u=$NAVIDROME_USER" --data-urlencode "t=$token" --data-urlencode "s=$salt" \
    -d v=1.16.1 -d c=melodex-sandbox -d f=json "$@"
}

melodex() {
  local method="$1" path="$2"; shift 2
  curl -sf -X "$method" "$melodex_url$path" -H 'Content-Type: application/json' -b "$COOKIES" -c "$COOKIES" "$@"
}

# Signs in to Melodex, making the admin login first if it's a new stack.
sign_in() {
  local login="{\"username\":\"$MELODEX_USER\",\"password\":\"$MELODEX_PASSWORD\"}"
  melodex POST /auth/setup -o /dev/null -d "$login" 2>/dev/null || melodex POST /auth/login -o /dev/null -d "$login"
}

wait_for() {
  local what="$1" url="$2"
  for _ in $(seq 1 60); do
    curl -sf -o /dev/null "$url" && return 0
    sleep 1
  done
  echo "$what didn't come up at $url" >&2
  exit 1
}

wait_for_scan() {
  for _ in $(seq 1 120); do
    if subsonic getScanStatus | grep -q '"scanning":false'; then return 0; fi
    sleep 1
  done
  echo "scan didn't finish" >&2
  exit 1
}

build() {
  local only_missing="${1:-}" entry image context file
  for entry in "${IMAGES[@]}"; do
    IFS='|' read -r image context file <<<"$entry"
    if [[ -n "$only_missing" ]] && "$ENGINE" image inspect "$image" >/dev/null 2>&1; then continue; fi
    echo "Building $image..."
    "$ENGINE" build -q -f "$file" -t "$image" "$context" >/dev/null
  done
}

# Adds the plugins to Melodex, once: Navidrome as the media server, then the two retrieval plugins.
configure() {
  local plugins
  plugins="$(melodex GET /plugins)"
  if ! grep -q '"melodex.connector.navidrome"' <<<"$plugins"; then
    melodex POST /plugins -o /dev/null -d "{\"pluginId\":\"melodex.connector.navidrome\",\"settings\":{\"url\":\"http://navidrome:4533\",\"username\":\"$NAVIDROME_USER\",\"password\":\"$NAVIDROME_PASSWORD\"}}"
  fi
  for endpoint in http://retriever-inbox:8080 http://retriever-ytdlp:8080 http://retriever-archive:8080; do
    grep -q "\"$endpoint\"" <<<"$plugins" && continue
    # The plugin may still be starting.
    for _ in $(seq 1 30); do
      melodex POST /plugins -o /dev/null -d "{\"endpoint\":\"$endpoint\"}" && continue 2
      sleep 1
    done
    echo "couldn't add the plugin at $endpoint" >&2
    exit 1
  done
  configure_torrents "$plugins"
  configure_streaming "$plugins"
  # Test each, so Settings shows how they're doing.
  for id in $(melodex GET /plugins | grep -o '"id":"[^"]*"' | cut -d'"' -f4); do
    melodex POST "/plugins/$id/test" -o /dev/null || true
  done
}

# The Qobuz, Tidal and Bandcamp plugins, with the stand-ins' app keys and cookie. Tidal still needs signing in: its
# status gives the link. Bandcamp's collection starts empty: buy things on the stand-in's page.
configure_streaming() {
  local plugins="$1" endpoint settings
  while read -r endpoint settings; do
    grep -q "\"$endpoint\"" <<<"$plugins" && continue
    # The plugin may still be starting.
    for _ in $(seq 1 30); do
      melodex POST /plugins -o /dev/null -d "{\"endpoint\":\"$endpoint\",\"settings\":$settings}" 2>/dev/null && continue 2
      sleep 1
    done
    echo "couldn't add the plugin at $endpoint" >&2
    exit 1
  done <<'PLUGINS'
http://retriever-qobuz:8080 {"appId":"standin","appSecret":"standin-secret","email":"listener@sandbox.invalid","password":"sandbox"}
http://retriever-tidal:8080 {"clientId":"standin"}
http://retriever-bandcamp:8080 {"identity":"standin"}
PLUGINS
}

# Prowlarr's API key, which it makes on first start.
prowlarr_key() {
  "$ENGINE" exec melodex-sandbox-prowlarr sed -n 's/.*<ApiKey>\([^<]*\).*/\1/p' /config/config.xml
}

# PandaCD in Prowlarr, then the Prowlarr plugin in Melodex with Prowlarr's key and the sandbox's qBittorrent.
configure_torrents() {
  local plugins="$1" key prowlarr_url="http://localhost:$PROWLARR_PORT/api/v1"
  wait_for Prowlarr "http://localhost:$PROWLARR_PORT/ping"
  key="$(prowlarr_key)"
  if ! curl -sf -H "X-Api-Key: $key" "$prowlarr_url/indexer" | grep -q '"definitionName": *"pandacd"'; then
    curl -sf -H "X-Api-Key: $key" "$prowlarr_url/indexer/schema" | python3 -I -c '
import json, sys
schema = next(s for s in json.load(sys.stdin) if s.get("definitionName") == "pandacd")
json.dump({**schema, "enable": True, "appProfileId": 1}, sys.stdout)' |
      curl -sf -o /dev/null -H "X-Api-Key: $key" -H 'Content-Type: application/json' -d @- "$prowlarr_url/indexer" ||
      echo "couldn't add PandaCD to Prowlarr; add an indexer at http://localhost:$PROWLARR_PORT" >&2
  fi
  grep -q '"http://retriever-prowlarr:8080"' <<<"$plugins" && return 0
  for _ in $(seq 1 30); do
    melodex POST /plugins -o /dev/null -d "{\"endpoint\":\"http://retriever-prowlarr:8080\",\"settings\":{\"prowlarrUrl\":\"http://prowlarr:9696\",\"prowlarrApiKey\":\"$key\",\"qbittorrentUrl\":\"http://qbittorrent:8080\"}}" && return 0
    sleep 1
  done
  echo "couldn't add the Prowlarr plugin" >&2
  exit 1
}

status() {
  local scan
  scan="$(subsonic getScanStatus)"
  echo "Navidrome: $navidrome_url (user $NAVIDROME_USER, password in sandbox/.env)"
  echo "  tracks indexed: $(sed -n 's/.*"count":\([0-9]*\).*/\1/p' <<<"$scan")"
  echo "  artists: $(subsonic getArtists | grep -o '"albumCount"' | wc -l)"
  echo "Melodex:   http://localhost:$MELODEX_PORT (user $MELODEX_USER, password in sandbox/.env) -> $(curl -sf "$melodex_url/health" || echo 'not running')"
  sign_in 2>/dev/null || true
  melodex GET /plugins 2>/dev/null | python3 -I -c '
import json, sys
for p in json.load(sys.stdin):
    message = p.get("healthMessage")
    print("  " + p["name"] + ": " + (p.get("health") or "not tested") + (" (" + message + ")" if message else ""))' || true
  echo "Prowlarr:    http://localhost:$PROWLARR_PORT"
  echo "qBittorrent: http://localhost:$QBITTORRENT_PORT (no login from this computer)"
  echo "Qobuz and Tidal stand-ins: http://localhost:$STANDIN_PORT"
  echo "Bandcamp collection:       http://localhost:$STANDIN_PORT/bandcamp (buy things here to fetch them)"
  echo "Library copy: $MUSIC"
  echo "Inbox:        $INBOX (drop files here, then Retry the request)"
}

remove() {
  "$ENGINE" rm -f "${CONTAINERS[@]}" >/dev/null 2>&1 || true
}

up() {
  if [[ ! -d "$TESTLIB" ]]; then
    echo "Generating the test library..."
    python3 -I "$ROOT/tools/testlib/generate.py"
  fi
  if [[ ! -d "$MUSIC" ]]; then
    echo "Copying the test library to $MUSIC..."
    cp -r "$TESTLIB" "$MUSIC"
  fi
  mkdir -p "$INBOX"
  build only-missing

  "$ENGINE" network inspect "$NETWORK" >/dev/null 2>&1 || "$ENGINE" network create "$NETWORK" >/dev/null
  remove

  "$ENGINE" run -d --name melodex-sandbox-navidrome "${SHARED[@]}" --network-alias navidrome \
    -p "$NAVIDROME_PORT:4533" \
    -v "$MUSIC:/music:ro,z" -v melodex-sandbox-navidrome:/data \
    -e ND_LOGLEVEL=warn -e ND_SCANNER_SCHEDULE=0 -e ND_ENABLEINSIGHTSCOLLECTOR=false \
    "$NAVIDROME_IMAGE" >/dev/null
  # The core first, so the staging volume takes its ownership from the core's image. It restarts when it stops, as in
  # the compose file, so a factory reset brings it back.
  "$ENGINE" run -d --name melodex-sandbox-core "${SHARED[@]}" --network-alias melodex --restart unless-stopped \
    -p "$MELODEX_PORT:8080" \
    -v "$MUSIC:/music:ro,z" -v melodex-sandbox-config:/config -v melodex-sandbox-staging:/staging \
    localhost/melodex/core:dev >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-beets "${SHARED[@]}" --network-alias beets-worker "${AS_YOU[@]}" \
    -v "$MUSIC:/music:z" -v melodex-sandbox-staging:/staging \
    localhost/melodex/beets-worker:dev >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-ytdlp "${SHARED[@]}" --network-alias retriever-ytdlp "${AS_YOU[@]}" \
    -v melodex-sandbox-staging:/staging \
    localhost/melodex/retriever-ytdlp:dev >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-inbox "${SHARED[@]}" --network-alias retriever-inbox "${AS_YOU[@]}" \
    -v "$INBOX:/inbox:z" -v melodex-sandbox-staging:/staging \
    localhost/melodex/retriever-inbox:dev >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-archive "${SHARED[@]}" --network-alias retriever-archive "${AS_YOU[@]}" \
    -v melodex-sandbox-staging:/staging \
    localhost/melodex/retriever-archive:dev >/dev/null

  # qBittorrent lets anyone it can reach in without a login: its port is only open to this computer, and the
  # plugin reaches it inside the sandbox network.
  # The downloads volume starts out root's; qBittorrent runs as PUID.
  "$ENGINE" run --rm --entrypoint sh -v melodex-sandbox-qbittorrent:/config -v melodex-sandbox-downloads:/downloads \
    -e PUID="$(id -u)" -e PGID="$(id -g)" "$QBITTORRENT_IMAGE" -c '
    mkdir -p /config/qBittorrent /downloads/melodex && chown -R "$PUID:$PGID" /downloads
    [ -f /config/qBittorrent/qBittorrent.conf ] || printf "%s\n" "[Preferences]" \
      "WebUI\\AuthSubnetWhitelistEnabled=true" "WebUI\\AuthSubnetWhitelist=0.0.0.0/0, ::/0" \
      "WebUI\\LocalHostAuth=false" "WebUI\\HostHeaderValidation=false" \
      "Downloads\\SavePath=/downloads/" > /config/qBittorrent/qBittorrent.conf' >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-qbittorrent "${SHARED[@]}" --network-alias qbittorrent \
    -p "127.0.0.1:$QBITTORRENT_PORT:8080" -e WEBUI_PORT=8080 -e PUID="$(id -u)" -e PGID="$(id -g)" \
    -v melodex-sandbox-qbittorrent:/config -v melodex-sandbox-downloads:/downloads \
    "$QBITTORRENT_IMAGE" >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-prowlarr "${SHARED[@]}" --network-alias prowlarr \
    -p "127.0.0.1:$PROWLARR_PORT:9696" -e PUID="$(id -u)" -e PGID="$(id -g)" \
    -v melodex-sandbox-prowlarr:/config \
    "$PROWLARR_IMAGE" >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-torrents "${SHARED[@]}" --network-alias retriever-prowlarr "${AS_YOU[@]}" \
    -v melodex-sandbox-staging:/staging -v melodex-sandbox-downloads:/downloads:ro \
    localhost/melodex/retriever-prowlarr:dev >/dev/null

  # Stand-ins for Qobuz, Tidal and Bandcamp, run with the Tidal plugin's image, which has ffmpeg to make their tones.
  "$ENGINE" run -d --name melodex-sandbox-standin "${SHARED[@]}" --network-alias streaming-standin \
    -p "127.0.0.1:$STANDIN_PORT:8080" -e PUBLIC_URL="http://localhost:$STANDIN_PORT" \
    -e INTERNAL_URL=http://streaming-standin:8080 -v "$SANDBOX/standins:/standin:ro,z" \
    localhost/melodex/retriever-tidal:dev uvicorn streaming:app --app-dir /standin --host 0.0.0.0 --port 8080 >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-qobuz "${SHARED[@]}" --network-alias retriever-qobuz "${AS_YOU[@]}" \
    -v melodex-sandbox-staging:/staging -e QOBUZ_API=http://streaming-standin:8080/qobuz/api.json/0.2 \
    localhost/melodex/retriever-qobuz:dev >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-tidal "${SHARED[@]}" --network-alias retriever-tidal "${AS_YOU[@]}" \
    -v melodex-sandbox-staging:/staging -v melodex-sandbox-tidal:/data \
    -e TIDAL_API=http://streaming-standin:8080/tidal/v1 -e TIDAL_AUTH=http://streaming-standin:8080/tidal/oauth2 \
    localhost/melodex/retriever-tidal:dev >/dev/null
  "$ENGINE" run -d --name melodex-sandbox-bandcamp "${SHARED[@]}" --network-alias retriever-bandcamp "${AS_YOU[@]}" \
    -v melodex-sandbox-staging:/staging -e BANDCAMP_URL=http://streaming-standin:8080/bandcamp \
    localhost/melodex/retriever-bandcamp:dev >/dev/null

  wait_for Navidrome "$navidrome_url/ping"
  # First run only: Navidrome accepts creating the admin while it has no users.
  curl -sf -o /dev/null -X POST "$navidrome_url/auth/createAdmin" -H 'Content-Type: application/json' \
    -d "{\"username\":\"$NAVIDROME_USER\",\"password\":\"$NAVIDROME_PASSWORD\"}" || true
  wait_for Melodex "$melodex_url/health"
  rm -f "$COOKIES"
  sign_in
  configure

  subsonic startScan >/dev/null
  wait_for_scan
  status
}

case "${1:-}" in
  up) up ;;
  build) build ;;
  status) status ;;
  scan) subsonic startScan >/dev/null; wait_for_scan; status ;;
  down|reset)
    remove
    "$ENGINE" volume rm -f "${VOLUMES[@]}" >/dev/null 2>&1 || true
    "$ENGINE" network rm "$NETWORK" >/dev/null 2>&1 || true
    if [[ "$1" == reset ]]; then
      # Files the containers wrote may belong to a mapped user; let the engine remove them if needed.
      rm -f "$COOKIES"
      rm -rf "$MUSIC" "$INBOX" 2>/dev/null || "$ENGINE" unshare rm -rf "$MUSIC" "$INBOX"
    fi
    echo "Sandbox removed." ;;
  *) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
