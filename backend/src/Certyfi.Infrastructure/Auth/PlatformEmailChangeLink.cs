using System.Security.Cryptography;
using System.Text;
using Certyfi.Infrastructure.Signing;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Auth;

// The link emailed to a platform admin's NEW address to confirm an email
// change: "<admin id>.<new email>.<old email check>.<expiry>.<sig>", signed
// with a key derived from the platform master key. Nothing is stored; the
// old-email check makes it single-use (once the email has changed, it no
// longer matches) and it expires after 24 hours.
public class PlatformEmailChangeLink
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly byte[] _key;

    public PlatformEmailChangeLink(IOptions<KmsOptions> kms)
    {
        var master = Convert.FromBase64String(kms.Value.LocalMasterKey);
        _key = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("certyfi-platform-email-v1"));
    }

    public record Target(Guid AdminId, string NewEmail, string OldEmailCheck, DateTimeOffset ExpiresAt);

    public string Issue(Guid adminId, string currentEmail, string newEmail, DateTimeOffset now)
    {
        var payload = $"{adminId:N}.{B64(Encoding.UTF8.GetBytes(newEmail))}.{Check(currentEmail)}.{(now + Lifetime).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    public Target? Read(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 5 }) return null;
        var payload = string.Join('.', parts[..4]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[4]))) return null;
        if (!Guid.TryParseExact(parts[0], "N", out var id) || !long.TryParse(parts[3], out var exp)) return null;
        string email;
        try
        {
            email = Encoding.UTF8.GetString(FromB64(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
        return new Target(id, email, parts[2], DateTimeOffset.FromUnixTimeSeconds(exp));
    }

    // Short fingerprint of the current email: the link only works while the
    // account still has the email it was issued for.
    public string Check(string email) => B64(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(email))[..12]);

    private string Sign(string payload) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
