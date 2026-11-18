namespace Certyfi.Infrastructure.Signing;

// Bound from configuration (appsettings / user-secrets / env vars) — same
// "real secrets never in appsettings.json, only user-secrets/env" rule
// JwtOptions.SigningKey already follows (see README "Secrets"). LocalMasterKey
// is a base64-encoded 32-byte AES-256 key that encrypts every tenant's
// private signing key at rest — it is NOT a signing key itself, and it
// never leaves this process. Only meaningful for LocalTenantSigningKeyProvider;
// a real cloud KMS provider wouldn't need this at all (the cloud provider
// holds the key material, never this app).
public class KmsOptions
{
    public string LocalMasterKey { get; set; } = default!;
}
