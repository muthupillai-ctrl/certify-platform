namespace Certyfi.Infrastructure.Signing;

// The "KMS interface" — deliberately shaped so a real cloud KMS (AWS KMS /
// Azure Key Vault) can be swapped in as a second implementation later with
// ZERO changes to callers (CredentialsController, VerificationController):
// same "local now, pluggable later" pattern already used for file uploads
// this session. Private key material never crosses this interface —
// SignAsync returns only signature bytes, GetPublicKeyPemAsync only the
// public half. See LocalTenantSigningKeyProvider for the only
// implementation today.
public interface ITenantSigningKeyProvider
{
    // Returns the tenant's current active key, creating one (lazily, on
    // first use) if none exists yet.
    Task<TenantSigningKeyInfo> GetOrCreateActiveKeyAsync(Guid tenantId, CancellationToken ct);

    // Signs data under the named key. keyId must be one this tenant
    // actually owns — see the implementation's own isolation check.
    Task<byte[]> SignAsync(Guid tenantId, string keyId, byte[] data, CancellationToken ct);

    // Null if the tenant has no key with that id — callers treat that as
    // "can't verify," never as "verified false."
    Task<string?> GetPublicKeyPemAsync(Guid tenantId, string keyId, CancellationToken ct);
}

public record TenantSigningKeyInfo(string KeyId, string Algorithm);
