using System.Security.Cryptography;
using System.Text;

namespace Certyfi.Infrastructure.Auth;

// Partner API keys look like "cfy_<prefix>_<secret>":
//   prefix — 12 hex chars, stored in clear (ApiClient.KeyPrefix) to find the
//            row, and safe to show in the admin UI to tell keys apart.
//   secret — 32 random bytes, base64url.
// Only SHA-256 of the WHOLE key is stored. A fast hash is correct here, not a
// password hash like BCrypt: the key has 256 bits of randomness, so there is
// nothing to brute-force, and every Partner API request verifies one.
public static class ApiKeyGenerator
{
    public const string KeyPrefixMarker = "cfy_";

    public static (string plaintextKey, string keyPrefix, string secretHash) Generate()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var key = $"{KeyPrefixMarker}{prefix}_{secret}";
        return (key, prefix, Hash(key));
    }

    // Returns false for anything not shaped like a Certyfi key, so a
    // malformed header fails fast without a database lookup.
    public static bool TryParsePrefix(string presentedKey, out string keyPrefix)
    {
        keyPrefix = string.Empty;
        if (!presentedKey.StartsWith(KeyPrefixMarker, StringComparison.Ordinal)) return false;
        var rest = presentedKey[KeyPrefixMarker.Length..];
        var separator = rest.IndexOf('_');
        if (separator != 12) return false;
        keyPrefix = rest[..separator];
        return rest.Length > separator + 1;
    }

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static bool Matches(string presentedKey, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(presentedKey)),
            Encoding.ASCII.GetBytes(storedHash));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
