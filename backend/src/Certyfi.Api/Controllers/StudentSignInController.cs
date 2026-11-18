using Certyfi.Api.Auth;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Student sign-in without an institution code: the email finds the
// institutions where it's a student (StudentSignInIndex), and the password
// is checked at each. Nothing is said about an email until its password
// has matched, so neither endpoint shows whether an email is a student
// anywhere. Not tenant-scoped (exempt in TenantResolutionMiddleware).
[ApiController]
[Route("api/v1/student-sign-in")]
public class StudentSignInController : ControllerBase
{
    // Compared against when no institution has the email, so the answer
    // takes about as long either way.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("certyfi-no-such-student");

    private readonly StudentSignInIndex _index;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;

    public StudentSignInController(StudentSignInIndex index, IServiceScopeFactory scopes, IConfiguration configuration)
    {
        _index = index;
        _scopes = scopes;
        _configuration = configuration;
    }

    [HttpPost]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult<StudentSignInResponse>> SignIn(StudentLoginRequest request, CancellationToken ct)
    {
        var email = StudentDirectory.Normalize(request.Email);
        var password = request.Password ?? "";
        var tenants = email is null ? [] : await _index.InstitutionsForAsync(email, ct);
        if (tenants.Count == 0) BCrypt.Net.BCrypt.Verify(password, DummyHash);

        var matches = new List<(Tenant Tenant, Student Student)>();
        foreach (var tenant in tenants)
        {
            using var scope = Scope(tenant);
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Email == email, ct);
            if (student?.PasswordHash is not null && BCrypt.Net.BCrypt.Verify(password, student.PasswordHash))
            {
                matches.Add((tenant, student));
            }
        }

        switch (matches.Count)
        {
            case 0:
                return Unauthorized(new { error = "Invalid email or password." });
            case 1:
            {
                var (tenant, student) = matches[0];
                using var scope = Scope(tenant);
                var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                await db.Students.Where(s => s.Id == student.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastLoginAt, DateTimeOffset.UtcNow), ct);
                var token = scope.ServiceProvider.GetRequiredService<IJwtTokenService>().IssueStudentToken(student, tenant.Id, tenant.Slug, tenant.SchemaName);
                var minutes = _configuration.GetValue("Jwt:AccessTokenMinutes", 30);
                var profile = new StudentProfileResponse(student.Id, student.FullName, student.Email, student.StudentNumber, tenant.Name, tenant.Slug);
                return Ok(new StudentSignInResponse(new StudentLoginResponse(token, DateTime.UtcNow.AddMinutes(minutes), profile), null));
            }
            default:
                // The page asks which one, then signs in there
                // (student-portal/login with that institution).
                return Ok(new StudentSignInResponse(null, matches
                    .Select(m => new StudentPortalInstitution(m.Tenant.Name, m.Tenant.Slug, m.Tenant.BrandingLogoUrl, m.Tenant.BrandingPrimaryColor))
                    .ToList()));
        }
    }

    // Emails a reset link (or the set-up link, if never set up) from each
    // institution where the email is a student. Always 202.
    [HttpPost("forgot-password")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult> ForgotPassword(StudentForgotPasswordRequest request, CancellationToken ct)
    {
        var email = StudentDirectory.Normalize(request.Email);
        if (email is null) return Accepted();
        foreach (var tenant in await _index.InstitutionsForAsync(email, ct))
        {
            using var scope = Scope(tenant);
            var student = await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Students.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Email == email, ct);
            if (student is not null) await scope.ServiceProvider.GetRequiredService<StudentNotifications>().PasswordResetAsync(student, ct);
        }
        return Accepted();
    }

    private IServiceScope Scope(Tenant tenant)
    {
        var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug, tenant.SchemaName);
        return scope;
    }
}
