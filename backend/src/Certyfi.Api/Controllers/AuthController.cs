using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped login. Requires a resolved tenant (subdomain or
// X-Tenant-Slug header) — TenantResolutionMiddleware has already run and
// rejected the request with 400/404 before this action executes if not.
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IJwtTokenService _jwt;

    public AuthController(TenantDbContext db, ITenantContext tenantContext, IJwtTokenService jwt)
    {
        _db = db;
        _tenantContext = tenantContext;
        _jwt = jwt;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(TenantLoginRequest request)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == request.Email.Trim().ToLowerInvariant());

        if (user is null || !user.IsActive || user.IsServiceAccount || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            // Deliberately identical response for "no such user" and "wrong
            // password" — don't let login responses leak which is which.
            // Service accounts (Partner API keys) never log in.
            return Unauthorized(new { error = "Invalid email or password." });
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var token = _jwt.IssueTenantUserToken(user, _tenantContext.TenantId, _tenantContext.TenantSlug, _tenantContext.SchemaName, roles, user.MustChangePassword);

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new LoginResponse(token, DateTime.UtcNow.AddMinutes(30), user.MustChangePassword));
    }

    // Any signed-in staff user changing their own password — including
    // someone signed in with a temporary password after an admin reset
    // (the only request that token is allowed to make). Returns a fresh
    // token without the change-required marker.
    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<LoginResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId) || User.FindFirstValue("tenant_id") is null) return Unauthorized();

        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive && !u.IsServiceAccount, ct);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword ?? "", user.PasswordHash))
        {
            return BadRequest(new { error = "Current password is incorrect." });
        }
        if (PasswordPolicy.Validate(request.NewPassword, user.Email) is { } policyError)
        {
            return BadRequest(new { error = policyError });
        }
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.PasswordHash))
        {
            return BadRequest(new { error = "Choose a password different from the current one." });
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var token = _jwt.IssueTenantUserToken(user, _tenantContext.TenantId, _tenantContext.TenantSlug, _tenantContext.SchemaName, roles);
        return Ok(new LoginResponse(token, DateTime.UtcNow.AddMinutes(30)));
    }
}
