#!/usr/bin/env bash
# Deploy Certyfi to the TEST system or to PRODUCTION (both on pwms-server).
#
#   scripts/deploy.sh test    # https://certyfi.onnurendu.shop, inbox /mail/ (pwms-server)
#   scripts/deploy.sh prod    # https://certyfi.ahamsys.com
#
# Same code, same steps, in this order, for both:
#   1. (prod) database backup
#   2. platform migration (idempotent SQL script)
#   3. backend: build and restart
#   4. institution (tenant) migrations, through the new backend
#   5. frontend: production build, copied in
#   6. health check
# Deploy to test first, check it there, then deploy the same code to prod.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:-}"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT

case "$TARGET" in
  test|prod) ;;
  *) echo "Usage: $0 test|prod" >&2; exit 1 ;;
esac

if [[ "$TARGET" == prod ]]; then
  read -r -p "Deploy to PRODUCTION (certyfi.ahamsys.com)? Type 'prod' to continue: " answer
  [[ "$answer" == prod ]] || { echo "Cancelled."; exit 1; }
fi

step() { printf '\n== %s\n' "$*"; }

# Mints a 5-minute SuperAdmin token from an env file's Jwt settings and calls
# migrate-schemas. Runs wherever the env file is (locally for test, on the
# server for prod), so production's signing key never leaves the server.
MIGRATE_PY='
import base64, hashlib, hmac, json, sys, time, urllib.request
env_file, base_url, admin = sys.argv[1], sys.argv[2], sys.argv[3]
# Optional 4th: the Host header to send (when calling the proxy by IP address).
host = sys.argv[4] if len(sys.argv) > 4 else None
# "<id>|<session version>": platform tokens must carry the current session
# version of the account, or the API refuses them (PlatformAdmin.SessionVersion).
admin_id, _, session_version = admin.partition("|")
env = dict(l.split("=", 1) for l in open(env_file).read().splitlines() if "=" in l and not l.startswith("#"))
b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=").decode()
now = int(time.time())
head = b64(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
body = b64(json.dumps({"sub": admin_id, "role": "SuperAdmin", "sv": session_version or "0", "iss": env.get("Jwt__Issuer", "certyfi-platform"),
    "aud": env.get("Jwt__Audience", "certyfi-clients"), "nbf": now, "iat": now, "exp": now + 300}).encode())
sig = b64(hmac.new(env["Jwt__SigningKey"].encode(), f"{head}.{body}".encode(), hashlib.sha256).digest())
req = urllib.request.Request(base_url + "/api/v1/platform/tenants/migrate-schemas", data=b"{}", method="POST",
    headers={"Authorization": f"Bearer {head}.{body}.{sig}", "Content-Type": "application/json", "User-Agent": "certyfi-deploy", **({"Host": host} if host else {})})
try:
    r = urllib.request.urlopen(req, timeout=180); print("  tenant migration:", r.status, r.read().decode()[:200])
except urllib.error.HTTPError as e:
    print("  tenant migration FAILED:", e.code, e.read().decode()[:300]); sys.exit(1)
'

wait_healthy() {
  local url="$1"
  for _ in $(seq 1 60); do
    [[ "$(curl -s -o /dev/null -w '%{http_code}' "$url")" == 200 ]] && return 0
    sleep 3
  done
  echo "Not healthy: $url" >&2
  exit 1
}

step "Platform migration script"
(cd "$ROOT/backend" && dotnet ef migrations script --idempotent --context PlatformDbContext \
  -p src/Certyfi.Infrastructure -s src/Certyfi.Api -o "$SCRATCH/platform.sql" >/dev/null)

if [[ "$TARGET" == test ]]; then
  # The TEST system lives on pwms-server too, in /data/apps/certyfi-test
  # (deploy/test/), at https://certyfi.onnurendu.shop. Health checks
  # go through the server's own proxy, so they work without DNS.
  T=/data/apps/certyfi-test
  TEST_HOST=certyfi.onnurendu.shop
  test_healthy() {
    for _ in $(seq 1 60); do
      [[ "$(ssh pwms-server "curl -s -o /dev/null -w '%{http_code}' -H 'Host: $TEST_HOST' http://127.0.0.1/health")" == 200 ]] && return 0
      sleep 3
    done
    echo "Not healthy: $TEST_HOST" >&2
    exit 1
  }

  step "Backup (test)"
  ssh pwms-server "mkdir -p /data/backups/certyfi-test && f=/data/backups/certyfi-test/certyfi-test-\$(date +%Y%m%d-%H%M%S)-predeploy.sql.gz && docker exec certyfi-test-postgres pg_dump -U certyfi certyfi | gzip > \$f && ls -la \$f && ls -1t /data/backups/certyfi-test/*.sql.gz | tail -n +15 | xargs -r rm --"

  step "Platform migration"
  ssh pwms-server 'docker exec -i certyfi-test-postgres psql -q -v ON_ERROR_STOP=1 -U certyfi -d certyfi' < "$SCRATCH/platform.sql" 2>&1 | grep -v NOTICE || true

  step "Backend"
  rsync -a --delete --exclude bin/ --exclude obj/ --exclude tests/ --exclude appsettings.Development.json \
    "$ROOT/backend/" pwms-server:$T/backend/
  rsync -a "$ROOT/deploy/test/compose.yaml" pwms-server:$T/compose.yaml
  rsync -a "$ROOT/deploy/test/nginx.conf" pwms-server:$T/frontend/nginx.conf
  ssh pwms-server "cd $T && docker compose --env-file .env.test up -d --build 2>&1 | tail -1"
  test_healthy

  step "Institution migrations"
  ssh pwms-server "cd $T && ADMIN_ID=\$(docker exec certyfi-test-postgres psql -tA -U certyfi -d certyfi -c 'select \"Id\" || chr(124) || \"SessionVersion\" from platform.platform_admins where \"IsActive\" order by \"CreatedAt\" limit 1') && python3 -c '$MIGRATE_PY' .env.test http://127.0.0.1 \"\$ADMIN_ID\" $TEST_HOST"

  step "Frontend"
  (cd "$ROOT/frontend" && npx ng build --configuration production >/dev/null)
  rsync -a --delete "$ROOT/frontend/dist/certyfi-frontend/browser/" pwms-server:$T/frontend/www/

  test_healthy
  step "Done: https://$TEST_HOST (inbox https://$TEST_HOST/mail/)"
  exit 0
fi

# ---------- production ----------
step "Backup"
ssh pwms-server 'f=/data/backups/certyfi/certyfi-$(date +%Y%m%d-%H%M%S)-predeploy.sql.gz; docker exec certyfi-postgres pg_dump -U certyfi certyfi | gzip > $f && ls -la $f'

step "Platform migration"
ssh pwms-server 'docker exec -i certyfi-postgres psql -q -v ON_ERROR_STOP=1 -U certyfi -d certyfi' < "$SCRATCH/platform.sql" 2>&1 | grep -v NOTICE || true

step "Backend"
rsync -a --delete --exclude bin/ --exclude obj/ --exclude tests/ --exclude appsettings.Development.json \
  "$ROOT/backend/" pwms-server:/data/apps/certyfi/backend/
ssh pwms-server 'cd /data/apps/certyfi && docker compose --env-file .env.production up -d --build certyfi-backend 2>&1 | tail -1'
wait_healthy https://certyfi.ahamsys.com/health

step "Institution migrations"
ssh pwms-server "cd /data/apps/certyfi && ADMIN_ID=\$(docker exec certyfi-postgres psql -tA -U certyfi -d certyfi -c 'select \"Id\" || chr(124) || \"SessionVersion\" from platform.platform_admins where \"IsActive\" order by \"CreatedAt\" limit 1') && python3 -c '$MIGRATE_PY' .env.production https://certyfi.ahamsys.com \"\$ADMIN_ID\""

step "Frontend"
(cd "$ROOT/frontend" && npx ng build --configuration production >/dev/null)
rsync -a --delete "$ROOT/frontend/dist/certyfi-frontend/browser/" pwms-server:/data/apps/certyfi/frontend/www/

wait_healthy https://certyfi.ahamsys.com/health
step "Done: https://certyfi.ahamsys.com"
