# Certyfi — Brochure Content

Client-facing overview of the platform, kept here so it can be edited directly in
the repo as the product changes. This is the same content published as a
two-page designed PDF; when this file changes meaningfully, regenerate that PDF
from it rather than letting the two drift apart.

## Tagline

Certificates people can trust — verified in seconds, not phone calls.

## What Certyfi is

Certyfi is a digital credentialing platform built for institutions: design your
own certificates, route every issuance through an approval step, and give each
recipient a credential that anyone, anywhere, can verify instantly online.

## How it works

1. **Design** — Build a certificate on a drag-and-drop canvas, or import a
   professionally designed background and place your fields on top.
2. **Issue** — Pick a template, enter recipient details, and submit; the draft
   is queued for approval.
3. **Approve** — A second, different staff member reviews and approves. No one
   can approve their own request.
4. **Verify** — Every certificate carries a QR code and a public link. Anyone
   can confirm it's genuine in seconds, no login required.

## What's included

- **Visual certificate designer** — a real drag-and-drop canvas for every
  field, or import a background someone else designed and place your data on
  top.
- **Maker–checker workflow** — every certificate is drafted by one person and
  approved by another, enforced by the system, not just policy.
- **Tamper-evident ledger** — a hash-chained audit trail records every action,
  so a certificate's history can never be silently altered.
- **Cryptographic signatures** — every approved certificate is signed with
  its institution's own key; anyone can check the signature in their own
  browser, without trusting Certyfi.
- **Bitcoin timestamping** — the ledger is periodically anchored to the
  Bitcoin blockchain, so even Certyfi itself can't quietly rewrite history.
- **Instant public verification** — every certificate carries a QR code and a
  public link; anyone can confirm it's genuine in seconds.
- **Role-based access** — Institution Admins, Registrars, Approvers, and
  Learners each see exactly what their role needs, nothing more.
- **Multi-tenant by design** — each institution's data lives in its own
  isolated workspace, under its own branding.

## Security & trust

**Verification checks the record, not the document.** A tampered PDF or image
is easy to spot the moment someone checks it — Certyfi never asks a verifier
to trust the file in front of them, only what the platform itself reports
back. Three layers:

1. **The document isn't the proof** — every certificate resolves back to its
   real record by credential ID. Editing a downloaded PDF or image changes
   nothing; verification always checks the source of record, not the file.
   *(Live today.)*
2. **A tamper-evident history** — every action (issued, approved, suspended,
   revoked) is recorded in a hash-chained ledger. Altering any past entry
   breaks every entry that follows it, so history can't be quietly edited.
   *(Live today.)*
3. **Cryptographic signing** — each institution has its own signing key, and
   every approved certificate is signed with it. The verify page includes a
   "Verify in this browser" check that validates the signature locally using
   the institution's public key, so authenticity doesn't depend on trusting
   Certyfi's own answer. *(Live today.)*
4. **Bitcoin anchoring** — the ledger's latest state is periodically
   timestamped to the Bitcoin blockchain via the open OpenTimestamps
   protocol. Once confirmed (typically within hours), rewriting history would
   mean rewriting Bitcoin. Each certificate offers a downloadable proof
   that can be verified with standard open-source tools, with no reliance on
   Certyfi. *(Live today.)*

## Verification endpoint

Every issued credential is verifiable both as a human-readable page and as a
raw API call — useful if a client's own systems (e.g. an HR platform) want to
check a certificate programmatically instead of opening the page.

- **Public verify page** (human-facing, renders the certificate):
  `{origin}/verify/{tenantSlug}/{credentialId}`
- **Public verify API** (machine-facing, JSON, anonymous — no login or API key
  required): `GET {apiOrigin}/api/v1/verify/{tenantSlug}/{credentialId}`
  Includes the signature, signing-key id and Bitcoin anchor status
  (`NotYetCheckpointed` / `Pending` / `Confirmed`, plus block height once
  confirmed).
