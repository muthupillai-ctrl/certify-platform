using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Certyfi.Api.Auth;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Certyfi.Api.Controllers;

// The Partner API: server-to-server credential issuance for other systems
// (LMSpromise, Montessori360, an institution's own SIS). Authenticated only
// by API key (ApiKeyAuthenticationHandler), each action gated on one
// PartnerScope, and rate-limited per key.
//
// A key only ever sees credentials IT created (RequestedByUserId == its
// service account) — never the institution's other credentials, including
// those created by other keys. Anything else is a 404, not a 403, so a key
// can't probe for credential ids it doesn't own.
//
// Maker-checker: credentials land PendingApproval for a human Approver,
// unless an InstitutionAdmin created the key with AutoIssue — then they're
// approved immediately with that admin recorded as the checker (see
// ApiClient.AutoIssue).
[ApiController]
[Route("api/v1/partner")]
[ApiExplorerSettings(GroupName = PartnerApiDocs.DocumentName)]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting(PartnerRateLimit.PolicyName)]
[Produces("application/json")]
public class PartnerController : ControllerBase
{
    private const int MaxListLimit = 200;

    private readonly TenantDbContext _db;
    private readonly CredentialIssuanceService _issuance;
    private readonly RevocationService _revocations;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly TemplatePreviewLink _previewLinks;
    private readonly StudentDirectory _students;
    private readonly StudentNotifications _notifications;
    private readonly PlatformDbContext _platformDb;

    // Shown in the student's next step, so they know which email to look for.
    private readonly string _emailFrom;

    public PartnerController(TenantDbContext db, CredentialIssuanceService issuance, RevocationService revocations, ITenantContext tenantContext,
        IConfiguration configuration, TemplatePreviewLink previewLinks, StudentDirectory students, StudentNotifications notifications,
        PlatformDbContext platformDb, IOptions<EmailOptions> emailOptions)
    {
        _emailFrom = emailOptions.Value.From;
        _platformDb = platformDb;
        _previewLinks = previewLinks;
        _students = students;
        _notifications = notifications;
        _db = db;
        _issuance = issuance;
        _revocations = revocations;
        _tenantContext = tenantContext;
        _configuration = configuration;
    }

