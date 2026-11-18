using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Certyfi.Domain.Entities;

namespace Certyfi.Infrastructure.Signing;

// Builds a recipient's portable credential: an Open Badges 3.0
// OpenBadgeCredential (1EdTech), secured as a VC-JWT (W3C VC Data Model
// 1.1 JWT encoding, which OB 3.0 specifies) and signed ES256 with the
// institution's existing key — so any JWT library, OB 3.0 verifier or
// wallet can check it against the key at `kid`.
//
// Shape follows the OB 3.0 specification (AchievementCredential with an
// AchievementSubject). It has not been run through 1EdTech's conformance
// suite yet. Not included: credentialStatus (1EdTech revocation list);
// current status is checked at the credential's `id`, which is its public
// verification page.
//
// The recipient's email is included only as a salted SHA-256 hash (OB 3.0
// IdentityObject, hashed=true), so the file doesn't expose it but the
// recipient can still prove it's theirs. Built fresh on each download (ECDSA
// signatures are randomized, so files differ byte-for-byte; all verify).
public class OpenBadgeCredentialIssuer
{
    private readonly ITenantSigningKeyProvider _keys;

    public OpenBadgeCredentialIssuer(ITenantSigningKeyProvider keys)
    {
        _keys = keys;
    }

    public static string VerificationPageUrl(string baseUrl, string tenantSlug, Guid credentialId) =>
        $"{baseUrl}/verify/{tenantSlug}/{credentialId}";

    public static string IssuerProfileUrl(string baseUrl, string tenantSlug) => $"{baseUrl}/api/v1/verify/{tenantSlug}/issuer";

    public static string KeyUrl(string baseUrl, string tenantSlug, string keyId) => $"{baseUrl}/api/v1/verify/{tenantSlug}/jwks/{keyId}";

    // apiBaseUrl: where the issuer profile and keys are served; siteBaseUrl:
    // where the verify page is. Same host in production.
    public async Task<string> IssueAsync(string apiBaseUrl, string siteBaseUrl, Tenant tenant, Credential credential, CancellationToken ct)
    {
        var keyId = credential.SignatureKeyId
            ?? throw new InvalidOperationException("Only a signed (approved) credential can be exported.");
        var achievement = credential.Achievement;
        var issuedAt = credential.IssuedAt ?? credential.CreatedAt;
        var credentialUrl = VerificationPageUrl(siteBaseUrl, tenant.Slug, credential.Id);
        var issuerUrl = IssuerProfileUrl(apiBaseUrl, tenant.Slug);

        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var emailHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.RecipientEmail + salt))).ToLowerInvariant();

        var vc = new Dictionary<string, object?>
        {
            ["@context"] = new[]
            {
                "https://www.w3.org/2018/credentials/v1",
                "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json",
            },
            ["id"] = credentialUrl,
            ["type"] = new[] { "VerifiableCredential", "OpenBadgeCredential" },
            ["name"] = achievement.Title,
            ["issuer"] = new Dictionary<string, object?>
            {
                ["id"] = issuerUrl,
                ["type"] = new[] { "Profile" },
                ["name"] = tenant.Name,
                ["url"] = tenant.WebsiteUrl,
                ["description"] = tenant.About,
                ["image"] = tenant.BrandingLogoUrl is { } logo ? new Dictionary<string, object> { ["id"] = logo, ["type"] = "Image" } : null,
            },
            ["issuanceDate"] = Iso(issuedAt),
            // Open Badges 3.0 awardedDate: when it was really awarded (the
            // paper certificate's date), as opposed to issued in this form.
            ["awardedDate"] = credential.OriginalIssueDate is { } awarded ? awarded.ToString("yyyy-MM-dd") + "T00:00:00Z" : null,
            ["expirationDate"] = credential.ExpiresOn is { } expires ? Iso(expires) : null,
            ["credentialSubject"] = new Dictionary<string, object?>
            {
                ["type"] = new[] { "AchievementSubject" },
                // Digitised paper certificates may have no email on record.
                ["identifier"] = string.IsNullOrEmpty(credential.RecipientEmail)
                    ? new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "IdentityObject", ["identityType"] = "name", ["hashed"] = false, ["identityHash"] = credential.RecipientName,
                        },
                    }
                    : new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "IdentityObject", ["identityType"] = "name", ["hashed"] = false, ["identityHash"] = credential.RecipientName,
                        },
                        new Dictionary<string, object>
                        {
                            ["type"] = "IdentityObject", ["identityType"] = "emailAddress", ["hashed"] = true,
                            ["identityHash"] = $"sha256${emailHash}", ["salt"] = salt,
                        },
                    },
                ["achievement"] = new Dictionary<string, object?>
                {
                    ["id"] = $"urn:uuid:{achievement.Id}",
                    ["type"] = new[] { "Achievement" },
                    ["name"] = achievement.Title,
                    ["description"] = string.IsNullOrWhiteSpace(achievement.Description) ? achievement.Title : achievement.Description,
                    ["criteria"] = new Dictionary<string, object>
                    {
                        ["narrative"] = string.IsNullOrWhiteSpace(achievement.CriteriaNarrative)
                            ? $"Awarded by {tenant.Name}."
                            : achievement.CriteriaNarrative,
                    },
                },
            },
        };

        var claims = new Dictionary<string, object?>
        {
            ["iss"] = issuerUrl,
            ["jti"] = credentialUrl,
            ["nbf"] = issuedAt.ToUnixTimeSeconds(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = credential.ExpiresOn?.ToUnixTimeSeconds(),
            ["vc"] = vc,
        };

        var header = new Dictionary<string, string>
        {
            ["alg"] = "ES256",
            ["typ"] = "JWT",
            ["kid"] = KeyUrl(apiBaseUrl, tenant.Slug, keyId),
        };

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        var signingInput = $"{Jose.Base64Url(JsonSerializer.Serialize(header))}.{Jose.Base64Url(JsonSerializer.Serialize(claims, options))}";
        var der = await _keys.SignAsync(tenant.Id, keyId, Encoding.ASCII.GetBytes(signingInput), ct);
        return $"{signingInput}.{Jose.Base64Url(Jose.DerToP1363(der))}";
    }

    public static Dictionary<string, object?> IssuerProfile(string apiBaseUrl, string siteBaseUrl, Tenant tenant) => new()
    {
        ["@context"] = new[]
        {
            "https://www.w3.org/2018/credentials/v1",
            "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json",
        },
        ["id"] = IssuerProfileUrl(apiBaseUrl, tenant.Slug),
        ["type"] = new[] { "Profile" },
        ["name"] = tenant.Name,
        ["url"] = tenant.WebsiteUrl ?? siteBaseUrl,
        ["description"] = tenant.About,
        ["image"] = tenant.BrandingLogoUrl,
        // OB 3.0 IdentifierEntry: the institution's registration number, if given.
        ["identifier"] = tenant.RegistrationNumber is { } reg
            ? new[] { new Dictionary<string, string> { ["type"] = "IdentifierEntry", ["identifier"] = reg, ["identifierType"] = "identifier" } }
            : null,
    };

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
