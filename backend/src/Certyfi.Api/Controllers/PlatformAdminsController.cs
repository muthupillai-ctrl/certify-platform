using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Platform users: other platform admin accounts. New ones and resets get a
// temporary password (shown once) to change at first sign-in. You can't
// deactivate or reset yourself, and there's always at least one active
// account. Deactivating or resetting ends that account's sessions at once.
[ApiController]
[Route("api/v1/platform/admins")]
[Authorize(Roles = SystemRole.SuperAdmin)]
public class PlatformAdminsController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformAdminsController(PlatformDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlatformAdminResponse>>> List(CancellationToken ct)
    {
        var me = CurrentId();
        var admins = await _db.PlatformAdmins.AsNoTracking().OrderBy(a => a.FullName).ToListAsync(ct);
        return Ok(admins.Select(a => ToResponse(a, me)));
    }

    [HttpPost]
    public async Task<ActionResult<PlatformAdminWithPasswordResponse>> Create(CreatePlatformAdminRequest request, CancellationToken ct)
    {
        var me = await Me(ct);
        if (me is null) return Unauthorized();
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var name = request.FullName?.Trim() ?? "";
        if (!PlatformAdminHistory.IsEmail(email)) return BadRequest(new { error = "Enter a valid email address." });
        if (name.Length is 0 or > 200) return BadRequest(new { error = "Enter their name (up to 200 characters)." });
        if (await _db.PlatformAdmins.AnyAsync(a => a.Email == email, ct)) return Conflict(new { error = "A platform account with this email already exists." });

        var temporary = PasswordPolicy.GenerateTemporary();
        var admin = new PlatformAdmin
        {
            Email = email, FullName = name, PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporary),
            MustChangePassword = true, CreatedByAdminId = me.Id,
        };
        _db.PlatformAdmins.Add(admin);
        PlatformAdminHistory.Record(_db, admin, me, "Created", null, email);
        await _db.SaveChangesAsync(ct);
        return Ok(new PlatformAdminWithPasswordResponse(ToResponse(admin, me.Id), temporary));
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<PlatformAdminWithPasswordResponse>> ResetPassword(Guid id, CancellationToken ct)
    {
        var me = await Me(ct);
        if (me is null) return Unauthorized();
        if (id == me.Id) return BadRequest(new { error = "Change your own password on your profile page." });
        var admin = await _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (admin is null) return NotFound();

        var temporary = PasswordPolicy.GenerateTemporary();
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporary);
        admin.MustChangePassword = true;
        admin.SessionVersion++;
        PlatformAdminHistory.Record(_db, admin, me, "Password reset", null, null);
        await _db.SaveChangesAsync(ct);
        return Ok(new PlatformAdminWithPasswordResponse(ToResponse(admin, me.Id), temporary));
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<ActionResult<PlatformAdminResponse>> Deactivate(Guid id, CancellationToken ct)
    {
        var me = await Me(ct);
        if (me is null) return Unauthorized();
        if (id == me.Id) return BadRequest(new { error = "You can't deactivate your own account." });
        var admin = await _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (admin is null) return NotFound();
        if (!admin.IsActive) return Ok(ToResponse(admin, me.Id));
        if (await _db.PlatformAdmins.CountAsync(a => a.IsActive, ct) <= 1)
        {
            return Conflict(new { error = "There must always be at least one active platform account." });
        }
        admin.IsActive = false;
        admin.SessionVersion++;
        PlatformAdminHistory.Record(_db, admin, me, "Deactivated", null, null);
        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(admin, me.Id));
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<ActionResult<PlatformAdminResponse>> Reactivate(Guid id, CancellationToken ct)
    {
        var me = await Me(ct);
        if (me is null) return Unauthorized();
        var admin = await _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (admin is null) return NotFound();
        if (!admin.IsActive)
        {
            admin.IsActive = true;
            PlatformAdminHistory.Record(_db, admin, me, "Reactivated", null, null);
            await _db.SaveChangesAsync(ct);
        }
        return Ok(ToResponse(admin, me.Id));
    }

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<PlatformAccountChangeResponse>>> History(Guid id, CancellationToken ct) =>
        Ok(await PlatformAdminHistory.ForAsync(_db, id, ct));

    private Guid CurrentId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    private Task<PlatformAdmin?> Me(CancellationToken ct)
    {
        var id = CurrentId();
        return _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == id && a.IsActive, ct);
    }

    private static PlatformAdminResponse ToResponse(PlatformAdmin a, Guid me) =>
        new(a.Id, a.Email, a.FullName, a.IsActive, a.MustChangePassword, a.CreatedAt, a.LastLoginAt, a.Id == me);
}
