using System.Security.Cryptography;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Signing;

// The only ITenantSigningKeyProvider implementation today — generates a
// real ECDSA P-256 keypair per tenant on first use, encrypts the private
// key at rest with AES-256-GCM under a single master key from config, and
// never returns private key material to any caller. A real cloud KMS
// provider (AWS KMS / Azure Key Vault) would implement the same interface
// and hold key material itself instead of EncryptedPrivateKey — swapping
// providers later is a DI registration change, not a rewrite.
public class LocalTenantSigningKeyProvider : ITenantSigningKeyProvider
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const string Algorithm = "ES256";

    private readonly PlatformDbContext _platformDb;
    private readonly byte[] _masterKey;

    public LocalTenantSigningKeyProvider(PlatformDbContext platformDb, IOptions<KmsOptions> kmsOptions)
    {
        _platformDb = platformDb;
        var keyBase64 = kmsOptions.Value.LocalMasterKey;
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            throw new InvalidOperationException("Kms:LocalMasterKey is not configured. See README 'Local setup'.");
        }
        _masterKey = Convert.FromBase64String(keyBase64);
        if (_masterKey.Length != 32)
        {
            throw new InvalidOperationException("Kms:LocalMasterKey must decode to exactly 32 bytes (AES-256).");
        }
    }

    public async Task<TenantSigningKeyInfo> GetOrCreateActiveKeyAsync(Guid tenantId, CancellationToken ct)
    {
        var existing = await _platformDb.TenantSigningKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.IsActive)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new TenantSigningKeyInfo(existing.KeyId, existing.Algorithm);
        }

        // "v1" for a tenant's first key, then v2, v3... when an earlier one
        // was retired (IsActive false). Every certificate records the KeyId
        // that signed it, and old keys keep their public key, so certificates
        // signed by a retired key still verify.
        var existingKeys = await _platformDb.TenantSigningKeys.AsNoTracking().CountAsync(k => k.TenantId == tenantId, ct);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new TenantSigningKey
        {
            TenantId = tenantId,
            KeyId = $"v{existingKeys + 1}",
            Algorithm = Algorithm,
            PublicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem(),
            EncryptedPrivateKey = Encrypt(ecdsa.ExportPkcs8PrivateKey()),
            IsActive = true,
        };

        _platformDb.TenantSigningKeys.Add(key);
        try
        {
            await _platformDb.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _platformDb.Entry(key).State = EntityState.Detached;

            // Lost a race with a concurrent first-approval for the same
            // tenant — someone else's insert won, ours violated the unique
            // (TenantId, KeyId) index. Their key is just as valid as ours
            // would have been; use it. Anything else (a genuinely different
            // failure) should still surface as a real error.
            var winner = await _platformDb.TenantSigningKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.KeyId == key.KeyId && k.IsActive, ct);
            if (winner is null) throw;
            return new TenantSigningKeyInfo(winner.KeyId, winner.Algorithm);
        }

        return new TenantSigningKeyInfo(key.KeyId, key.Algorithm);
    }

    public async Task<byte[]> SignAsync(Guid tenantId, string keyId, byte[] data, CancellationToken ct)
    {
        var key = await _platformDb.TenantSigningKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.KeyId == keyId, ct);
        if (key?.EncryptedPrivateKey is null)
        {
            throw new InvalidOperationException($"No local signing key '{keyId}' found for tenant '{tenantId}'.");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Decrypt(key.EncryptedPrivateKey), out _);
        // DSASignatureFormat.Rfc3279DerSequence, explicitly — .NET's own
        // default (IeeeP1363, raw r||s bytes) is NOT what OpenSSL, Node,
        // Java, or Python's crypto libraries expect from a PEM ECDSA public
        // key by default; they expect DER-encoded ECDSA-Sig-Value. Signing
        // in .NET's own default format would make every ".NET-internal"
        // test pass while genuinely independent verification (the entire
        // point of a real signature) silently fails — confirmed by hand
        // against Node's crypto module before fixing this.
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public async Task<string?> GetPublicKeyPemAsync(Guid tenantId, string keyId, CancellationToken ct)
    {
        return await _platformDb.TenantSigningKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.KeyId == keyId)
            .Select(k => k.PublicKeyPem)
            .FirstOrDefaultAsync(ct);
    }

    private string Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        using (var aes = new AesGcm(_masterKey, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var combined = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSizeBytes + ciphertext.Length, TagSizeBytes);
        return Convert.ToBase64String(combined);
    }

    private byte[] Decrypt(string encryptedBase64)
    {
        var combined = Convert.FromBase64String(encryptedBase64);
        var nonce = combined[..NonceSizeBytes];
        var tag = combined[^TagSizeBytes..];
        var ciphertext = combined[NonceSizeBytes..^TagSizeBytes];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_masterKey, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
