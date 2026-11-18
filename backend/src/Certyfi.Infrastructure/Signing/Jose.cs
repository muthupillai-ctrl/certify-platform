using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace Certyfi.Infrastructure.Signing;

// Small JOSE helpers for the formats verifiers check with standard tools:
// the Open Badges credential (a JWT, RFC 7519) and the offline QR proof.
// ITenantSigningKeyProvider returns DER signatures (RFC 3279, what OpenSSL
// expects — see LocalTenantSigningKeyProvider); JWS ES256 and the browser's
// Web Crypto both need the raw 64-byte r||s form (IEEE P1363) instead.
public static class Jose
{
    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Base64Url(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

    public static byte[] DerToP1363(byte[] der, int coordinateSize = 32)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadIntegerBytes().ToArray();
        var s = sequence.ReadIntegerBytes().ToArray();
        sequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();

        var result = new byte[coordinateSize * 2];
        CopyUnsigned(r, result.AsSpan(0, coordinateSize));
        CopyUnsigned(s, result.AsSpan(coordinateSize, coordinateSize));
        return result;
    }

    // A P-256 public key PEM as a JWK (RFC 7517) — what `kid` in a signed
    // credential resolves to, and what the verify page caches for offline use.
    public static Dictionary<string, string> PublicJwk(string publicKeyPem, string keyId)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        return new Dictionary<string, string>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url(parameters.Q.X!),
            ["y"] = Base64Url(parameters.Q.Y!),
            ["kid"] = keyId,
            ["alg"] = "ES256",
            ["use"] = "sig",
        };
    }

    // ASN.1 INTEGERs are big-endian two's complement: strip a leading 0x00
    // sign byte, left-pad short values with zeros.
    private static void CopyUnsigned(byte[] value, Span<byte> destination)
    {
        var trimmed = value.AsSpan();
        while (trimmed.Length > destination.Length && trimmed[0] == 0) trimmed = trimmed[1..];
        if (trimmed.Length > destination.Length) throw new CryptographicException("Signature component is too long for P-256.");
        destination.Clear();
        trimmed.CopyTo(destination[(destination.Length - trimmed.Length)..]);
    }
}
