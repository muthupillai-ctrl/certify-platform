using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Api.Auth;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// The student portal: a student sets a password from their portal link
// (StudentPortalLink), signs in with email + password, sees their own
// certificates and pays (dummy payment) for any awaiting payment. Tenant
// comes from X-Tenant-Slug like the staff app; tokens carry only the
// Student role, which no staff endpoint accepts.
[ApiController]
[Route("api/v1/student-portal")]
public class StudentPortalController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly PlatformDbContext _platformDb;
    private readonly ITenantContext _tenantContext;
    private readonly IJwtTokenService _jwt;
    private readonly StudentPortalLink _links;
    private readonly CredentialIssuanceService _issuance;
    private readonly IConfiguration _configuration;
    private readonly StudentNotifications _notifications;
    private readonly StudentSignInIndex _signInIndex;

    public StudentPortalController(
        TenantDbContext db, PlatformDbContext platformDb, ITenantContext tenantContext, IJwtTokenService jwt,
        StudentPortalLink links, CredentialIssuanceService issuance, IConfiguration configuration, StudentNotifications notifications,
        StudentSignInIndex signInIndex)
    {
        _signInIndex = signInIndex;
        _notifications = notifications;
        _db = db;
        _platformDb = platformDb;
        _tenantContext = tenantContext;
        _jwt = jwt;
        _links = links;
        _issuance = issuance;
        _configuration = configuration;
    }

    // ---------- public: the institution and its banners ----------

    // Name and branding for the student pages (no sign-in needed).
    [HttpGet("institution")]
    public async Task<ActionResult<StudentPortalInstitution>> Institution(CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking().FirstAsync(t => t.Id == _tenantContext.TenantId, ct);
        return Ok(new StudentPortalInstitution(tenant.Name, tenant.Slug, tenant.BrandingLogoUrl, tenant.BrandingPrimaryColor));
    }

    // Banners running today, in order (see PortalBanner).
    [HttpGet("banners")]
    public async Task<ActionResult<IEnumerable<StudentPortalBanner>>> Banners(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var banners = await _db.PortalBanners.AsNoTracking()
            .Where(b => b.IsActive && (b.StartsOn == null || b.StartsOn <= today) && (b.EndsOn == null || b.EndsOn >= today))
            .OrderBy(b => b.Side).ThenBy(b => b.SortOrder)
            .Select(b => new StudentPortalBanner(b.Id, b.Title, b.ImageUrl, b.LinkUrl, b.Side.ToString()))
            .ToListAsync(ct);
        return Ok(banners);
    }

    // Counts a click on a banner's link (no personal data recorded).
    [HttpPost("banners/{id:guid}/click")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<IActionResult> BannerClick(Guid id, CancellationToken ct)
    {
        await _db.PortalBanners.Where(b => b.Id == id && b.LinkUrl != null)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.ClickCount, b => b.ClickCount + 1), ct);
        return NoContent();
    }

    // ---------- signing in ----------

    // Who the welcome link is for, so the page can greet them before they
    // choose a password.
    [HttpGet("welcome/{token}")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult<StudentWelcomeResponse>> Welcome(string token, CancellationToken ct)
    {
        var (student, purpose, error) = await ReadLink(token, ct);
        if (student is null) return error!;
        var isReset = purpose == StudentPortalLink.Purpose.Reset;
        return Ok(new StudentWelcomeResponse(student.FullName, student.Email, await InstitutionName(ct), _tenantContext.TenantSlug,
            !isReset && student.PasswordHash is not null, isReset));
    }

    [HttpPost("setup")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult<StudentLoginResponse>> SetUp(StudentSetupRequest request, CancellationToken ct)
    {
        var (student, purpose, error) = await ReadLink(request.Token, ct);
        if (student is null) return error!;
        if (purpose == StudentPortalLink.Purpose.Welcome && student.PasswordHash is not null)
        {
            return Conflict(new { error = "You've already set your password. Sign in with your email and password." });
        }
        if (PasswordPolicy.Validate(request.Password, student.Email) is { } policyError)
        {
            return BadRequest(new { error = policyError });
        }

        var now = DateTimeOffset.UtcNow;
        student.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        student.PasswordSetAt = now;
        student.LastLoginAt = now;
        // A reset link works once: this ends it and any other reset links.
        if (purpose == StudentPortalLink.Purpose.Reset) student.SignInVersion++;
        await _db.SaveChangesAsync(ct);
        return Ok(await SignedIn(student, ct));
    }

    // Emails a reset link (or, never set up, the set-up link). Always 202,
    // so it can't be used to find out which emails are students here.
    [HttpPost("forgot-password")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult> ForgotPassword(StudentForgotPasswordRequest request, CancellationToken ct)
    {
        var email = StudentDirectory.Normalize(request.Email);
        var student = email is null ? null : await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Email == email, ct);
        if (student is not null) await _notifications.PasswordResetAsync(student, ct);
        return Accepted();
    }

    [HttpPost("login")]
    [EnableRateLimiting(StudentSignInRateLimit.PolicyName)]
    public async Task<ActionResult<StudentLoginResponse>> Login(StudentLoginRequest request, CancellationToken ct)
    {
        var email = StudentDirectory.Normalize(request.Email);
        var student = email is null ? null : await _db.Students.FirstOrDefaultAsync(s => s.Email == email, ct);
        if (student?.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(request.Password ?? "", student.PasswordHash))
        {
            // Same answer for unknown email, not set up yet and wrong password.
            return Unauthorized(new { error = "Invalid email or password." });
        }

        student.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(await SignedIn(student, ct));
    }

    [HttpPost("change-password")]
    [Authorize(Roles = SystemRole.Student)]
    public async Task<ActionResult> ChangePassword(StudentChangePasswordRequest request, CancellationToken ct)
    {
        var student = await CurrentStudent(ct);
        if (student?.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword ?? "", student.PasswordHash))
        {
            return BadRequest(new { error = "Current password is incorrect." });
        }
        if (PasswordPolicy.Validate(request.NewPassword, student.Email) is { } policyError)
        {
            return BadRequest(new { error = policyError });
        }

        student.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        student.PasswordSetAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- the signed-in student ----------

    [HttpGet("me")]
    [Authorize(Roles = SystemRole.Student)]
    public async Task<ActionResult<StudentProfileResponse>> Me(CancellationToken ct)
    {
        var student = await CurrentStudent(ct);
        return student is null ? Unauthorized() : Ok(await Profile(student, ct));
    }

    [HttpGet("certificates")]
    [Authorize(Roles = SystemRole.Student)]
    public async Task<ActionResult<IEnumerable<StudentCertificateResponse>>> Certificates(CancellationToken ct)
    {
        var studentId = CurrentStudentId();
        var credentials = await _db.Credentials.AsNoTracking()
            .Include(c => c.Achievement)
            .Where(c => c.StudentId == studentId && c.PersonalDataErasedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return Ok(credentials.Select(ToResponse));
    }

    // Dummy payment: marks an AwaitingPayment certificate paid. It then goes
    // to an approver, or is issued at once if the key that asked for it has
    // auto-issue.
    [HttpPost("certificates/{id:guid}/pay")]
    [Authorize(Roles = SystemRole.Student)]
    public async Task<ActionResult<StudentCertificateResponse>> Pay(Guid id, CancellationToken ct)
    {
        var studentId = CurrentStudentId();
        var credential = await _db.Credentials.Include(c => c.Achievement)
            .FirstOrDefaultAsync(c => c.Id == id && c.StudentId == studentId && c.PersonalDataErasedAt == null, ct);
        if (credential is null) return NotFound(new { error = "Certificate not found." });
        if (credential.Status != CredentialStatus.AwaitingPayment)
        {
            return Conflict(new { error = "This certificate isn't awaiting payment." });
        }
        if (credential.PaymentDueAt is { } due && due <= DateTimeOffset.UtcNow)
        {
            // Past the deadline; the deadline job cancels it shortly.
            return Conflict(new { error = "The time to pay for this certificate has passed, so it can't be paid for now." });
        }

        try
        {
            await _issuance.MarkPaidAsync(credential, ct);
        }
        catch (CredentialStateChangedException)
        {
            return Conflict(new { error = "This certificate isn't awaiting payment any more. Reload the page." });
        }

        var apiClient = await _db.ApiClients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ServiceUserId == credential.RequestedByUserId && c.RevokedAt == null, ct);
        if (apiClient is { AutoIssue: true })
        {
            await _issuance.ApproveAsync(credential, apiClient.AutoIssueConfirmedByUserId ?? apiClient.CreatedByUserId, credential.RequestedByUserId, apiClient.Id, ct);
        }
        return Ok(ToResponse(credential));
    }

    // ---------- helpers ----------

    // The link's student and purpose, or the error to send: 404 for a link
    // that isn't ours (or is for another institution, or was replaced or
    // used), 410 for an expired one.
    private async Task<(Student? Student, StudentPortalLink.Purpose Purpose, ActionResult? Error)> ReadLink(string? token, CancellationToken ct)
    {
        var target = _links.Read(token);
        if (target is null || target.TenantSlug != _tenantContext.TenantSlug)
        {
            return (null, default, NotFound(new { error = "This link isn't valid. Ask for a new one." }));
        }
        if (target.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return (null, default, StatusCode(StatusCodes.Status410Gone, new { error = "This link has expired. Ask for a new one." }));
        }
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == target.StudentId, ct);
        if (student is null || student.SignInVersion != target.SignInVersion)
        {
            return (null, default, NotFound(new { error = "This link isn't valid any more. Ask for a new one." }));
        }
        return (student, target.Purpose, null);
    }

    private async Task<StudentLoginResponse> SignedIn(Student student, CancellationToken ct)
    {
        // Self-heals a missing email-only sign-in entry (StudentSignInIndex).
        await _signInIndex.AddAsync(_tenantContext.TenantId, student.Email, ct);
        var token = _jwt.IssueStudentToken(student, _tenantContext.TenantId, _tenantContext.TenantSlug, _tenantContext.SchemaName);
        var minutes = _configuration.GetValue("Jwt:AccessTokenMinutes", 30);
        return new StudentLoginResponse(token, DateTime.UtcNow.AddMinutes(minutes), await Profile(student, ct));
    }

    private async Task<StudentProfileResponse> Profile(Student student, CancellationToken ct) =>
        new(student.Id, student.FullName, student.Email, student.StudentNumber, await InstitutionName(ct), _tenantContext.TenantSlug);

    private async Task<string> InstitutionName(CancellationToken ct) =>
        await _platformDb.Tenants.AsNoTracking().Where(t => t.Id == _tenantContext.TenantId).Select(t => t.Name).FirstAsync(ct);

    private Guid CurrentStudentId() =>
        Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    private Task<Student?> CurrentStudent(CancellationToken ct)
    {
        var id = CurrentStudentId();
        return _db.Students.FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    private StudentCertificateResponse ToResponse(Credential c)
    {
        var verifiable = c.Status is CredentialStatus.Active or CredentialStatus.Suspended or CredentialStatus.Revoked;
        var status = c.Status == CredentialStatus.Active && c.ExpiresOn is { } exp && exp < DateTimeOffset.UtcNow
            ? "Expired"
            : c.Status == CredentialStatus.PendingFinalApproval ? "PendingApproval" : c.Status.ToString();
        return new StudentCertificateResponse(
            c.Id, c.Achievement.Title, status, c.CreatedAt, c.PaidAt, c.IssuedAt, c.ExpiresOn,
            verifiable ? $"/verify/{_tenantContext.TenantSlug}/{c.Id}" : null,
            verifiable ? Certyfi.Infrastructure.Signing.VerificationCodes.Format(c.VerificationCode) : null,
            c.Status == CredentialStatus.Rejected ? c.RejectionReason : null,
            c.Status == CredentialStatus.AwaitingPayment ? c.PaymentDueAt : null, c.CancelledForNonPayment,
            Fields(c.FieldSchemaJson, c.FieldValuesJson));
    }

    // The certificate's own text fields, labelled, in schema order (tables
    // are left out; they're on the certificate itself).
    private static IReadOnlyList<StudentCertificateField> Fields(string schemaJson, string valuesJson)
    {
        var fields = new List<StudentCertificateField>();
        try
        {
            using var schema = System.Text.Json.JsonDocument.Parse(schemaJson);
            using var values = System.Text.Json.JsonDocument.Parse(valuesJson);
            if (schema.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array || values.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return fields;
            foreach (var field in schema.RootElement.EnumerateArray())
            {
                if (!field.TryGetProperty("key", out var key) || key.GetString() is not { } k) continue;
                if (field.TryGetProperty("type", out var type) && type.GetString() == "table") continue;
                if (!values.RootElement.TryGetProperty(k, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var label = field.TryGetProperty("label", out var l) && l.GetString() is { Length: > 0 } lt ? lt : k;
                fields.Add(new StudentCertificateField(label, text));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed data shouldn't break the list.
        }
        return fields;
    }
}