- **Institution public key**: `GET {apiOrigin}/api/v1/verify/{tenantSlug}/signing-keys/{keyId}`
  — for checking a signature independently of Certyfi.
- **Bitcoin proof download**: `GET {apiOrigin}/api/v1/verify/{tenantSlug}/{credentialId}/anchor-proof`
  — an `.ots` file, verifiable with the standard OpenTimestamps tools.

Example against the local dev environment:

```
GET http://localhost:5080/api/v1/verify/acme-university/4e251765-8321-4273-aac2-e48ee1d6d1a7
```

No production domain is configured yet — `{origin}` / `{apiOrigin}` above
become the real deployed hostnames once that's decided. This is currently the
only endpoint in the API that's genuinely public/anonymous; everything else
requires a logged-in session (see the answer on external APIs from this same
conversation if revisiting this later).

## Future roadmap (not yet built)

Internal planning notes — designed or discussed, not shipped. Don't lift this
section into client-facing material without reframing it as "coming soon"
and confirming each item is still the plan; some are open design questions,
not commitments.

**Trust & cryptographic verification**

Shipped since this section was first written: per-tenant ECDSA P-256 signing
(private keys AES-GCM-encrypted at rest, behind an `ITenantSigningKeyProvider`
interface) and checkpoint anchoring of the ledger to Bitcoin via
OpenTimestamps. What remains:

- **Real cloud KMS provider.** Signing keys are currently generated and held
  by a local provider, encrypted with a master key from configuration. The
  interface exists so an AWS KMS / Azure Key Vault / HashiCorp Vault
  implementation can be swapped in with no caller changes, so private keys
  never leave the KMS. Not yet built.
- **Key rotation.** One active signing key per tenant today. Rotation needs
  retired keys kept so old signatures still verify.
- **RFC 8785 canonicalization.** The signed payload is a fixed-shape JSON
  hash, not standards-based JCS canonicalization.
- **Full on-chain issuance.** Deliberately not chosen: anchoring periodic
  checkpoints (as shipped) gives the tamper-evidence benefit at a tiny
  fraction of the cost and without putting personal data on a public chain.
- **Trust-Ledger as its own service.** Signing and anchoring currently live
  inside this codebase behind interfaces; splitting them into a separate API
  (to sell/version per-vertical) remains a future option.

**Platform & integration**

- **Partner/developer API.** Today only the verify endpoint above is
  externally callable; issuing programmatically (not just verifying) would
  need API keys or OAuth client-credentials, scoped permissions, rate
  limiting, and real docs. A `Developer` role already exists in the role
  enum as a placeholder for this but isn't wired to anything yet.
- **Cloud object storage for uploaded images.** Background images uploaded
  via the template designer currently land on the API server's local disk
  (`UploadsController`) — fine for one instance, but won't survive a
  redeploy or work behind a load balancer with multiple instances. Moving to
  S3/Blob-style storage removes that limitation.
- **Refresh tokens.** Access tokens are short-lived with no refresh flow —
  acceptable for now, not for a production rollout.
- **Rate limiting.** Nothing yet on `/api/v1/auth/login` or the public verify
  endpoint, both natural abuse targets.

**Credentialing depth** (from the original capability note, not yet started)

- Badge rules engine (auto-award based on defined criteria)
- A formal revocation registry (today revoke is a per-credential status
  change with a ledger entry, not a standalone registry/service)
- DID / digital-wallet support
- Selective disclosure (recipient shares only some fields of a credential)
- Legacy-system data migration tooling

## Contact

info@ahamsys.com · www.ahamsys.com

## Designed PDF

Published as a two-page Design Artifact: https://claude.ai/artifact/N7sABQ3kxJtSjEvTpLA1pV
(private until shared from its own Share menu). Re-export the PDF from there
after updating this file, or rebuild the artifact from this content.
