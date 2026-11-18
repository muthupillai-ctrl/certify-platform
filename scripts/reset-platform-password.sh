#!/usr/bin/env bash
# Reset a platform operator's (Platform login) password.
#
# Usage:
#   scripts/reset-platform-password.sh local <email>   # DEV system (localhost:4200/5080), docker-compose.yml Postgres
#   scripts/reset-platform-password.sh test  <email>   # TEST system (certyfi.onnurendu.shop), certyfi-test-postgres on pwms-server, over ssh
#   scripts/reset-platform-password.sh prod  <email>   # PRODUCTION, certyfi-postgres on pwms-server, over ssh
#
# Each system has its own database: a reset changes only the one you name.
#
# The new password is asked for twice and never appears on a command line or
# in shell history. It's hashed here with bcrypt (cost 12, via htpasswd, which
# ships with macOS) and only the hash is sent to the database — the same kind
# of hash BCrypt.Net writes in PlatformAuthController. There's no API route
# for this on purpose (see SuperAdminSeeder): it needs database access.
#
# Sessions already signed in are ended (SessionVersion goes up).

set -euo pipefail

usage() {
  echo "Usage: $0 local|test|prod <email>" >&2
  exit 1
}

[[ $# -eq 2 ]] || usage
target="$1"
email="$(printf '%s' "$2" | tr '[:upper:]' '[:lower:]')"

# Emails go into SQL below, so allow only ordinary address characters.
if [[ ! "$email" =~ ^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}$ ]]; then
  echo "Not a valid email address: $2" >&2
  exit 1
fi

case "$target" in
  local)
    container="$(docker ps --filter 'name=certyfi-platform-postgres' --format '{{.Names}}' | head -n 1)"
    if [[ -z "$container" ]]; then
      echo "The local Postgres isn't running. Start it with: docker compose up -d postgres" >&2
      exit 1
    fi
    run_psql() { docker exec -i "$container" psql -q -v ON_ERROR_STOP=1 -U certyfi -d certyfi; }
    ;;
  test)
    if ! ssh pwms-server "docker ps --format '{{.Names}}'" | grep -qx certyfi-test-postgres; then
      echo "The test system isn't running. Start it with: scripts/systems.sh test start" >&2
      exit 1
    fi
    run_psql() { ssh pwms-server 'docker exec -i certyfi-test-postgres psql -q -v ON_ERROR_STOP=1 -U certyfi -d certyfi'; }
    ;;
  prod)
    run_psql() { ssh pwms-server 'docker exec -i certyfi-postgres psql -q -v ON_ERROR_STOP=1 -U certyfi -d certyfi'; }
    ;;
  *)
    usage
    ;;
esac

command -v htpasswd >/dev/null || { echo "htpasswd not found (it's part of apache2-utils / httpd-tools)." >&2; exit 1; }

read -r -s -p "New password for $email: " password; echo
read -r -s -p "Type it again: " confirm; echo
if [[ "$password" != "$confirm" ]]; then
  echo "The two passwords don't match. Nothing changed." >&2
  exit 1
fi
if (( ${#password} < 12 )); then
  echo "Use at least 12 characters. Nothing changed." >&2
  exit 1
fi
if (( ${#password} > 72 )); then
  echo "bcrypt only uses the first 72 bytes; use 72 characters or fewer. Nothing changed." >&2
  exit 1
fi

# htpasswd prints "user:$2y$12$..."; $2y$ and $2a$ are the same algorithm,
# and $2a$ is what BCrypt.Net itself writes.
hash="$(printf '%s' "$password" | htpasswd -niBC 12 certyfi | cut -d: -f2- | sed 's/^\$2y\$/$2a$/')"
unset password confirm
if [[ ! "$hash" =~ ^\$2a\$12\$[./A-Za-z0-9]{53}$ ]]; then
  echo "Hashing failed. Nothing changed." >&2
  exit 1
fi

# Both values are passed as psql variables (:'name' quotes them safely).
result="$(run_psql <<SQL
\set email '$email'
\set hash '$hash'
\pset tuples_only on
\pset format unaligned
UPDATE platform.platform_admins
   SET "PasswordHash" = :'hash',
       "PasswordChangedAt" = now(),
       "MustChangePassword" = false,
       -- Ends every signed-in session of this account.
       "SessionVersion" = "SessionVersion" + 1
 WHERE lower("Email") = :'email'
RETURNING 'updated|' || "FullName" || '|' || CASE WHEN "IsActive" THEN 'active' ELSE 'deactivated' END;
SQL
)"

if [[ "$result" == updated\|* ]]; then
  IFS='|' read -r _ name active <<<"$result"
  echo "Password reset for $email ($name) on $target."
  if [[ "$active" != "active" ]]; then
    echo "Note: this account is deactivated, so it still can't sign in." >&2
  fi
else
  echo "No platform admin with email $email on $target. Accounts there:" >&2
  run_psql <<'SQL' >&2
SELECT "Email", "FullName", CASE WHEN "IsActive" THEN 'active' ELSE 'deactivated' END AS status
  FROM platform.platform_admins ORDER BY "Email";
SQL
  exit 1
fi
