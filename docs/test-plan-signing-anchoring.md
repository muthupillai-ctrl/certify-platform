# Test Plan — Real Signing + Anchoring (Phase 1 + Phase 2)

Covers per-tenant ECDSA credential signing (Phase 1) and OpenTimestamps
checkpoint anchoring to Bitcoin (Phase 2). Phase 2 has no frontend UI yet, so
everything below is API / CLI level.

## 0. Prerequisites

- Dev backend running with `ASPNETCORE_ENVIRONMENT=Development`, Postgres up.
- User-secrets set in `backend/src/Certyfi.Api`: `Jwt:SigningKey`,
  `Kms:LocalMasterKey`, `ConnectionStrings:Default`, `SuperAdminSeed:Email`,
  `SuperAdminSeed:Password`.
- Tools: `curl`, `psql`, `openssl`, `python3`, and the official OpenTimestamps
  CLI for independent proof checks: `pip3 install opentimestamps-client`
  (installs to `~/Library/Python/3.9/bin/ots` on macOS; not on PATH by default).

## 1. Automated coverage (run first)

```bash
cd backend && dotnet build && dotnet test
```

Expect **77/77 passing** (about 1 minute).

| Suite | Tests | What it proves |
|---|---|---|
| Pre-existing | 69 | Auth, tenancy, achievements, credentials, ledger, maker-checker, templates |
| `SigningTests` | 3 | Signature verifies against the publicly fetched key; per-tenant key isolation; unknown key id returns 404 |
| `OpenTimestampsSerializationTests` | 5 | Binary-format round trips, including a byte-identical replay of a real captured calendar response |
| `AnchoringTests` | 3 | Checkpoint creation against live calendar servers; idempotency; SuperAdmin-only gate |

`AnchoringTests` make real internet calls. An intermittent failure is more
likely a calendar-server timeout than a regression; rerun before investigating.

## 2. Manual walkthrough — Phase 1 (signing)

1. Log in as an Approver or InstitutionAdmin; draft and approve a credential
   (approver must differ from the requester).
2. `GET /api/v1/verify/{slug}/{credentialId}`. Confirm `canonicalHash`,
   `signature`, `signatureKeyId`, `signatureAlgorithm` (`ES256`) are non-null.
3. `GET /api/v1/verify/{slug}/signing-keys/{signatureKeyId}` with **no auth
   header and no `X-Tenant-Slug`**. Expect 200 and a PEM public key.
4. Verify independently with OpenSSL (not another .NET call):
   ```bash
   printf '%s' "<canonicalHash>" > /tmp/hash.txt
   echo "<signature base64>" | base64 -d > /tmp/sig.der
   echo "<publicKeyPem>" > /tmp/pub.pem
   openssl dgst -sha256 -verify /tmp/pub.pem -signature /tmp/sig.der /tmp/hash.txt
   ```
   Expect `Verified OK`.
5. Negative: alter one character of the hash and re-verify. Must fail.
6. UI: open the verify page and the admin credential "Verification" tab; click
   "Verify in this browser". Expect the valid state; tamper case shows invalid.

## 3. Manual walkthrough — Phase 2 (anchoring)

1. Log in as platform SuperAdmin: `POST /api/v1/platform/auth/login`.
2. Before any checkpoint covers the credential:
   - `GET /api/v1/verify/{slug}/{credentialId}` returns `anchorStatus: "NotYetCheckpointed"`.
   - `GET /api/v1/verify/{slug}/{credentialId}/anchor-proof` returns 404.
3. `POST /api/v1/platform/anchoring/checkpoint-now` (Bearer SuperAdmin token).
   Expect `checkpointsCreated >= 1`.
   - The background service also checkpoints automatically (about 30 s after
     startup, then hourly). If it already ran, `0` here is correct
     (idempotency), not a bug. Confirm via:
     ```sql
     SELECT "UpToSequenceNumber","Status","CreatedAt"
     FROM tenant_<slug>.ledger_checkpoints ORDER BY "CreatedAt" DESC;
     ```
4. Re-check the verify response: `anchorStatus: "Pending"`.
5. `GET .../anchor-proof` returns 200 with a binary `.ots` file. Save it and
   inspect with the official CLI:
   ```bash
   ~/Library/Python/3.9/bin/ots info /tmp/credential.ots
   ```
   Expect one or more `verify PendingAttestation('https://...')` lines, one per
   calendar that accepted the submission (up to four).
6. `POST /api/v1/platform/anchoring/upgrade-now` returns
   `checkpointsConfirmed: 0`. Expected: real Bitcoin confirmation takes hours.
   This proves the poll path runs cleanly against live calendars.
7. Authorization: repeat steps 3 and 6 with a tenant admin token. Expect
   `403 Forbidden`.
8. Idempotency: call `checkpoint-now` twice with no ledger activity between.
   The second call must return `0`.

## 4. Delayed verification (check back in 1–3 hours)

1. `POST /api/v1/platform/anchoring/upgrade-now` again.
2. If `checkpointsConfirmed > 0`, re-fetch the verify response. Expect
   `anchorStatus: "Confirmed"`, a real `anchorBlockHeight`, and
   `anchorMerkleRootVerified: true`.
3. Download the updated `.ots` and run `ots info`. It should now show a
   `BitcoinBlockHeaderAttestation` line, not only `PendingAttestation`.
4. If a Bitcoin attestation appears but `anchorMerkleRootVerified` is `false`,
   treat it as a real bug (merkle-root byte order or block fetch), not a pass.
   Note: the service compares the attestation's leaf message to the Esplora
   `merkle_root` after byte-reversing it (explorers display roots reversed).

## 5. Regression checks

- [ ] Credential lifecycle (draft, approve, suspend, reactivate, revoke) unaffected.
- [ ] Ledger integrity check still passes on tenants that have checkpoints.
- [ ] A tenant with zero ledger entries yields no checkpoint and no errors.
- [ ] After a backend restart in Development, `tenant_<slug>.ledger_checkpoints`
      exists (tenant-schema migrations auto-apply; platform-schema migrations
      do **not** and need `dotnet ef database update --context PlatformDbContext`).
- [ ] Public verify page still renders and prints correctly (signature panel is `print:hidden`).

## 6. Out of scope / known gaps

- No frontend UI for anchor status yet.
- `OpRipemd160` parses but its `Apply()` throws (not observed from real calendars);
  `OpReverse`/`OpHexlify`/`OpSha1` are implemented.
- Litecoin attestations are parsed but never acted on.
- No signing-key rotation; one active key per tenant, created lazily.
- Canonicalization is not RFC 8785 JCS (unchanged from before this work).
