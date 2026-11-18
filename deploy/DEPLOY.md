# Deploying Certyfi to pwms-server (certyfi.ahamsys.com)

First deployed 2026-09-26. Layout on the box (`ssh pwms-server`, host `muthu-linux`):

```
/data/apps/certyfi/
  compose.yaml          <- deploy/compose.yaml
  .env.production       <- secrets, chmod 600, generated on the box, never copied off it
  backend/              <- backend/ (src + Certyfi.sln), built into certyfi-backend
  frontend/www/         <- Angular production build
  frontend/nginx.conf   <- deploy/nginx/certyfi-frontend.conf
/data/reverse-proxy/nginx/conf.d/certyfi.conf  <- deploy/nginx/certyfi.conf
```

Containers (all on `apps-network`, none publicly exposed):

| Container | Purpose |
|---|---|
| `certyfi-postgres` | PostgreSQL 16, volume `certyfi_certyfi_pgdata`, host port `127.0.0.1:5436` only |
| `certyfi-backend` | API on :8080, runs as non-root, uploads in volume `certyfi_certyfi_uploads` |
| `certyfi-frontend` | nginx serving the Angular build |

Traffic: Cloudflare (HTTPS, Always Use HTTPS) → tunnel → shared `reverse-proxy`
nginx → `certyfi.conf` → `/api/`, `/uploads/`, `/health` to the backend,
everything else to the frontend container. Upstreams resolve at request time,
so Certyfi being down never stops the shared proxy or other sites.

## Routine update

**Use the script:** `scripts/deploy.sh test` (the TEST system, also on pwms-server at https://certyfi.onnurendu.shop,
see `deploy/test/README.md`), check it, then `scripts/deploy.sh prod`. It does
the backup, platform migration, backend, institution migrations and frontend
below, in that order. The manual steps are kept here for reference.


From the repo root on a dev machine:

```bash
# Backend
rsync -a --delete --exclude bin/ --exclude obj/ --exclude tests/ \
  --exclude appsettings.Development.json backend/ pwms-server:/data/apps/certyfi/backend/
ssh pwms-server 'cd /data/apps/certyfi && docker compose --env-file .env.production up -d --build certyfi-backend'

# Frontend (no container restart needed)
(cd frontend && npx ng build --configuration production)
rsync -a --delete frontend/dist/certyfi-frontend/browser/ pwms-server:/data/apps/certyfi/frontend/www/
```

Always pass `--env-file .env.production` to `docker compose`: `compose.yaml`
reads `POSTGRES_PASSWORD` from it.

Before a deploy that includes migrations, take a backup:

```bash
ssh pwms-server 'docker exec certyfi-postgres pg_dump -U certyfi certyfi | gzip > /data/backups/certyfi/certyfi-$(date +%Y%m%d-%H%M%S)-predeploy.sql.gz'
```

If `deploy/nginx/certyfi-frontend.conf` changed, copy it and **restart** (not
reload) the frontend container. It's bind-mounted as a single file, and rsync
replaces the file rather than editing it, so a running container keeps seeing
the old one:

```bash
rsync -a deploy/nginx/certyfi-frontend.conf pwms-server:/data/apps/certyfi/frontend/nginx.conf
ssh pwms-server 'cd /data/apps/certyfi && docker compose --env-file .env.production restart certyfi-frontend'
```

The frontend is also an installable offline app (Angular service worker).
`ngsw-worker.js`, `ngsw.json` and `manifest.webmanifest` are served
`no-store` so Cloudflare never caches them; check after a deploy with
`curl -sI https://certyfi.ahamsys.com/ngsw-worker.js` (expect `cf-cache-status: BYPASS`).

### Migrations

Production never migrates at startup.

- **Platform schema** (new migration under `Migrations/` for `PlatformDbContext`):
  ```bash
  cd backend
  dotnet ef migrations script --idempotent --context PlatformDbContext \
    -p src/Certyfi.Infrastructure -s src/Certyfi.Api -o /tmp/platform.sql
  ssh pwms-server 'docker exec -i certyfi-postgres psql -v ON_ERROR_STOP=1 -U certyfi -d certyfi' < /tmp/platform.sql
  ```
- **Tenant schemas** (new migration under `Migrations/TenantDb`): after deploying
  the backend, sign in as SuperAdmin and run the migrate-schemas action
  (`POST /api/v1/platform/tenants/migrate-schemas`). New tenants are provisioned
  with every migration already applied.

## First-time setup (done 2026-09-26, kept for rebuilding the box)

1. `mkdir -p /data/apps/certyfi/backend /data/apps/certyfi/frontend/www`, then copy files as above.
2. On the box, generate `.env.production` from `.env.production.example` with
   `openssl rand` values (`umask 077` first). **Back up `Kms__LocalMasterKey`
   somewhere safe**: without it, existing tenant signing keys can't be decrypted
   and no new credential can be signed under them.
3. `docker compose --env-file .env.production up -d --build`
4. Apply the platform migration script (above).
5. Create the first SuperAdmin directly in `platform.platform_admins` with a
   bcrypt (cost 12) password hash. Production has no API for this on purpose.
6. Copy `nginx/certyfi.conf` to `/data/reverse-proxy/nginx/conf.d/`, then
   `docker exec reverse-proxy nginx -t && docker exec reverse-proxy nginx -s reload`.
7. Public hostname `certyfi.ahamsys.com` routed to the box through Cloudflare.
8. Check: `curl https://certyfi.ahamsys.com/health`, then sign in at
   `/platform/login` and create the first institution.

## Backups

Not yet automated. Minimum: a nightly
`docker exec certyfi-postgres pg_dump -U certyfi certyfi | gzip > /data/backups/certyfi-$(date +%F).sql.gz`
plus a copy of `.env.production`.

## Reverse proxy config (private home page)

`deploy/nginx/certyfi.conf` is the shared reverse proxy's config for Certyfi
(`/data/reverse-proxy/nginx/conf.d/certyfi.conf` on the box). It makes the home
page and `/assets/presentation/` require a pass from the mobile-number gate
(`/access`, see AccessController); everything else is open. After changing it:

```bash
rsync -a deploy/nginx/certyfi.conf pwms-server:/data/reverse-proxy/nginx/conf.d/certyfi.conf
ssh pwms-server 'docker exec reverse-proxy nginx -t && docker exec reverse-proxy nginx -s reload'
```

The shared proxy serves other sites too: always run `nginx -t` before reloading.
Add at least one number under Platform › Home page access before this goes
live, or nobody (including us) can open the home page.

