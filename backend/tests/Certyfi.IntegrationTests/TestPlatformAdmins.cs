using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Certyfi.IntegrationTests;

// A saved platform admin and a token for it. Platform tokens are checked
// against the account on every request (active, session version), so the
// account must exist.
public static class TestPlatformAdmins
{
    public static string CreateWithToken(IServiceProvider services, string email, string fullName)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var admin = new PlatformAdmin { Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword("Platform-Test-Pass-1"), FullName = fullName };
        db.PlatformAdmins.Add(admin);
        db.SaveChanges();
        return scope.ServiceProvider.GetRequiredService<IJwtTokenService>().IssuePlatformAdminToken(admin);
    }
}
