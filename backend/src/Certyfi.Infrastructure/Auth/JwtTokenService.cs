using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Certyfi.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Certyfi.Infrastructure.Auth;

public interface IJwtTokenService
{
    string IssueTenantUserToken(ApplicationUser user, Guid tenantId, string tenantSlug, string tenantSchema, IEnumerable<string> roles, bool passwordChangeRequired = false);
    string IssuePlatformAdminToken(PlatformAdmin admin);
    string IssueStudentToken(Student student, Guid tenantId, string tenantSlug, string tenantSchema);
}

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    // Tenant claims (tenant_id / tenant_schema) travel in every tenant-user
    // token. TenantResolutionMiddleware still independently resolves the
    // tenant from the request (header/subdomain) — the token is checked
    // AGAINST that resolution in the JWT validation pipeline (Program.cs),
    // so a token for tenant A can't be replayed against tenant B's subdomain.
    // passwordChangeRequired adds the claim PasswordChangeRequiredMiddleware
    // enforces: the token then works only for choosing a new password.
    public const string PasswordChangeClaim = "pwd_change";
    // Platform tokens: the account's SessionVersion when issued; a token
    // with an older one is refused (PlatformAdmin.SessionVersion).
    public const string SessionVersionClaim = "sv";

    public string IssueTenantUserToken(ApplicationUser user, Guid tenantId, string tenantSlug, string tenantSchema, IEnumerable<string> roles, bool passwordChangeRequired = false)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("tenant_id", tenantId.ToString()),
            new("tenant_slug", tenantSlug),
            new("tenant_schema", tenantSchema),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (passwordChangeRequired) claims.Add(new Claim(PasswordChangeClaim, "required"));

        return WriteToken(claims);
    }

    // A student-portal sign-in: tenant-bound like a staff token (so
    // TenantClaimGuardMiddleware applies), but its only role is Student,
    // which no staff endpoint accepts.
    public string IssueStudentToken(Student student, Guid tenantId, string tenantSlug, string tenantSchema)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, student.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, student.Email),
            new("tenant_id", tenantId.ToString()),
            new("tenant_slug", tenantSlug),
            new("tenant_schema", tenantSchema),
            new(ClaimTypes.Role, Domain.Enums.SystemRole.Student),
        };

        return WriteToken(claims);
    }

    public string IssuePlatformAdminToken(PlatformAdmin admin)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new(ClaimTypes.Role, Domain.Enums.SystemRole.SuperAdmin),
            // Deliberately NO tenant_id/tenant_schema claim — a platform
            // admin token must never satisfy a tenant-scoped authorization
            // check. See technical note 4.6, separation of duties.
            new(SessionVersionClaim, admin.SessionVersion.ToString()),
        };
        if (admin.MustChangePassword) claims.Add(new Claim(PasswordChangeClaim, "required"));

        return WriteToken(claims);
    }

    private string WriteToken(List<Claim> claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
