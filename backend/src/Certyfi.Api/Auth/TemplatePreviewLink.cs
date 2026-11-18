using System.Security.Cryptography;
using System.Text;
using Certyfi.Infrastructure.Signing;
using Microsoft.Extensions.Options;

namespace Certyfi.Api.Auth;

// A short-lived link a partner app opens (new tab or iframe) to show what a
// certificate template looks like, drawn with sample data by Certyfi's own
// preview. The token names the institution and template and an expiry, and
// is signed with a key derived from the platform master key (same approach
// as HomeAccessPass, so no new secret to configure). It grants nothing but a
// look at that one template's design: no personal data, no credentials.
public class TemplatePreviewLink
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly byte[] _key;

    public TemplatePreviewLink(IOptions<KmsOptions> kms)
    {
        var master = Convert.FromBase64String(kms.Value.LocalMasterKey);
        _key = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("certyfi-template-preview-v1"));
    }

    public record Target(string TenantSlug, Guid TemplateId, DateTimeOffset ExpiresAt);

    // "<slug>.<template id>.<expiry unix seconds>.<signature>"; slugs never
    // contain dots (TenantProvisioningService.SlugPattern).
    public (string Token, DateTimeOffset ExpiresAt) Issue(string tenantSlug, Guid templateId, DateTimeOffset now)
    {
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds((now + Lifetime).ToUnixTimeSeconds());
        var payload = $"{tenantSlug}.{templateId:N}.{expiresAt.ToUnixTimeSeconds()}";
        return ($"{payload}.{Sign(payload)}", expiresAt);
    }

    // The target if the token is genuine, whether or not it has expired
    // (the caller tells an expired link apart from a bad one); null if it
    // was not issued by us.
    public Target? Read(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 4 }) return null;
        var payload = $"{parts[0]}.{parts[1]}.{parts[2]}";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[3]))) return null;
        if (!long.TryParse(parts[2], out var expires) || !Guid.TryParseExact(parts[1], "N", out var templateId)) return null;
        return new Target(parts[0], templateId, DateTimeOffset.FromUnixTimeSeconds(expires));
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();
}
