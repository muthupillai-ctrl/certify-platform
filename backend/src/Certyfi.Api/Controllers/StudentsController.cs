using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Staff view of the institution's students (see Student): the list, one
// student with their certificates, and a sign-in link to give a student
// (optionally resetting their password, for a student who forgot it —
// Certyfi doesn't send email).
[ApiController]
[Route("api/v1/students")]
[Authorize(Roles = $"{SystemRole.InstitutionAdmin},{SystemRole.Registrar}")]
public class StudentsController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly StudentPortalLink _links;
    private readonly StudentNotifications _notifications;
    private readonly StudentDirectory _directory;

    public StudentsController(TenantDbContext db, ITenantContext tenantContext, StudentPortalLink links, StudentNotifications notifications,
        StudentDirectory directory)
    {
        _directory = directory;
        _db = db;
        _tenantContext = tenantContext;
        _links = links;
        _notifications = notifications;
    }

    [HttpGet]
    public async Task<ActionResult<StudentListResponse>> List(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);

        var query = _db.Students.AsNoTracking();
        var term = q?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // Contains, case-insensitive, over name, email and student id.
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(s =>
                EF.Functions.ILike(s.FullName, pattern, "\\") ||
                EF.Functions.ILike(s.Email, pattern, "\\") ||
                (s.StudentNumber != null && EF.Functions.ILike(s.StudentNumber, pattern, "\\")));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(s => s.FullName).ThenBy(s => s.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new StudentListItemResponse(
                s.Id, s.StudentNumber, s.FullName, s.Email, s.Source.ToString(), s.CreatedAt,
                s.PasswordHash != null, s.LastLoginAt, s.Credentials.Count))
            .ToListAsync(ct);
        return Ok(new StudentListResponse(items, total));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StudentDetailResponse>> Get(Guid id, CancellationToken ct)
    {
        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new StudentListItemResponse(
                s.Id, s.StudentNumber, s.FullName, s.Email, s.Source.ToString(), s.CreatedAt,
                s.PasswordHash != null, s.LastLoginAt, s.Credentials.Count))
            .FirstOrDefaultAsync(ct);
        if (student is null) return NotFound(new { error = "Student not found." });

        var certificates = await _db.Credentials.AsNoTracking()
            .Where(c => c.StudentId == id)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Id, c.Achievement.Title, c.Status, c.ExpiresOn, c.CreatedAt, c.IssuedAt, c.PaidAt })
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var phone = await _db.Students.AsNoTracking().Where(s => s.Id == id).Select(s => s.Phone).FirstAsync(ct);
        var changes = await _db.StudentChanges.AsNoTracking()
            .Where(c => c.StudentId == id)
            .OrderByDescending(c => c.ChangedAt).Take(50)
            .Select(c => new StudentChangeResponse(c.ChangedAt, c.ChangedByName, c.Field, c.OldValue, c.NewValue))
            .ToListAsync(ct);
        return Ok(new StudentDetailResponse(student, certificates.Select(c => new StudentCertificateSummary(
            c.Id, c.Title,
            c.Status == CredentialStatus.Active && c.ExpiresOn is { } exp && exp < now ? "Expired" : c.Status.ToString(),
            c.CreatedAt, c.IssuedAt, c.PaidAt)).ToList(), phone, changes));
    }

    // Edit name, email, phone or student ID. The email is the student's
    // sign-in, so they use the new one (same password) from now on; the old
    // address is told about the change and the new one gets the sign-in (or
    // set-up) link. Certificates already issued keep the details they were
    // issued with.
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<StudentDetailResponse>> Update(Guid id, UpdateStudentRequest request, CancellationToken ct)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { error = "Student not found." });

        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);
        var actorName = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "Staff";
        var result = await _directory.UpdateAsync(student,
            new StudentDirectory.StudentEdit(request.FullName, request.Email, request.Phone, request.StudentNumber), userId, actorName, ct);
        if (result.Error is not null) return BadRequest(new { error = result.Error });
        if (result.Conflict is not null) return Conflict(new { error = result.Conflict });
        if (result.OldEmail is not null)
        {
            await _notifications.EmailChangedAsync(student, result.OldEmail, sendLinkToNewAddress: true, ct);
        }
        return await Get(id, ct);
    }

    // The link to give the student. Reset forgets their password and makes
    // every earlier link stop working, then returns a fresh welcome link.
    [HttpPost("{id:guid}/sign-in-link")]
    public async Task<ActionResult<StudentSignInLinkResponse>> SignInLink(Guid id, StudentSignInLinkRequest request, CancellationToken ct)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { error = "Student not found." });

        if (request.Reset)
        {
            student.PasswordHash = null;
            student.PasswordSetAt = null;
            student.SignInVersion++;
            await _db.SaveChangesAsync(ct);
        }
        var url = _links.PortalUrl(_links.BaseUrl ?? $"{Request.Scheme}://{Request.Host}", _tenantContext.TenantSlug, student, DateTimeOffset.UtcNow);
        return Ok(new StudentSignInLinkResponse(url, student.PasswordHash is null));
    }

    // ---------- Bulk invitations ----------
    public const int RecentInviteDays = 7;
    // The sender paces Resend at about 2 a second; this is for the estimate.
    private const int EmailsPerMinute = 100;

    [HttpGet("invitations/preview")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<StudentInvitationPreview>> InvitationPreview(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-RecentInviteDays);
        var notSignedUp = await _db.Students.CountAsync(s => s.PasswordHash == null, ct);
        var recent = await _db.Students.CountAsync(s => s.PasswordHash == null && s.LastInvitedAt >= since, ct);
        var toInvite = notSignedUp - recent;
        return Ok(new StudentInvitationPreview(notSignedUp, recent, toInvite, RecentInviteDays, Minutes(toInvite)));
    }

    // Emails a set-up link to every student who hasn't signed up, except
    // anyone sent one in the last RecentInviteDays (so repeating it, or a
    // double click, doesn't email people twice).
    [HttpPost("invitations")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<StudentInvitationResult>> InviteAll(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-RecentInviteDays);
        var students = await _db.Students.AsNoTracking()
            .Where(s => s.PasswordHash == null && (s.LastInvitedAt == null || s.LastInvitedAt < since))
            .ToListAsync(ct);
        var skipped = await _db.Students.CountAsync(s => s.PasswordHash == null && s.LastInvitedAt >= since, ct);
        var queued = await _notifications.InviteManyAsync(students, ct);
        return Ok(new StudentInvitationResult(queued, skipped, Minutes(queued)));
    }

    private static int Minutes(int emails) => emails == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(emails / (double)EmailsPerMinute));

    // Emails the student their set-up link (or, if they've set a password,
    // the sign-in page). Queued: false when there's no PublicApp:BaseUrl.
    [HttpPost("{id:guid}/email-link")]
    public async Task<ActionResult<StudentEmailLinkResponse>> EmailLink(Guid id, CancellationToken ct)
    {
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound(new { error = "Student not found." });
        return Ok(new StudentEmailLinkResponse(await _notifications.SignInLinkAsync(student, ct)));
    }
}
