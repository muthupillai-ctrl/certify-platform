using System.Security.Cryptography;
using System.Text;
using Certyfi.Domain.Entities;

namespace Certyfi.Infrastructure.Services;

// Salted fingerprints of a certificate's personal data (HMAC-SHA256 keyed
// with Credential.PersonalDataSalt). Unlike a plain hash, a fingerprint can't
// be confirmed by guessing a name or email, and once the salt is erased it
// can't be linked to anyone at all.
public static class PersonalDataFingerprint
{
    public const string Prefix = "hmac-sha256:";

    // Creates the credential's salt on first use (certificates made before
    // salts existed get one the first time they need it).
    public static string EnsureSalt(Credential credential) =>
        credential.PersonalDataSalt ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Of(string salt, string personalDataJson) =>
        Prefix + Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(salt), Encoding.UTF8.GetBytes(personalDataJson))).ToLowerInvariant();
}
