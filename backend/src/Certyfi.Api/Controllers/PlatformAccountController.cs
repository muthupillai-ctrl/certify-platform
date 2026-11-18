using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// The signed-in platform admin's own profile: name, email (confirmed by a
// link to the new address, old address told), password (current password
// required; ends every other session; emailed notice). Each change is kept
// in PlatformAdminChange.
[ApiController]
[Route("api/v1/platform/account")]
[Authorize(Roles = SystemRole.SuperAdmin)]
public class PlatformAccountController : ControllerBase
{
    public const int MinPasswordLength = 12;

    private readonly PlatformDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly PlatformEmailChangeLink _emailLinks;
    private readonly EmailOutbox _outbox;
    private readonly IConfiguration _configuration;

    public PlatformAccountController(PlatformDbContext db, IJwtTokenService jwt, PlatformEmailChangeLink emailLinks, EmailOutbox outbox, IConfiguration configuration)
    {
        _db = db;
        _jwt = jwt;
        _emailLinks = emailLinks;
        _outbox = outbox;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<ActionResult<PlatformAccountResponse>> Get(CancellationToken ct)
    {
        var admin = await Current(ct);
        return admin is null ? Unauthorized() : Ok(await Response(admin, ct));
    }

    [HttpPut("name")]
    public async Task<ActionResult<PlatformAccountResponse>> UpdateName(UpdatePlatformNameRequest request, CancellationToken ct)
    {
        var admin = await Current(ct);
        if (admin is null) return Unauthorized();
        var name = request.FullName?.Trim() ?? "";
        if (name.Length is 0 or > 200) return BadRequest(new { error = "Enter your name (up to 200 characters)." });
        if (name != admin.FullName)
        {
            PlatformAdminHistory.Record(_db, admin, admin, "Name", admin.FullName, name);
            admin.FullName = name;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(await Response(admin, ct));
    }

    // Also how a temporary password is replaced at first sign-in. Ends every
    // other session; this one gets a fresh token.
    [HttpPost("change-password")]
    public async Task<ActionResult<LoginResponse>> ChangePassword(PlatformChangePasswordRequest request, CancellationToken ct)
    {
        var admin = await Current(ct);
        if (admin is null) return Unauthorized();
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword ?? "", admin.PasswordHash))
        {
            return BadRequest(new { error = "Your current password is incorrect." });
        }
        if (PasswordPolicy.Validate(request.NewPassword, admin.Email) is { } policyError)
        {
            return BadRequest(new { error = policyError });
        }
        if (request.NewPassword.Length < MinPasswordLength)
        {
            return BadRequest(new { error = $"Use at least {MinPasswordLength} characters." });
        }
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, admin.PasswordHash))
        {
            return BadRequest(new { error = "Choose a password different from the current one." });
        }

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        admin.PasswordChangedAt = DateTimeOffset.UtcNow;
        admin.MustChangePassword = false;
        admin.SessionVersion++;
        PlatformAdminHistory.Record(_db, admin, admin, "Password", null, null);
        await _db.SaveChangesAsync(ct);

        await _outbox.QueueAsync(EmailTemplates.PlatformPasswordChanged, admin.Email, admin.FullName,
            EmailTemplates.PlatformPasswordChangedNotice(admin.FullName), null, null, null, ct);
        return Ok(new LoginResponse(_jwt.IssuePlatformAdminToken(admin), DateTime.UtcNow.AddMinutes(30)));
    }

    // Emails a confirmation link to the new address; nothing changes until
    // it's opened (ConfirmEmail).
    [HttpPost("change-email")]
    public async Task<ActionResult<PlatformChangeEmailResponse>> ChangeEmail(PlatformChangeEmailRequest request, CancellationToken ct)
    {
        var admin = await Current(ct);
        if (admin is null) return Unauthorized();
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword ?? "", admin.PasswordHash))
        {
            return BadRequest(new { error = "Your current password is incorrect." });
        }
        var email = request.NewEmail?.Trim().ToLowerInvariant() ?? "";
        if (!PlatformAdminHistory.IsEmail(email)) return BadRequest(new { error = "Enter a valid email address." });
        if (email == admin.Email) return BadRequest(new { error = "That's already your email." });
        if (await _db.PlatformAdmins.AnyAsync(a => a.Email == email, ct))
        {
            return Conflict(new { error = "Another platform account already uses this email." });
        }
        var baseUrl = _configuration["PublicApp:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Email links aren't set up on this server." });

        var token = _emailLinks.Issue(admin.Id, admin.Email, email, DateTimeOffset.UtcNow);
        await _outbox.QueueAsync(EmailTemplates.PlatformEmailConfirm, email, admin.FullName,
            EmailTemplates.PlatformEmailConfirmation(admin.FullName, $"{baseUrl}/platform/confirm-email/{token}"), null, null, null, ct);
        return Accepted(new PlatformChangeEmailResponse(email));
    }

    // Opened from the emailed link (signed in or not).
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [EnableRateLimiting(Auth.StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult<PlatformConfirmEmailResponse>> ConfirmEmail(PlatformConfirmEmailRequest request, CancellationToken ct)
    {
        var target = _emailLinks.Read(request.Token);
        if (target is null) return NotFound(new { error = "This link isn't valid." });
        if (target.ExpiresAt < DateTimeOffset.UtcNow) return StatusCode(StatusCodes.Status410Gone, new { error = "This link has expired. Ask for a new one from your profile." });
        var admin = await _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == target.AdminId && a.IsActive, ct);
        if (admin is null || _emailLinks.Check(admin.Email) != target.OldEmailCheck)
        {
            return NotFound(new { error = "This link has already been used or isn't valid any more." });
        }
        if (await _db.PlatformAdmins.AnyAsync(a => a.Email == target.NewEmail && a.Id != admin.Id, ct))
        {
            return Conflict(new { error = "Another platform account already uses this email." });
        }

        var oldEmail = admin.Email;
        PlatformAdminHistory.Record(_db, admin, admin, "Email", oldEmail, target.NewEmail);
        admin.Email = target.NewEmail;
        await _db.SaveChangesAsync(ct);
        await _outbox.QueueAsync(EmailTemplates.PlatformEmailChanged, oldEmail, admin.FullName,
            EmailTemplates.PlatformEmailChangedNotice(admin.FullName, EmailTemplates.MaskEmail(admin.Email)), null, null, null, ct);
        return Ok(new PlatformConfirmEmailResponse(admin.Email));
    }

    private Task<PlatformAdmin?> Current(CancellationToken ct)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id)
            ? _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == id && a.IsActive, ct)
            : Task.FromResult<PlatformAdmin?>(null);
    }

    private async Task<PlatformAccountResponse> Response(PlatformAdmin admin, CancellationToken ct) =>
        new(admin.Id, admin.Email, admin.FullName, admin.CreatedAt, admin.LastLoginAt, admin.PasswordChangedAt, admin.MustChangePassword,
            await PlatformAdminHistory.ForAsync(_db, admin.Id, ct));
}

// Shared by the profile and Platform users pages.
public static class PlatformAdminHistory
{
    public static void Record(PlatformDbContext db, PlatformAdmin subject, PlatformAdmin actor, string field, string? oldValue, string? newValue) =>
        db.PlatformAdminChanges.Add(new PlatformAdminChange
        {
            PlatformAdminId = subject.Id, ChangedByAdminId = actor.Id, ChangedByName = actor.FullName,
            Field = field, OldValue = oldValue, NewValue = newValue,
        });

    public static async Task<IReadOnlyList<PlatformAccountChangeResponse>> ForAsync(PlatformDbContext db, Guid adminId, CancellationToken ct) =>
        await db.PlatformAdminChanges.AsNoTracking()
            .Where(c => c.PlatformAdminId == adminId)
            .OrderByDescending(c => c.ChangedAt).Take(30)
            .Select(c => new PlatformAccountChangeResponse(c.ChangedAt, c.ChangedByName, c.Field, c.OldValue, c.NewValue, c.ChangedByAdminId))
            .ToListAsync(ct);

    public static bool IsEmail(string email)
    {
        var at = email.IndexOf('@');
        return email.Length <= 320 && at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1
            && !email.EndsWith('.') && !email.Any(char.IsWhiteSpace);
    }
}
