using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Certyfi.Infrastructure.Services;

// Known Gap #2 (README): there is deliberately no public API endpoint that
// creates a PlatformAdmin — platform operator accounts must never be
// reachable through a route a tenant or attacker could hit. This seeder is
// the only way to create the first one. Called from Program.cs, Development
// environment only, and it's a no-op the moment any PlatformAdmin already
// exists — it bootstraps the platform once and then gets out of the way.
public static class SuperAdminSeeder
{
    public static async Task SeedAsync(PlatformDbContext db, IConfiguration configuration, CancellationToken ct = default)
    {
        if (await db.PlatformAdmins.AnyAsync(ct))
        {
            return;
        }

        var email = configuration["SuperAdminSeed:Email"];
        var password = configuration["SuperAdminSeed:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            // No seed credentials configured (see README "Local setup") —
            // skip rather than seeding a default/guessable account.
            return;
        }

        db.PlatformAdmins.Add(new PlatformAdmin
        {
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FullName = "Super Admin",
            IsActive = true
        });
        await db.SaveChangesAsync(ct);
    }
}
