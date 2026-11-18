# Certyfi TEST system (on pwms-server)

A second, separate Certyfi built and run exactly like production, for
checking changes before they go live. Since 2026-10-17 it runs on
pwms-server next to production (it used to run on the MacBook).

| | Test | Production |
|---|---|---|
| Web | https://certyfi.onnurendu.shop | https://certyfi.ahamsys.com |
| Emails | caught at https://certyfi.onnurendu.shop/mail/ (password), never sent | sent through Resend |
| On the server | `/data/apps/certyfi-test/` | `/data/apps/certyfi/` |
| Database | `certyfi-test-postgres`, 127.0.0.1:5437 on the server | `certyfi-postgres`, 127.0.0.1:5436 |
| Keys | `/data/apps/certyfi-test/.env.test` (its own) | `/data/apps/certyfi/.env.production` |
| Proxy config | `/data/reverse-proxy/nginx/conf.d/certyfi-test.conf` (from `deploy/nginx/certyfi-test.conf`) | `certyfi.conf` |
| Banner | "TEST SYSTEM" on every page; not indexed by search engines | none |

Nothing is shared: different master key, login keys and signing keys, so a
login, link, API key secret or signature from one never works on the other.
Webhooks follow production's rules (public `https://` addresses only).

`deploy/test/.env.test` here is a private backup copy of the server's
`.env.test` (chmod 600). Its master key decrypts the test system's signing
keys and webhook secrets: without it they can't be read.

## Inbox

Every email the test system sends lands in the catch-all inbox at `/mail/`.
The login is in `/data/apps/certyfi-test/mailpit-login.txt` on the server
(`ssh pwms-server cat /data/apps/certyfi-test/mailpit-login.txt`). To change
it, write a new bcrypt line to `mailpit-auth`
(`docker run --rm httpd:2.4-alpine htpasswd -nbB <user> <password>`) and
restart `certyfi-test-mailpit`.

## Deploying

```bash
scripts/deploy.sh test     # backup, migrations, backend and frontend on the server; check it
scripts/deploy.sh prod     # same code to production (asks you to type "prod")
```

Test keeps its last 14 pre-deploy backups in `/data/backups/certyfi-test/`.

## Day to day

- `scripts/systems.sh status` (all three systems), `scripts/systems.sh test start|stop|restart|logs|open|inbox`.
- Reset a platform password: `scripts/reset-platform-password.sh test <email>`.

## Data

Copied from the MacBook test system on 2026-10-17 (7 institutions, with
their users, certificates and uploaded images; image links moved to the new
address). Its history: on 2026-10-09 production's test institution and the
local dev institutions were copied in; their old signing keys were retired
and their certificates still verify. Production data must not be copied to
test now that real institutions use production.
