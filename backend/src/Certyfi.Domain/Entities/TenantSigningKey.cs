namespace Certyfi.Domain.Entities;

// Lives in the PLATFORM schema, not a tenant schema — key material is a
// platform-level secret-management concern even though it's logically
// per-tenant, same reasoning as why Tenant itself lives here; a real cloud
// KMS provider would also be one shared account with per-tenant CMKs, not
// per-tenant infrastructure. See ITenantSigningKeyProvider for the
// abstraction this backs — LocalTenantSigningKeyProvider is the only
// implementation today (EncryptedPrivateKey is only ever populated/used by
// that local provider); a real KMS-backed provider would instead store a
// key reference (ARN/resource ID) here and never touch
// EncryptedPrivateKey at all, with zero change to callers.
public class TenantSigningKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    // Opaque identifier, unique per tenant — "v1" for the first key, v2… after a retirement. Not a
    // sequence number: a real KMS provider's key ids are provider-assigned
    // strings (an ARN, a Key Vault key version), so this stays a plain
    // string rather than an int.
    public string KeyId { get; set; } = default!;

    // "ES256" today (ECDSA P-256 + SHA-256) — the one algorithm
    // LocalTenantSigningKeyProvider issues. Stored per-key (not assumed
    // globally) so a future provider/algorithm change doesn't invalidate
    // how already-issued signatures are verified.
    public string Algorithm { get; set; } = "ES256";

    // SubjectPublicKeyInfo PEM — safe to expose publicly (see
    // VerificationController's signing-keys endpoint). This is what makes a
    // credential's signature independently verifiable: anyone can fetch
    // this and check a signature themselves, without trusting Certyfi's own
    // verify response.
    public string PublicKeyPem { get; set; } = default!;

    // AES-256-GCM encrypted PKCS8 private key, base64 — LOCAL provider
    // only. Never returned by any API; only ITenantSigningKeyProvider.SignAsync
    // ever decrypts it, in memory, for the duration of one signing
    // operation.
    public string? EncryptedPrivateKey { get; set; }

    // One active key per tenant for now — see the plan's "Known gaps" on
    // why rotation (multiple concurrently-valid keys) isn't built yet.
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
