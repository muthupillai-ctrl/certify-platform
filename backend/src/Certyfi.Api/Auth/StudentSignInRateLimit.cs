using System.Threading.RateLimiting;

namespace Certyfi.Api.Auth;

// Student sign-in, set-up and forgot-password attempts: a fixed window per
// client IP, like HomeAccessRateLimit, so passwords can't be guessed (or
// reset emails sent) quickly.
public static class StudentSignInRateLimit
{
    public const string PolicyName = "student-sign-in";

    public static IServiceCollection AddStudentSignInRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var permitPerMinute = configuration.GetValue("StudentPortal:SignInAttemptsPerMinute", 10);
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, httpContext =>
            {
                var ip = httpContext.Request.Headers["CF-Connecting-IP"].ToString();
                if (string.IsNullOrWhiteSpace(ip)) ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter($"student:{ip}", _ => new FixedWindowRateLimiterOptions
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
