using System.Threading.RateLimiting;

namespace Certyfi.Api.Auth;

// Public certificate-code look-ups: a fixed window per client IP. Behind
// Cloudflare every request reaches the API from the same proxy chain, so
// the client IP comes from Cloudflare's CF-Connecting-IP header (set by
// Cloudflare on every request; the API container has no public port, so it
// can't be sent from anywhere else). Locally it falls back to the socket IP.
public static class VerificationLookupRateLimit
{
    public const string PolicyName = "verify-lookup";

    public static IServiceCollection AddVerificationLookupRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var permitPerMinute = configuration.GetValue("Verification:LookupsPerMinute", 20);
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, httpContext =>
            {
                var ip = httpContext.Request.Headers["CF-Connecting-IP"].ToString();
                if (string.IsNullOrWhiteSpace(ip)) ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"lookup:{ip}", _ => new FixedWindowRateLimiterOptions
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