    /// <summary>List the institution's active certificate templates.</summary>
    /// <remarks>Use a template's id and field schema to build an issue request. Requires <c>templates:read</c>.</remarks>
    [HttpGet("templates")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.TemplatesRead)]
    [ProducesResponseType(typeof(IEnumerable<PartnerTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PartnerTemplateResponse>>> ListTemplates(CancellationToken ct)
    {
        var templates = await _db.CertificateTemplates.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
        return Ok(templates.Select(t => new PartnerTemplateResponse(
            t.Id, t.Name, t.Description, t.Kind.ToString(), JsonDocument.Parse(t.FieldSchemaJson).RootElement.Clone())));
    }

    /// <summary>Get a link to a preview of a template.</summary>
    /// <remarks>
    /// Returns a URL to a Certyfi page that draws the template with sample data, exactly as a certificate
    /// from it will look. Show it in a new tab or an iframe (e.g. next to your template picker). The link
    /// expires after 15 minutes, so request a fresh one each time you show it. Requires <c>templates:read</c>.
    /// </remarks>
    [HttpPost("templates/{id:guid}/preview-link")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.TemplatesRead)]
    [ProducesResponseType(typeof(PartnerTemplatePreviewLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PartnerTemplatePreviewLinkResponse>> CreateTemplatePreviewLink(Guid id, CancellationToken ct)
    {
        if (!await _db.CertificateTemplates.AnyAsync(t => t.Id == id && t.IsActive, ct))
        {
            return NotFound(new { error = "Unknown or inactive certificate template." });
        }

        var (token, expiresAt) = _previewLinks.Issue(_tenantContext.TenantSlug, id, DateTimeOffset.UtcNow);
        var baseUrl = _configuration["PublicApp:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new PartnerTemplatePreviewLinkResponse($"{baseUrl}/template-preview/{token}", expiresAt));
    }

    /// <summary>Issue a credential.</summary>
    /// <remarks>
    /// Returns 201 with the new credential. If <c>externalReference</c> matches a credential this key already
    /// created, nothing new is created and that credential is returned with 200, so retries are safe.
    /// The credential is <c>PendingApproval</c> until an approver at the institution approves it, or
    /// <c>Active</c> immediately if the key was created with auto-issue. If the key requires payment it starts
    /// as <c>AwaitingPayment</c> until the student pays in the student portal. Certyfi emails the student
    /// a link to the student portal (and again when the certificate is issued). Requires <c>credentials:write</c>.
    /// </remarks>
    [HttpPost("credentials")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.CredentialsWrite)]
    [ProducesResponseType(typeof(PartnerCredentialResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PartnerCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PartnerCredentialResponse>> IssueCredential(PartnerIssueCredentialRequest request, CancellationToken ct)
    {
        var validationError = ValidateIssueRequest(request);
        if (validationError is not null) return BadRequest(new { error = validationError });

        var serviceUserId = ServiceUserId();
        var externalReference = string.IsNullOrWhiteSpace(request.ExternalReference) ? null : request.ExternalReference.Trim();

        if (externalReference is not null)
        {
            var existing = await OwnCredentials().FirstOrDefaultAsync(c => c.ExternalReference == externalReference, ct);
            if (existing is not null) return Ok(ToResponse(existing));
        }

        var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.IsActive, ct);
        if (template is null)
        {
            return BadRequest(new { error = "Unknown or inactive certificate template." });
        }

        var title = request.Title.Trim();
        var achievement = await _issuance.GetOrCreateAchievementForTemplateAsync(template, title, ct);

        var fieldValuesJson = request.FieldValues is { } fv ? fv.GetRawText() : "{}";
        if (!FieldSchemaValidator.TryValidateValues(fieldValuesJson, achievement.FieldSchemaJson, out var fieldError, await _issuance.RequiredFieldKeysAsync(achievement, ct)))
        {
            return BadRequest(new { error = fieldError });
        }
        try
        {
            // Checked before the student is created or updated.
            await _issuance.SignatoriesJsonAsync(achievement, ct);
        }
        catch (MissingSignatoryException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var studentNumber = string.IsNullOrWhiteSpace(request.StudentId) ? null : request.StudentId.Trim();
        var apiClient = await _db.ApiClients.AsNoTracking().FirstAsync(c => c.ServiceUserId == serviceUserId, ct);
        var (student, conflict, oldEmail) = await _students.ForPartnerAsync(
            studentNumber, request.RecipientEmail, request.RecipientName.Trim(), serviceUserId, $"{apiClient.Name} (API)", ct);
        if (conflict is not null) return Conflict(new { error = conflict });
        if (student is not null && oldEmail is not null)
        {
            await _notifications.EmailChangedAsync(student, oldEmail, sendLinkToNewAddress: false, ct);
        }
        var paymentDays = await PaymentDaysAsync(ct);
        DateTimeOffset? paymentDueAt = apiClient.RequiresPayment && paymentDays > 0 ? DateTimeOffset.UtcNow.AddDays(paymentDays) : null;

        Credential credential;
        try
        {
            credential = await _issuance.CreatePendingAsync(
                achievement, request.RecipientName.Trim(), request.RecipientEmail, fieldValuesJson, serviceUserId, externalReference, ct,
                student: student, awaitingPayment: apiClient.RequiresPayment, paymentDueAt: paymentDueAt);
        }
        catch (DbUpdateException ex) when (externalReference is not null
            && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race with a concurrent retry carrying the same
            // externalReference — the other request's credential is the one.
            _db.ChangeTracker.Clear();
            var winner = await OwnCredentials().FirstAsync(c => c.ExternalReference == externalReference, ct);
            return Ok(ToResponse(winner));
        }

        // With payment required, auto-issue happens when the student pays
        // (StudentPortalController.Pay).
        if (apiClient.AutoIssue && !apiClient.RequiresPayment)
        {
            await _db.Entry(credential).Reference(c => c.Achievement).LoadAsync(ct);
            await _issuance.ApproveAsync(credential, apiClient.AutoIssueConfirmedByUserId ?? apiClient.CreatedByUserId, serviceUserId, apiClient.Id, ct);
        }

        // Tell the student (an auto-issued one got the "issued" email already).
        if (student is not null)
        {
            await _notifications.CertificateRequestedAsync(credential, student, title, ct);
        }

        var created = await OwnCredentials().FirstAsync(c => c.Id == credential.Id, ct);
        return CreatedAtAction(nameof(GetCredential), new { id = credential.Id }, ToResponse(created) with { PaymentDays = apiClient.RequiresPayment ? paymentDays : null });
    }

    /// <summary>Get a credential this key created.</summary>
    /// <remarks>Requires <c>credentials:read</c>.</remarks>
    [HttpGet("credentials/{id:guid}")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.CredentialsRead)]
    [ProducesResponseType(typeof(PartnerCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PartnerCredentialResponse>> GetCredential(Guid id, CancellationToken ct)
    {
        var credential = await OwnCredentials().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound(new { error = "Credential not found." });
        var pending = await PendingRevocations([id], ct);
        return Ok(ToResponse(credential, pending.Contains(id)));
    }

    /// <summary>List credentials this key created, newest first.</summary>
    /// <remarks>
    /// Filter by <c>externalReference</c> to look up the credential for one record in your system.
    /// <c>limit</c> defaults to 50 (max 200). Requires <c>credentials:read</c>.
    /// </remarks>
    [HttpGet("credentials")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.CredentialsRead)]
    [ProducesResponseType(typeof(IEnumerable<PartnerCredentialResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PartnerCredentialResponse>>> ListCredentials(
        [FromQuery] string? externalReference, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        var query = OwnCredentials();
        if (!string.IsNullOrWhiteSpace(externalReference))
        {
            var reference = externalReference.Trim();
            query = query.Where(c => c.ExternalReference == reference);
        }
        var credentials = await query.OrderByDescending(c => c.CreatedAt).Take(limit).ToListAsync(ct);
        var pending = await PendingRevocations(credentials.Select(c => c.Id).ToList(), ct);
        return Ok(credentials.Select(c => ToResponse(c, pending.Contains(c.Id))));
    }

    /// <summary>Ask for a credential this key created to be revoked.</summary>
    /// <remarks>
    /// Revoking is permanent, so it needs a person at the institution to approve it: this records a
    /// revocation request and returns <c>202 Accepted</c> with <c>revocationRequested: true</c>. The
    /// credential stays as it is until someone approves it; <c>status</c> then becomes <c>Revoked</c>
    /// (poll <c>GET /credentials/{id}</c>). Calling again while a request is waiting returns the same 202.
    /// Only Active or Suspended credentials can be revoked. A reason is required. Requires <c>credentials:revoke</c>.
    /// </remarks>
    [HttpPost("credentials/{id:guid}/revoke")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.CredentialsRevoke)]
    [ProducesResponseType(typeof(PartnerCredentialResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PartnerCredentialResponse>> RevokeCredential(Guid id, PartnerRevokeCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "A reason is required to revoke a credential." });
        }

        var serviceUserId = ServiceUserId();
        var credential = await OwnCredentials().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound(new { error = "Credential not found." });
        if (credential.Status is CredentialStatus.AwaitingPayment or CredentialStatus.PendingApproval or CredentialStatus.PendingFinalApproval)
        {
            return Conflict(new { error = "This credential hasn't been issued yet, so there's nothing to revoke. Cancel it instead: POST /credentials/{id}/cancel." });
        }

        var (outcome, _) = await _revocations.RequestAsync(id, serviceUserId, RevocationReasonCategory.PartnerSystem, request.Reason.Trim(), ct);
        if (outcome.Error is { } error)
        {
            return error.Kind == RevocationService.OutcomeErrorKind.Conflict
                ? Conflict(new { error = error.Message })
                : BadRequest(new { error = error.Message });
        }
        return Accepted(ToResponse(credential, revocationRequested: true));
    }

    /// <summary>Cancel a credential that hasn't been issued yet.</summary>
    /// <remarks>
    /// For a credential this key requested that is still <c>AwaitingPayment</c> or <c>PendingApproval</c>
    /// (e.g. the enrolment was cancelled). It becomes <c>Cancelled</c> straight away: the student can no
    /// longer pay for it, an approver can no longer approve it, and the student is emailed. Cancelling one
    /// that's already cancelled returns it again (200). An issued credential gets 409; revoke it instead.
    /// Requires <c>credentials:write</c>.
    /// </remarks>
    [HttpPost("credentials/{id:guid}/cancel")]
    [Authorize(Policy = PartnerApiDocs.PolicyPrefix + PartnerScope.CredentialsWrite)]
    [ProducesResponseType(typeof(PartnerCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PartnerCredentialResponse>> CancelCredential(Guid id, PartnerCancelCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
        {
            return BadRequest(new { error = "A reason is required to cancel a credential (max 500 characters)." });
        }

        var serviceUserId = ServiceUserId();
        var credential = await _db.Credentials.Include(c => c.Achievement).Include(c => c.Student)
            .FirstOrDefaultAsync(c => c.Id == id && c.RequestedByUserId == serviceUserId, ct);
        if (credential is null) return NotFound(new { error = "Credential not found." });
        if (credential.Status == CredentialStatus.Cancelled) return Ok(ToResponse(credential));
        if (credential.Status is not (CredentialStatus.AwaitingPayment or CredentialStatus.PendingApproval or CredentialStatus.PendingFinalApproval))
        {
            return Conflict(new
            {
                error = credential.Status is CredentialStatus.Active or CredentialStatus.Suspended
                    ? "This credential has been issued. Revoke it instead: POST /credentials/{id}/revoke."
                    : $"A {credential.Status.ToString().ToLowerInvariant()} credential can't be cancelled.",
            });
        }

        try
        {
            await _issuance.CancelAsync(credential, serviceUserId, request.Reason.Trim(), ct);
        }
        catch (CredentialStateChangedException)
        {
            // Paid or approved a moment ago; report what it is now.
            _db.ChangeTracker.Clear();
            var now = await OwnCredentials().FirstAsync(c => c.Id == id, ct);
            return Conflict(new { error = $"The credential just changed to {now.Status} and can't be cancelled now." });
        }
        return Ok(ToResponse(credential));
    }

    private async Task<HashSet<Guid>> PendingRevocations(List<Guid> ids, CancellationToken ct) =>
        (await _db.RevocationRequests.AsNoTracking()
            .Where(r => ids.Contains(r.CredentialId) && r.Status == RevocationRequestStatus.Pending)
            .Select(r => r.CredentialId).ToListAsync(ct)).ToHashSet();

    private static string? ValidateIssueRequest(PartnerIssueCredentialRequest request)
    {
        if (request.TemplateId == Guid.Empty) return "templateId is required.";
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 300) return "title is required (max 300 characters).";
        if (string.IsNullOrWhiteSpace(request.RecipientName) || request.RecipientName.Trim().Length > 200) return "recipientName is required (max 200 characters).";
        if (string.IsNullOrWhiteSpace(request.RecipientEmail) || request.RecipientEmail.Trim().Length > 320 || !request.RecipientEmail.Contains('@'))
            return "recipientEmail must be a valid email address.";
        if (request.ExternalReference is { Length: > 200 }) return "externalReference must be at most 200 characters.";
        if (request.StudentId is { } studentId && studentId.Trim().Length > 100) return "studentId must be at most 100 characters.";
        if (request.FieldValues is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) }) return "fieldValues must be a JSON object.";
        return null;
    }

    private IQueryable<Credential> OwnCredentials()
    {
        var serviceUserId = ServiceUserId();
        return _db.Credentials.AsNoTracking()
            .Include(c => c.Achievement)
            .Include(c => c.Student)
            .Where(c => c.RequestedByUserId == serviceUserId);
    }

    private Guid ServiceUserId() => Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

    private PartnerCredentialResponse ToResponse(Credential c, bool revocationRequested = false)
    {
        var verifiable = c.Status is CredentialStatus.Active or CredentialStatus.Suspended or CredentialStatus.Revoked;
        var baseUrl = _configuration["PublicApp:BaseUrl"]?.TrimEnd('/');
        var verificationUrl = verifiable && !string.IsNullOrEmpty(baseUrl)
            ? $"{baseUrl}/verify/{_tenantContext.TenantSlug}/{c.Id}"
            : null;

        // Same rule as the public verify page: "Expired" is never stored.
        // Final approval is an internal step: partners see PendingApproval.
        var status = c.Status == CredentialStatus.Active && c.ExpiresOn is { } exp && exp < DateTimeOffset.UtcNow
            ? "Expired"
            : c.Status == CredentialStatus.PendingFinalApproval ? "PendingApproval" : c.Status.ToString();

        return new PartnerCredentialResponse(
            c.Id, status, c.Achievement.Title, c.RecipientName, c.RecipientEmail, c.ExternalReference,
            c.CreatedAt, c.IssuedAt, c.ExpiresOn, c.RevokedAt, verificationUrl,
            Certyfi.Infrastructure.Signing.VerificationCodes.Format(c.VerificationCode), revocationRequested,
            c.Student?.StudentNumber, c.PaidAt, c.CancelledAt,
            c.PaymentDueAt, null, c.CancelledForNonPayment, c.Status == CredentialStatus.Cancelled ? c.CancellationReason : null,
            NextStep(c, status));
    }

    // The student's next step (see PartnerNextStep), pointing them at the
    // email Certyfi sent: the partner app never gets the student's own link.
    private PartnerNextStep NextStep(Credential c, string status)
    {
        var from = SenderAddress();
        return status switch
        {
            "AwaitingPayment" => new("check_email_to_pay",
                $"Check your email from Certyfi ({from}) to pay for this certificate"
                + (c.PaymentDueAt is { } due ? " by " + due.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "")
                + ". It won't be issued until it's paid. If you can't find the email, look in your spam folder."),
            "PendingApproval" => new("awaiting_approval", "Your institution is reviewing this certificate. You'll get an email when it's issued."),
            "Active" => new("issued", "Your certificate has been issued. Check your email for the link to view and share it."),
            "Expired" => new("expired", "This certificate has expired."),
            "Rejected" => new("rejected", "This certificate wasn't approved. Check your email for the reason."),
            "Cancelled" => new("cancelled", c.CancelledForNonPayment
                ? "This request was cancelled because it wasn't paid by the due date."
                : "This request was cancelled."),
            "Suspended" => new("suspended", "This certificate is suspended for now. Check your email for details."),
            "Revoked" => new("revoked", "This certificate has been revoked. Check your email for details."),
            _ => new("none", ""),
        };
    }

    // The address student emails come from ("Certyfi <no-reply@…>" → "no-reply@…").
    private string SenderAddress()
    {
        var from = _emailFrom;
        var start = from.IndexOf('<');
        var end = from.IndexOf('>');
        return start >= 0 && end > start ? from[(start + 1)..end] : from.Trim();
    }

    private Task<int> PaymentDaysAsync(CancellationToken ct) =>
        _platformDb.Tenants.AsNoTracking().Where(t => t.Id == _tenantContext.TenantId).Select(t => t.PaymentDays).FirstAsync(ct);
}
