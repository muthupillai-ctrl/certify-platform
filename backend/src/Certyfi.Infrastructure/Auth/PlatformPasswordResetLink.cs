using System.Security.Cryptography;
using System.Text;
using Certyfi.Infrastructure.Signing;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Auth;

// "Forgot password" link for a platform account:
// "<admin id>.<session version>.<expiry>.<sig>", signed with a key derived
// from the platform master key. Nothing is stored. Single-use: setting the
// new password bumps SessionVersion, which also ends the link (as does any
// other password change, reset or deactivation). Valid for one hour.
public class PlatformPasswordResetLink
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly byte[] _key;

    public PlatformPasswordResetLink(IOptions<KmsOptions> kms)
    {
        var master = Convert.FromBase64String(kms.Value.LocalMasterKey);
        _key = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("certyfi-platform-reset-v1"));
    }

    public record Target(Guid AdminId, int SessionVersion, DateTimeOffset ExpiresAt);

    public string Issue(Guid adminId, int sessionVersion, DateTimeOffset now)
    {
        var payload = $"{adminId:N}.{sessionVersion}.{(now + Lifetime).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    public Target? Read(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 4 }) return null;
        var payload = string.Join('.', parts[..3]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[3]))) return null;
        if (!Guid.TryParseExact(parts[0], "N", out var id) || !int.TryParse(parts[1], out var version) || !long.TryParse(parts[2], out var exp)) return null;
        return new Target(id, version, DateTimeOffset.FromUnixTimeSeconds(exp));
    }

    private string Sign(string payload) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();
}
