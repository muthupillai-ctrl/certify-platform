#!/usr/bin/env bash
# One place to see and run Certyfi's three systems.
#
#   scripts/systems.sh status          # is each system up? (dev, test, production)
#
#   scripts/systems.sh test start      # start the TEST system on pwms-server (keeps its data)
#   scripts/systems.sh test stop       # stop it (data is kept)
#   scripts/systems.sh test restart
#   scripts/systems.sh test update     # rebuild it with the latest code (= scripts/deploy.sh test)
#   scripts/systems.sh test logs       # follow its backend log (Ctrl+C to stop watching)
#   scripts/systems.sh test open       # open it in the browser
#   scripts/systems.sh test inbox      # open its email inbox (login: /data/apps/certyfi-test/mailpit-login.txt on the server)
#
#   scripts/systems.sh dev start       # start the DEV system in the background
#   scripts/systems.sh dev stop
#   scripts/systems.sh dev logs        # follow the dev API log
#   scripts/systems.sh dev open
#
# Production is only checked here; it changes only through
# scripts/deploy.sh prod.
#
#   DEV    http://localhost:4200           your working copy; reloads as code changes
#   TEST   https://certyfi.onnurendu.shop  built like production; check changes here first
#   PROD   https://certyfi.ahamsys.com
# TEST and PROD both run on pwms-server.

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOGS="$ROOT/.logs"
TEST_URL=https://certyfi.onnurendu.shop
TEST_HOST=certyfi.onnurendu.shop
# Runs docker compose for the test system on the server.
test_compose() { ssh pwms-server "cd /data/apps/certyfi-test && docker compose --env-file .env.test $*"; }
# Through the server's own proxy, so it works even before DNS is set up.
test_ok() { [[ "$(ssh -o ConnectTimeout=5 pwms-server "curl -s -m 5 -o /dev/null -w '%{http_code}' -H 'Host: $TEST_HOST' http://127.0.0.1$1" 2>/dev/null)" =~ ^(200|302|401)$ ]]; }
test_container_up() { ssh -o ConnectTimeout=5 pwms-server "docker ps --format '{{.Names}}'" 2>/dev/null | grep -qx "$1"; }

green() { printf '\033[32m%s\033[0m' "$1"; }
red() { printf '\033[31m%s\033[0m' "$1"; }
http_ok() { [[ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$1")" =~ ^(200|302)$ ]]; }
line() { # label, ok?, detail
  if [[ "$2" == yes ]]; then printf '  %-22s %s  %s\n' "$1" "$(green 'up  ')" "$3"; else printf '  %-22s %s  %s\n' "$1" "$(red 'down')" "$3"; fi
}
container_up() { docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$1"; }
yes_if() { if "$@"; then echo yes; else echo no; fi; }

wait_for() { # url, name
  for _ in $(seq 1 90); do http_ok "$1" && return 0; sleep 2; done
  echo "  $2 didn't come up; see the log ($0 ${3:-test} logs)." >&2
  return 1
}

status() {
  echo
  echo "DEV  (your working copy)"
  line "Website" "$(yes_if http_ok http://localhost:4200/)" "http://localhost:4200"
  line "API" "$(yes_if http_ok http://localhost:5080/health)" "http://localhost:5080"
  line "Database" "$(yes_if container_up certyfi-platform-postgres-1)" "certyfi-platform-postgres-1 (port 5433)"
  line "Email inbox" "$(yes_if http_ok http://localhost:8025/)" "http://localhost:8025"
  echo
  echo "TEST (built like production, on pwms-server)"
  line "Website + API" "$(yes_if test_ok /health)" "$TEST_URL"
  line "Database" "$(yes_if test_container_up certyfi-test-postgres)" "certyfi-test-postgres (server, 127.0.0.1:5437)"
  line "Email inbox" "$(yes_if test_ok /mail/)" "$TEST_URL/mail/"
  echo
  echo "PRODUCTION"
  line "Website + API" "$(yes_if http_ok https://certyfi.ahamsys.com/health)" "https://certyfi.ahamsys.com"
  echo
}

test_cmd() {
  case "${1:-}" in
    start)
      echo "Starting the TEST system on pwms-server..."
      test_compose up -d >/dev/null 2>&1 || { echo "Couldn't start it on the server." >&2; exit 1; }
      for _ in $(seq 1 60); do test_ok /health && break; sleep 3; done
      test_ok /health && echo "TEST is up: $TEST_URL  (emails: $TEST_URL/mail/)" || { echo "  It didn't come up; see: $0 test logs" >&2; exit 1; }
      ;;
    stop)
      test_compose stop >/dev/null 2>&1
      echo "TEST stopped (its data is kept; start it again with: $0 test start)"
      ;;
    restart) test_cmd stop; test_cmd start ;;
    update) "$ROOT/scripts/deploy.sh" test ;;
    logs) ssh -t pwms-server docker logs -f --tail 100 certyfi-test-backend ;;
    open) open "$TEST_URL" ;;
    inbox) open "$TEST_URL/mail/" ;;
    *) usage ;;
  esac
}

dev_cmd() {
  mkdir -p "$LOGS"
  case "${1:-}" in
    start)
      echo "Starting the DEV system..."
      (cd "$ROOT" && docker compose up -d postgres >/dev/null 2>&1) || { echo "Couldn't start the dev database. Is Docker running?" >&2; exit 1; }
      if http_ok http://localhost:5080/health; then
        echo "  API already running on 5080"
      else
        (cd "$ROOT/backend/src/Certyfi.Api" && nohup dotnet run > "$LOGS/dev-api.log" 2>&1 &)
      fi
      if http_ok http://localhost:4200/; then
        echo "  Website already running on 4200"
      else
        (cd "$ROOT/frontend" && nohup npx ng serve --port 4200 > "$LOGS/dev-web.log" 2>&1 &)
      fi
      wait_for http://localhost:5080/health "The dev API" dev && wait_for http://localhost:4200/ "The dev website" dev \
        && echo "DEV is up: http://localhost:4200  (logs in .logs/)"
      ;;
    stop)
      for port in 5080 4200; do
        for pid in $(lsof -tiTCP:$port -sTCP:LISTEN 2>/dev/null); do kill "$pid" 2>/dev/null; done
      done
      pkill -f "Certyfi.Api/bin/Debug" 2>/dev/null
      echo "DEV stopped (its database keeps running; data is kept)"
      ;;
    restart) dev_cmd stop; sleep 2; dev_cmd start ;;
    logs) tail -f "$LOGS/dev-api.log" ;;
    open) open http://localhost:4200 ;;
    *) usage ;;
  esac
}

usage() {
  sed -n '2,27p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
}

case "${1:-}" in
  status) status ;;
  test) test_cmd "${2:-}" ;;
  dev) dev_cmd "${2:-}" ;;
  *) usage ;;
esac
