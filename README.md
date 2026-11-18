# Certyfi Platform — Scaffold

Multi-tenant digital credential/badge/verification platform. This scaffold
covers **project structure, multi-tenancy (schema-per-tenant), and RBAC
foundations** — the credential/badge/issuance domain (technical note
section 4.5) is not yet built; this is the base it sits on.

Written to match the approach in `docs/technical-approach-summary.md`
(condensed from the fuller capability note): purpose-built, clean codebase,
ASP.NET Core + Angular + PostgreSQL, Trust-Ledger consumed as a separate
service rather than merged in.

## Repo layout

```
backend/
  src/
    Certyfi.Domain/          entities, enums — no framework dependencies
    Certyfi.Infrastructure/  EF Core, multi-tenancy, auth, provisioning
    Certyfi.Api/             controllers, Program.cs, config
frontend/
  src/app/
    core/                    auth service, interceptor, guards, models
    features/auth/login/     first screen — everything else plugs in here
docker-compose.yml           local Postgres only
```

## How multi-tenancy works here

- One Postgres **schema per tenant** (`tenant_<slug>`), plus a `platform`
  schema for the tenant registry and platform-operator accounts.
- `TenantResolutionMiddleware` resolves the tenant from `X-Tenant-Slug`
  header (used by the Angular app and by partner API clients) or from the
  request subdomain, and rejects the request outright if it can't.
- `TenantDbContext` reads its schema from request-scoped `ITenantContext`.
  `TenantModelCacheKeyFactory` makes EF Core cache one compiled model per
  distinct schema instead of reusing the first tenant's model for everyone
  — this is the part most first-attempt EF multi-tenancy scaffolds get
  wrong, so it's called out explicitly in code comments too.
- `TenantClaimGuardMiddleware` cross-checks the JWT's `tenant_id` claim
  against the resolved tenant on every authenticated request — this, not
  JWT validation alone, is what actually prevents a token issued for one
  institution being used against another's data.

Read the comments in `Certyfi.Infrastructure/MultiTenancy/` and
`Persistence/TenantDbContext.cs` before changing any of this — the reasoning
is written in place, not just here.

## Local setup

Requires: .NET SDK (10.0 to match the csproj `TargetFramework`; drop to
whatever LTS is actually installed if 10 isn't available yet — the code
itself doesn't use anything version-specific), Node 20+, Docker.

```bash
# 1. Start Postgres
docker compose up -d

# 2. Backend secrets (never commit these — see .gitignore)
cd backend/src/Certyfi.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=certyfi;Username=certyfi;Password=certyfi_dev_only"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"
# Encrypts every tenant's credential-signing private key at rest — must
# decode to exactly 32 bytes (AES-256); openssl rand -base64 32 does that.
dotnet user-secrets set "Kms:LocalMasterKey" "$(openssl rand -base64 32)"

# 3. Restore and build
cd ../../
dotnet restore
dotnet build

# 4. Create the platform schema/migrations (first time only)
cd src/Certyfi.Infrastructure
dotnet ef migrations add InitialPlatform --context PlatformDbContext --startup-project ../Certyfi.Api
dotnet ef database update --context PlatformDbContext --startup-project ../Certyfi.Api

# Tenant-schema migrations are generated once and then physically applied
# per-tenant-schema at provisioning time (see TenantProvisioningService) —
# they are NOT applied via `dotnet ef database update` against a fixed
# schema, since there isn't one until a tenant exists.
dotnet ef migrations add InitialTenant --context TenantDbContext --startup-project ../Certyfi.Api

# 5. Run the API
cd ../Certyfi.Api
dotnet run
# Swagger at https://localhost:<port>/swagger

# 6. Frontend
cd ../../../frontend
npm install
npm start
# http://localhost:4200
```

There is no seeded SuperAdmin account yet — see "Known gaps" below.

## Known gaps (deliberately left for the next work session, not hidden)

1. **`TenantProvisioningService` DI wiring is unfinished.** It needs a
   `TenantDbContext` constructed against an *explicit* schema outside any
   HTTP request (provisioning happens before a tenant can be "the resolved
   tenant" of a request). `AddDbContextFactory` as wired in `Program.cs`
   won't see the per-request `ITenantContext` as-is. Cleanest fix is
   probably a small `TenantDbContextFactory` that takes a schema name
   directly rather than reading it from DI — flagged in code comments at
   both ends (`Program.cs` and `TenantProvisioningService.cs`).
2. **No SuperAdmin seed.** Need a one-time seed script/migration to create
   the first `PlatformAdmin` row — there's currently no way to create one
   through the API by design (platform admin creation shouldn't be a public
   endpoint).
3. **No refresh tokens.** Access tokens are 30 minutes with no refresh flow
   — fine for scaffold/demo, not for a real deployment.
4. **No rate limiting yet** on `/api/v1/auth/login` or (once built) the
   public verification endpoint — called out in the technical note as a
   requirement (4.5.4, section 5) and not yet implemented.
5. **Angular app is a login screen and route guards only.** The admin
   portal, credential/badge UI, and public verification page aren't started.
6. **No automated tests yet.** Given the tenant-isolation logic is the
   single most important correctness property of this system, an
   integration test that asserts a Tenant-A token is rejected against
   Tenant-B's resolved schema should exist before any real feature work
   lands on top of this scaffold.

## Roles (seeded per tenant at provisioning)

`SuperAdmin` (platform-level, not tenant-scoped), `InstitutionAdmin`,
`Registrar`, `Approver`, `Learner`, `Verifier`, `Developer` — matches the
actor model in the technical approach note. Fine-grained permissions
(beyond role checks) are not modelled yet; `Role` is a real table rather
than a hardcoded enum specifically so that's a data change, not a schema
migration, when it's needed.
