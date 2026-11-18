using Certyfi.Api.Auth;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Microsoft.AspNetCore.RateLimiting;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// NOT tenant-scoped — exempted in TenantResolutionMiddleware (path prefix
// /api/v1/platform). Separate table (PlatformAdmin), separate token shape
// with no tenant claim. See Auth/JwtTokenService.IssuePlatformAdminToken.
[ApiController]
[Route("api/v1/platform/auth")]
public class PlatformAuthController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly PlatformPasswordResetLink _resetLinks;
    private readonly EmailOutbox _outbox;
    private readonly IConfiguration _configuration;

    public PlatformAuthController(PlatformDbContext db, IJwtTokenService jwt, PlatformPasswordResetLink resetLinks, EmailOutbox outbox, IConfiguration configuration)
    {
        _db = db;
        _jwt = jwt;
        _resetLinks = resetLinks;
        _outbox = outbox;
        _configuration = configuration;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(PlatformLoginRequest request)
    {
        var admin = await _db.PlatformAdmins
            .FirstOrDefaultAsync(a => a.Email == request.Email.Trim().ToLowerInvariant());

        if (admin is null || !admin.IsActive || !BCrypt.Net.BCrypt.Verify(request.Password, admin.PasswordHash))
        {
            return Unauthorized(new { error = "Invalid email or password." });
        }

        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        var token = _jwt.IssuePlatformAdminToken(admin);
        return Ok(new LoginResponse(token, DateTime.UtcNow.AddMinutes(30), admin.MustChangePassword));
    }

    // Emails a one-hour reset link to an active account. Always 202, so it
    // can't be used to find out which emails have platform accounts.
    [HttpPost("forgot-password")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<IActionResult> ForgotPassword(PlatformForgotPasswordRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var admin = await _db.PlatformAdmins.AsNoTracking().FirstOrDefaultAsync(a => a.Email == email && a.IsActive, ct);
        var baseUrl = _configuration["PublicApp:BaseUrl"]?.TrimEnd('/');
        if (admin is not null && !string.IsNullOrEmpty(baseUrl))
        {
            var token = _resetLinks.Issue(admin.Id, admin.SessionVersion, DateTimeOffset.UtcNow);
            await _outbox.QueueAsync(EmailTemplates.PlatformPasswordReset, admin.Email, admin.FullName,
                EmailTemplates.PlatformPasswordResetLink(admin.FullName, $"{baseUrl}/platform/reset-password/{token}"), null, null, null, ct);
        }
        return Accepted();
    }

    // From the emailed link: sets the new password, ends every session (and
    // this link), and emails a notice. Then the admin signs in as usual.
    [HttpPost("reset-password")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<IActionResult> ResetPassword(PlatformResetPasswordRequest request, CancellationToken ct)
    {
        var target = _resetLinks.Read(request.Token);
        if (target is null) return NotFound(new { error = "This link isn't valid." });
        if (target.ExpiresAt < DateTimeOffset.UtcNow) return StatusCode(StatusCodes.Status410Gone, new { error = "This link has expired. Ask for a new one." });
        var admin = await _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == target.AdminId && a.IsActive, ct);
        if (admin is null || admin.SessionVersion != target.SessionVersion)
        {
            return NotFound(new { error = "This link has already been used or isn't valid any more. Ask for a new one." });
        }
        if (PasswordPolicy.Validate(request.NewPassword, admin.Email) is { } policyError) return BadRequest(new { error = policyError });
        if (request.NewPassword.Length < PlatformAccountController.MinPasswordLength)
        {
            return BadRequest(new { error = $"Use at least {PlatformAccountController.MinPasswordLength} characters." });
        }

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        admin.PasswordChangedAt = DateTimeOffset.UtcNow;
        admin.MustChangePassword = false;
        admin.SessionVersion++;
        PlatformAdminHistory.Record(_db, admin, admin, "Password reset by email", null, null);
        await _db.SaveChangesAsync(ct);
        await _outbox.QueueAsync(EmailTemplates.PlatformPasswordChanged, admin.Email, admin.FullName,
            EmailTemplates.PlatformPasswordChangedNotice(admin.FullName), null, null, null, ct);
        return NoContent();
    }
}
