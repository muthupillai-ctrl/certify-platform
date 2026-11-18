using System.Text;
using System.Text.Json;
using Certyfi.Domain.Entities;

namespace Certyfi.Infrastructure.Signing;

// The signed summary carried in a certificate's QR code, so it can be
// checked with no internet connection. The QR link is
//   https://<host>/verify/<slug>/<id>#o=<payload>.<signature>
// and the part after '#' is never sent to any server: scanning online opens
// the normal verify page; offline, the installed verify page checks the
// signature against the institution's key it cached earlier.
//
// payload   = base64url(JSON) with short keys to keep the QR small:
//             c code, n recipient name, a achievement title,
//             d issued (yyyyMMdd, UTC), e expires (optional), k key id
// signature = base64url(ES256, raw r||s) over the ASCII bytes of
//             "<slug>/<id>#<payload>" — binding the summary to the
//             institution and credential in the link itself, so neither has
//             to be repeated inside the payload.
//
// Deliberately excludes the recipient's email (privacy) and status (it can
// change after printing — offline checks say "valid as issued", never
// "not revoked").
public class OfflineProofSigner
{
    private readonly ITenantSigningKeyProvider _keys;

    public OfflineProofSigner(ITenantSigningKeyProvider keys)
    {
        _keys = keys;
    }

    public async Task<string> CreateAsync(Guid tenantId, string tenantSlug, Credential credential, string achievementTitle, string keyId, CancellationToken ct)
    {
        var summary = new Dictionary<string, string>
        {
            ["c"] = credential.VerificationCode,
            ["n"] = credential.RecipientName,
            ["a"] = achievementTitle,
            ["d"] = (credential.IssuedAt ?? credential.CreatedAt).UtcDateTime.ToString("yyyyMMdd"),
        };
        if (credential.ExpiresOn is { } expires) summary["e"] = expires.UtcDateTime.ToString("yyyyMMdd");
        // "o": the paper certificate's own date, for a digitised one.
        if (credential.OriginalIssueDate is { } original) summary["o"] = original.ToString("yyyyMMdd");
        summary["k"] = keyId;

        var payload = Jose.Base64Url(JsonSerializer.Serialize(summary));
        var signingInput = Encoding.ASCII.GetBytes(SigningInput(tenantSlug, credential.Id, payload));
        var der = await _keys.SignAsync(tenantId, keyId, signingInput, ct);
        return $"{payload}.{Jose.Base64Url(Jose.DerToP1363(der))}";
    }

    public static string SigningInput(string tenantSlug, Guid credentialId, string payloadSegment) =>
        $"{tenantSlug}/{credentialId}#{payloadSegment}";
}
