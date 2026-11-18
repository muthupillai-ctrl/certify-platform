using System.Security.Cryptography;
using System.Text;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Auth;

// Links that let a student choose a password in the student portal:
// "<slug>.<purpose>.<student id>.<sign-in version>.<expiry>.<sig>", signed
// with a key derived from the platform master key (like HomeAccessPass), so
// nothing is stored.
//   - Welcome ("w", 30 days): first set-up; only while the student has no
//     password. Each email can carry a fresh one; older ones keep working
//     until the password is set.
//   - Reset ("r", 1 hour): forgot password; setting the new password bumps
//     SignInVersion, which ends this and every other outstanding link.
// Staff "reset sign-in" also bumps SignInVersion.
public class StudentPortalLink
{
    public static readonly TimeSpan WelcomeLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public enum Purpose { Welcome, Reset }

    private readonly byte[] _key;
    private readonly IConfiguration _configuration;

    public StudentPortalLink(IOptions<KmsOptions> kms, IConfiguration configuration)
    {
        var master = Convert.FromBase64String(kms.Value.LocalMasterKey);
        _key = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("certyfi-student-portal-v1"));
        _configuration = configuration;
    }

    public record Target(string TenantSlug, Purpose Purpose, Guid StudentId, int SignInVersion, DateTimeOffset ExpiresAt);

    // PublicApp:BaseUrl, without a trailing slash; null if not configured.
    public string? BaseUrl => _configuration["PublicApp:BaseUrl"]?.TrimEnd('/') is { Length: > 0 } url ? url : null;

    // Where to send the student: set-up while they have no password, else
    // the institution's sign-in page.
    public string PortalUrl(string baseUrl, string tenantSlug, Student student, DateTimeOffset now) =>
        student.PasswordHash is null
            ? LinkUrl(baseUrl, Issue(tenantSlug, student, Purpose.Welcome, now))
            : SignInUrl(baseUrl, tenantSlug);

    public static string SignInUrl(string baseUrl, string tenantSlug) => $"{baseUrl}/student/{tenantSlug}/login";

    public static string LinkUrl(string baseUrl, string token) => $"{baseUrl}/student/welcome/{token}";

    public string Issue(string tenantSlug, Student student, Purpose purpose, DateTimeOffset now)
    {
        var lifetime = purpose == Purpose.Welcome ? WelcomeLifetime : ResetLifetime;
        var payload = $"{tenantSlug}.{(purpose == Purpose.Welcome ? "w" : "r")}.{student.Id:N}.{student.SignInVersion}.{(now + lifetime).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    // The target if genuine (expired or not; the caller checks); else null.
    public Target? Read(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 6 }) return null;
        var payload = string.Join('.', parts[..5]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[5]))) return null;
        Purpose? purpose = parts[1] switch { "w" => Purpose.Welcome, "r" => Purpose.Reset, _ => null };
        if (purpose is null || !Guid.TryParseExact(parts[2], "N", out var studentId) || !int.TryParse(parts[3], out var version)
            || !long.TryParse(parts[4], out var expires)) return null;
        return new Target(parts[0], purpose.Value, studentId, version, DateTimeOffset.FromUnixTimeSeconds(expires));
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();
}
