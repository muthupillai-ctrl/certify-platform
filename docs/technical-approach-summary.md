# Technical Approach — Condensed Reference

Full detail lives in the "Ahamsys Credentialing Capability Note" document.
This file exists so the reasoning behind scaffold decisions is checkable
from inside the repo, without needing the Word doc open.

## Decisions this scaffold implements

- **Clean codebase**, not built on the existing Ahamsys LMS. Core object
  (a signed, long-lived assertion) differs too much from a course-centric
  LMS data model to share cleanly. See capability note section 4.1.
- **Schema-per-tenant** multi-tenancy in PostgreSQL — hard isolation at the
  database level, not just an application-level tenant_id filter.
- **Trust-Ledger as a separate service** — the original plan, called over an
  API rather than merged into this codebase, to sell it per-vertical. As
  built, signing and anchoring live in-process behind interfaces
  (`ITenantSigningKeyProvider`, `ILedgerCheckpointService`), so they can be
  extracted into their own service later without changing callers.
- **.NET 10 / ASP.NET Core Web API + Angular + PostgreSQL (Aiven)** — the
  standing Ahamsys stack preference, not chosen fresh for this project.

## Trust layer as built

- **Per-tenant signing.** ECDSA P-256; each tenant gets its own key,
  generated lazily. Private keys are AES-256-GCM-encrypted at rest with a
  master key from configuration (`Kms:LocalMasterKey`, never in
  appsettings.json). Signatures are DER-encoded (RFC 3279) so OpenSSL, Node,
  Java and Python all verify them; .NET's default raw format is not
  interoperable. Public keys are served anonymously, and the verify page
  checks signatures in the browser with the Web Crypto API.
- **Ledger checkpoint anchoring.** Chosen over full on-chain issuance
  (Blockcerts-style precedent). A background service periodically hashes each
  tenant's ledger tip and submits it to public OpenTimestamps calendar
  servers; the proof is upgraded until a Bitcoin attestation appears. The
  OpenTimestamps binary format is parsed natively in C#, ported from the
  reference implementation, so the app can read status, produce downloadable
  `.ots` proofs, and check the attested Bitcoin block's merkle root against a
  public block explorer. The proof format was cross-checked with the official
  `ots` CLI.
- **Storage.** `tenant_signing_keys` lives in the platform schema (key
  material is a platform secret-management concern, like a shared KMS
  account); `ledger_checkpoints` lives in each tenant schema beside
  `ledger_entries`.
- **Operations.** Platform-schema migrations are applied manually
  (`dotnet ef database update --context PlatformDbContext`); tenant-schema
  migrations auto-apply in Development. SuperAdmins can trigger a checkpoint
  or confirmation check on demand from the Platform Admin "Anchoring" page.
- **Verification practice.** Crypto and binary-format work is checked with
  independent tools (OpenSSL, Node, the `ots` CLI), not only same-language
  tests. That approach caught the signature-format bug above.

See `test-plan-signing-anchoring.md` for how to verify all of this.

## Not yet built

Badge rules engine, a standalone revocation registry, legacy migration, DID/
wallet support, selective disclosure, a real cloud KMS provider behind
`ITenantSigningKeyProvider`, signing-key rotation, RFC 8785 canonicalization,
and a listing of checkpoints in the Platform Admin UI. The core credential
data model, issuance and maker-checker engine, public verification service,
signing and Bitcoin anchoring are built.
