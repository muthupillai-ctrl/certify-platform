using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Certyfi.Infrastructure.Signing;
using Microsoft.Extensions.Options;

namespace Certyfi.Api.Auth;

// The private home page's pass: a cookie holding the allowed number's id and
// an expiry, signed with a key derived from the platform master key (so no
// new secret to configure). The reverse proxy asks GET /api/v1/access before
// serving the home page and the presentation file (see
// deploy/nginx/certyfi.conf); the app asks too, for in-app navigation.
public class HomeAccessPass
{
    public const string CookieName = "certyfi_home";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly byte[] _key;

    public HomeAccessPass(IOptions<KmsOptions> kms)
    {
        var master = Convert.FromBase64String(kms.Value.LocalMasterKey);
        _key = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("certyfi-home-access-v1"));
    }

    public string Issue(Guid numberId, DateTimeOffset now)
    {
        var payload = $"{numberId:N}.{(now + Lifetime).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    // The number's id if the pass is genuine and unexpired.
    public Guid? Read(string? pass, DateTimeOffset now)
    {
        var parts = pass?.Split('.');
        if (parts is not { Length: 3 }) return null;
        var payload = $"{parts[0]}.{parts[1]}";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[2]))) return null;
        if (!long.TryParse(parts[1], out var expires) || DateTimeOffset.FromUnixTimeSeconds(expires) < now) return null;
        return Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();

    // "+234 (0)706-289-1305" → digits; null unless 10–16 digits and nothing
    // but digits, spaces, "+", "-", "(", ")" and ".".
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (input.Any(c => !char.IsDigit(c) && " +-().".IndexOf(c) < 0)) return null;
        var digits = new string(input.Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 16 ? digits : null;
    }
}

// Mobile-number attempts: a fixed window per client IP, so numbers on the
// list can't be found by trying many quickly. Same client-IP rule as
// VerificationLookupRateLimit.
public static class HomeAccessRateLimit
{
    public const string PolicyName = "home-access";

    public static IServiceCollection AddHomeAccessRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var permitPerMinute = configuration.GetValue("HomeAccess:AttemptsPerMinute", 10);
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, httpContext =>
            {
                var ip = httpContext.Request.Headers["CF-Connecting-IP"].ToString();
                if (string.IsNullOrWhiteSpace(ip)) ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"home:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }
}
