using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Signing;
using Certyfi.Infrastructure.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped. Maker-checker workflow (capability note 4.5.2): a
// Registrar/Learner/InstitutionAdmin drafts (Create), a DIFFERENT Approver/
// InstitutionAdmin decides (Approve/Reject) — self-approval is blocked
// explicitly below, not just by role, and applies to InstitutionAdmin too
// with no exemption. Suspend/Reactivate/Revoke act on an already-decided
// credential, not against the original maker, so no self-check applies there.
// Learner can view and draft/request, same as Registrar, but never
// approve/reject/suspend/reactivate/revoke, and (see AchievementsController)
// cannot create Achievement templates either.
//
// Every state-changing action also appends an entry to the tenant's
// hash-chained ledger (LedgerEntry/LedgerService) inside the SAME
// transaction as the credential's own mutation — the credential row and its
// audit trail entry either both commit or neither does.
[ApiController]
[Route("api/v1/credentials")]
[Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Approver},{SystemRole.InstitutionAdmin},{SystemRole.Learner}")]
public class CredentialsController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly ILedgerService _ledger;
    private readonly CredentialIssuanceService _issuance;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CredentialsController> _logger;

    public CredentialsController(
        TenantDbContext db, ILedgerService ledger, CredentialIssuanceService issuance, ITenantContext tenantContext,
        IConfiguration configuration, ILogger<CredentialsController> logger)
    {
        _db = db;
        _ledger = ledger;
        _issuance = issuance;
        _tenantContext = tenantContext;
        _configuration = configuration;
        _logger = logger;
    }

    // ---------- Bulk actions and export (plan phase 4) ----------

    private const int MaxBulk = 500;
    private const int MaxExportRows = 10_000;

    // Approve / reject many certificates at once: the ids
    // given, or everything matching `filter` (up to 500). Each certificate
    // goes through exactly the single-item rules — maker–checker (never your
    // own request), status checks, its own signature and ledger entry — and
    // anything that doesn't qualify is skipped with the reason rather than
    // failing the batch. Suspend and revoke are deliberately NOT bulk: they
    // are one certificate at a time on the Revoke & suspend page (revoke
    // through a second-person request — see RevocationService).
    // Route value is "operation", not "action": MVC reserves {action} for the
    // method name, and a parameter called that never matches.
    [HttpPost("bulk/{operation}")]
    [Authorize(Roles = $"{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<BulkActionResponse>> Bulk(string operation, BulkActionRequest request, CancellationToken ct)
    {
        var action = operation.ToLowerInvariant();
        if (action is not ("approve" or "reject"))
            return NotFound(new { error = $"Unknown bulk action '{action}'." });
        var reason = request.Reason?.Trim();
        if (action != "approve" && string.IsNullOrEmpty(reason))
            return BadRequest(new { error = $"A reason is required to {action} certificates." });

        var userId = CurrentUserId();
        List<Guid> ids;
        if (request.Ids is { Count: > 0 })
        {
            ids = request.Ids.Distinct().ToList();
        }
        else if (request.Filter is { } filter)
        {
            var query = ApplyNonStatusFilters(Visible(), filter, userId);
            if (!TryApplyStatus(ref query, filter.Status, DateTimeOffset.UtcNow)) return BadRequest(new { error = $"Unknown status '{filter.Status}'." });
            ids = await query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Id).Take(MaxBulk + 1).ToListAsync(ct);
        }
        else
        {
            return BadRequest(new { error = "Select certificates (ids) or give a filter." });
        }
        if (ids.Count > MaxBulk)
            return BadRequest(new { error = $"At most {MaxBulk} certificates at a time. Narrow the filters or select fewer." });

        // Each certificate is loaded fresh and processed in its own
        // transaction. After a failure the change tracker is cleared, which
        // is only safe because nothing else is tracked across iterations —
        // pre-loading them all would leave later ones detached (their ledger
        // entries saved without the status change).
        var results = new List<BulkItemResult>();
        foreach (var id in ids)
        {
            var credential = await _db.Credentials.Include(c => c.Achievement).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (credential is null)
            {
                results.Add(new BulkItemResult(id, null, "Skipped", "Not found."));
                continue;
            }
            var skip = DecisionProblem(credential, userId)?.Error;
            if (skip is not null)
            {
                results.Add(new BulkItemResult(id, credential.RecipientName, "Skipped", skip));
                continue;
            }
            try
            {
                switch (action)
                {
                    case "approve" when credential.Status == CredentialStatus.PendingFinalApproval: await _issuance.FinalApproveAsync(credential, userId, ct); break;
                    case "approve": await _issuance.ApproveAsync(credential, userId, userId, autoIssueApiClientId: null, ct); break;
                    case "reject": await _issuance.RejectAsync(credential, userId, reason!, ct); break;
                }
                results.Add(new BulkItemResult(id, credential.RecipientName, "Done", null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bulk {Action} failed for credential {CredentialId}.", action, id);
                _db.ChangeTracker.Clear();
                results.Add(new BulkItemResult(id, credential.RecipientName, "Skipped", "Couldn't be processed; try it on its own."));
            }
        }

        return Ok(new BulkActionResponse(action, results.Count(r => r.Outcome == "Done"), results.Count(r => r.Outcome == "Skipped"), results));
    }

    private static string Describe(CredentialStatus status) => status switch
    {
        CredentialStatus.PendingApproval => "pending approval",
        CredentialStatus.AwaitingPayment => "awaiting payment",
        CredentialStatus.PendingFinalApproval => "waiting for final approval",
        _ => status.ToString().ToLowerInvariant(),
    };

    // The current filters (all pages) as a CSV spreadsheet, up to 10,000
    // rows, with one column per custom field. Scoped like the list (a
    // Learner exports only their own).
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] CredentialListQuery request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var query = ApplyNonStatusFilters(Visible(), request, CurrentUserId());
        if (!TryApplyStatus(ref query, request.Status, now)) return BadRequest(new { error = $"Unknown status '{request.Status}'." });

        var rows = await Sort(query, request.Sort)
            .Take(MaxExportRows + 1)
            .Select(c => new
            {
                c.Id, c.RecipientName, c.RecipientEmail, Achievement = c.Achievement.Title, c.Status, c.ExpiresOn, c.IssuedAt,
                c.CreatedAt, c.VerificationCode, RequestedBy = c.RequestedByUser.FullName, Api = c.RequestedByUser.IsServiceAccount,
                c.FieldSchemaJson, c.FieldValuesJson,
            })
            .ToListAsync(ct);
        if (rows.Count > MaxExportRows)
            return BadRequest(new { error = $"More than {MaxExportRows:N0} certificates match. Narrow the filters to export." });

        var siteBase = _configuration["PublicApp:BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var fieldColumns = new List<(string Key, string Label)>();
        var parsedValues = new List<Dictionary<string, string>>();
        foreach (var row in rows)
        {
            foreach (var (key, label) in CsvExport.FieldLabels(row.FieldSchemaJson))
            {
                if (fieldColumns.All(f => f.Key != key)) fieldColumns.Add((key, label));
            }
            parsedValues.Add(CsvExport.FieldValues(row.FieldValuesJson));
        }

        var csv = new CsvExport();
        csv.Row(new[] { "Recipient", "Email", "Achievement", "Status", "Certificate code", "Requested (UTC)", "Requested by", "Source",
            "Issued (UTC)", "Expires (UTC)", "Verification link" }.Concat(fieldColumns.Select(f => f.Label)));
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var status = r.Status == CredentialStatus.Active && r.ExpiresOn < now ? "Expired" : r.Status.ToString();
            var verifiable = r.Status is CredentialStatus.Active or CredentialStatus.Suspended or CredentialStatus.Revoked;
            csv.Row(new[]
            {
                r.RecipientName, r.RecipientEmail, r.Achievement, status, VerificationCodes.Format(r.VerificationCode),
                r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm"), r.RequestedBy, r.Api ? "Partner API" : "Staff",
                r.IssuedAt?.UtcDateTime.ToString("yyyy-MM-dd") ?? "", r.ExpiresOn?.UtcDateTime.ToString("yyyy-MM-dd") ?? "",
                verifiable ? $"{siteBase}/verify/{_tenantContext.TenantSlug}/{r.Id}" : "",
            }.Concat(fieldColumns.Select(f => parsedValues[i].GetValueOrDefault(f.Key, ""))));
        }

        return File(csv.ToUtf8WithBom(), "text/csv; charset=utf-8", $"certificates-{_tenantContext.TenantSlug}-{now:yyyyMMdd}.csv");
    }

    // One page of certificate summaries, filtered and sorted on the server
    // (the full record, with layout and field data, comes from
    // GET /credentials/{id} when one is opened). StatusCounts apply every
    // filter except status, so the Status dropdown can show what each
    // choice would return.
    [HttpGet]
    public async Task<ActionResult<CredentialPageResponse>> List([FromQuery] CredentialListQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page ?? 1);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var now = DateTimeOffset.UtcNow;

        var filtered = ApplyNonStatusFilters(Visible(), request, CurrentUserId());

        var counts = await filtered
            .GroupBy(c => new { c.Status, Expired = c.ExpiresOn != null && c.ExpiresOn < now })
            .Select(g => new { g.Key.Status, g.Key.Expired, Count = g.Count() })
            .ToListAsync(ct);
        var statusCounts = counts
            .GroupBy(c => c.Status == CredentialStatus.Active && c.Expired ? "Expired" : c.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        if (!TryApplyStatus(ref filtered, request.Status, now))
        {
            return BadRequest(new { error = $"Unknown status '{request.Status}'." });
        }

        var total = await filtered.CountAsync(ct);
        var items = await Sort(filtered, request.Sort)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CredentialSummaryResponse(
                c.Id, c.RecipientName, c.RecipientEmail, c.AchievementId, c.Achievement.Title,
                c.Status == CredentialStatus.Active && c.ExpiresOn != null && c.ExpiresOn < now ? "Expired" : c.Status.ToString(),
                c.VerificationCode, c.CreatedAt, c.IssuedAt, c.ExpiresOn,
                c.RequestedByUserId, c.RequestedByUser.FullName,
                c.ImportBatchId != null ? "Import" : c.RequestedByUser.IsServiceAccount ? "Api" : "Staff",
                c.OriginalIssueDate, c.LegacyCertificateNumber))
            .ToListAsync(ct);

        return Ok(new CredentialPageResponse(
            items.Select(i => i with { VerificationCode = VerificationCodes.Format(i.VerificationCode) }).ToList(),
            total, page, pageSize, statusCounts));
    }

    // Counts for the sidebar badge and the page's quick views. PendingForMe:
    // pending certificates this user may approve — they hold Approver or
    // Institution Admin, and didn't request it themselves (maker–checker).
    [HttpGet("summary")]
    public async Task<ActionResult<CredentialSummaryCountsResponse>> Summary(CancellationToken ct)
    {
        var userId = CurrentUserId();
        var canApprove = User.IsInRole(SystemRole.Approver) || User.IsInRole(SystemRole.InstitutionAdmin);
        var pendingForMe = canApprove ? await AwaitingDecisionBy(Visible(), userId).CountAsync(ct) : 0;
        var mine = await Visible().Where(c => c.RequestedByUserId == userId)
            .GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        // Revocation requests someone else made, which this user may decide.
        var revocationsForMe = canApprove
            ? await _db.RevocationRequests.CountAsync(r => r.Status == RevocationRequestStatus.Pending && r.RequestedByUserId != userId, ct)
            : 0;
        var isApprover = User.IsInRole(SystemRole.Approver);
        var isAdmin = User.IsInRole(SystemRole.InstitutionAdmin);
        var importsForMe = canApprove
            ? await _db.ImportBatches.CountAsync(b => b.CreatedByUserId != userId && (
                (isApprover && b.Status == ImportBatchStatus.PendingApproval)
                || (isAdmin && b.Status == ImportBatchStatus.PendingFinalApproval && b.DecidedByUserId != userId)), ct)
            : 0;
        return Ok(new CredentialSummaryCountsResponse(
            canApprove, pendingForMe,
            mine.Sum(m => m.Count),
            mine.Where(m => m.Key is CredentialStatus.PendingApproval or CredentialStatus.PendingFinalApproval).Sum(m => m.Count),
            mine.FirstOrDefault(m => m.Key == CredentialStatus.Rejected)?.Count ?? 0,
            revocationsForMe, importsForMe));
    }

    // Choices for the Achievement and Requested-by dropdowns: only those that
    // have certificates this user can see.
    [HttpGet("filter-options")]
    public async Task<ActionResult<CredentialFilterOptionsResponse>> FilterOptions(CancellationToken ct)
    {
        var achievements = await Visible().Select(c => new { c.AchievementId, c.Achievement.Title }).Distinct()
            .OrderBy(a => a.Title).ToListAsync(ct);
        var requesters = await Visible().Select(c => new { c.RequestedByUserId, c.RequestedByUser.FullName }).Distinct()
            .OrderBy(r => r.FullName).ToListAsync(ct);
        var visibleRequesters = Visible().Select(c => c.RequestedByUserId);
        var apiClients = await _db.ApiClients.AsNoTracking()
            .Where(a => visibleRequesters.Contains(a.ServiceUserId))
            .OrderBy(a => a.Name).Select(a => new FilterOption(a.Id, a.Name)).ToListAsync(ct);
        return Ok(new CredentialFilterOptionsResponse(
            achievements.Select(a => new FilterOption(a.AchievementId, a.Title)).ToList(),
            requesters.Select(r => new FilterOption(r.RequestedByUserId, r.FullName)).ToList(),
            apiClients));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CredentialResponse>> Get(Guid id, CancellationToken ct)
    {
        var credential = await Query().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();
        var pending = await _db.RevocationRequests.AsNoTracking()
            .Where(r => r.CredentialId == id && r.Status == RevocationRequestStatus.Pending)
            .Select(r => new { r.Id, r.ReasonCategory, r.Note, r.RequestedByUser.FullName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        return Ok(ToResponse(credential) with
        {
            PendingRevocation = pending is null ? null
                : new PendingRevocationResponse(pending.Id, RevocationReasonCategory.Label(pending.ReasonCategory), pending.Note, pending.FullName, pending.RequestedAt),
        });
    }

    [HttpGet("{id:guid}/ledger")]
    public async Task<ActionResult<IEnumerable<LedgerEntryResponse>>> GetLedger(Guid id, CancellationToken ct)
    {
        var exists = await Visible().AnyAsync(c => c.Id == id, ct);
        if (!exists) return NotFound();

        var entries = await _db.LedgerEntries.AsNoTracking()
            .Include(e => e.ActorUser)
            .Where(e => e.CredentialId == id)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct);

        return Ok(entries.Select(e => new LedgerEntryResponse(
            e.SequenceNumber, e.Id, e.EventType, e.ActorUserId, e.ActorUser.FullName,
            e.OccurredAt, e.EventDataJson, e.PreviousEntryHash, e.EntryHash)));
    }

    // How often, and how, this certificate has been checked publicly. Staff
    // see channels and times only, never who verified.
    [HttpGet("{id:guid}/verifications")]
    public async Task<ActionResult<CredentialVerificationHistoryResponse>> GetVerifications(Guid id, CancellationToken ct)
    {
        var exists = await Visible().AnyAsync(c => c.Id == id, ct);
        if (!exists) return NotFound();

        var byChannel = await _db.VerificationEvents.AsNoTracking()
            .Where(e => e.CredentialId == id)
            .GroupBy(e => e.Channel)
            .Select(g => new { Channel = g.Key, Count = g.Count(), Last = g.Max(e => e.OccurredAt) })
            .ToListAsync(ct);
        var recent = await _db.VerificationEvents.AsNoTracking()
            .Where(e => e.CredentialId == id)
            .OrderByDescending(e => e.OccurredAt)
            .Take(50)
            .Select(e => new VerificationEventResponse(e.Channel, e.Result, e.OccurredAt))
            .ToListAsync(ct);

        int Count(string channel) => byChannel.FirstOrDefault(c => c.Channel == channel)?.Count ?? 0;
        return Ok(new CredentialVerificationHistoryResponse(
            byChannel.Sum(c => c.Count),
            Count(VerificationChannel.VerifyPage), Count(VerificationChannel.CodeLookup),
            Count(VerificationChannel.CredentialDownload), Count(VerificationChannel.Api),
            byChannel.Count == 0 ? null : byChannel.Max(c => c.Last),
            recent));
    }

    [HttpPost]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Learner}")]
    public async Task<ActionResult<CredentialResponse>> Create(CreateCredentialRequest request, CancellationToken ct)
    {
        var achievement = await _db.Achievements.FirstOrDefaultAsync(a => a.Id == request.AchievementId && a.IsActive, ct);
        if (achievement is null)
        {
            return BadRequest(new { error = "Unknown or inactive achievement." });
        }

        return await CreateCredentialForAchievementAsync(achievement, request.RecipientName, request.RecipientEmail, request.FieldValuesJson, ct);
    }

    // The admin UI's "New certificate" form no longer makes staff pick an
    // Achievement directly — it picks a CertificateTemplate and types a
    // Title, and this endpoint resolves (or auto-creates) the Achievement
    // that Credential still actually references underneath. Reuse is exact-
    // match on (TemplateId, Title): this deliberately also picks up an
    // achievement someone built the fuller way (via POST /api/v1/achievements,
    // with a real description/criteria/validity period) from the same
    // template with the same title — the Achievements page stays the place
    // to pre-define a richer achievement ahead of time; this is the fast
    // path that reuses it if the title matches, or creates a minimal
    // title-only one if it doesn't exist yet.
    [HttpPost("from-template")]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Learner}")]
    public async Task<ActionResult<CredentialResponse>> CreateFromTemplate(CreateCredentialFromTemplateRequest request, CancellationToken ct)
    {
        var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.IsActive, ct);
        if (template is null)
        {
            return BadRequest(new { error = "Unknown or inactive certificate template." });
        }
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "Title is required." });
        }

        var achievement = await _issuance.GetOrCreateAchievementForTemplateAsync(template, request.Title, ct);

        return await CreateCredentialForAchievementAsync(achievement, request.RecipientName, request.RecipientEmail, request.FieldValuesJson, ct);
    }

    private async Task<ActionResult<CredentialResponse>> CreateCredentialForAchievementAsync(
        Achievement achievement, string recipientName, string recipientEmail, string? fieldValuesJson, CancellationToken ct)
    {
        var values = fieldValuesJson ?? "{}";
        if (!FieldSchemaValidator.TryValidateValues(values, achievement.FieldSchemaJson, out var error, await _issuance.RequiredFieldKeysAsync(achievement, ct)))
        {
            return BadRequest(new { error });
        }

        Credential credential;
        try
        {
            credential = await _issuance.CreatePendingAsync(
                achievement, recipientName, recipientEmail, values, CurrentUserId(), externalReference: null, ct);
        }
        catch (MissingSignatoryException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var created = await Query().FirstAsync(c => c.Id == credential.Id, ct);
        return CreatedAtAction(nameof(Get), new { id = credential.Id }, ToResponse(created));
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = $"{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<CredentialResponse>> Approve(Guid id, CancellationToken ct)
    {
        var credential = await _db.Credentials.Include(c => c.Achievement).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();
        var currentUserId = CurrentUserId();
        if (DecisionRefusal(credential, currentUserId) is { } refusal) return refusal;

        try
        {
            if (credential.Status == CredentialStatus.PendingFinalApproval)
                await _issuance.FinalApproveAsync(credential, currentUserId, ct);
            else
                await _issuance.ApproveAsync(credential, currentUserId, currentUserId, autoIssueApiClientId: null, ct);
        }
        catch (CredentialStateChangedException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        var updated = await Query().FirstAsync(c => c.Id == id, ct);
        return Ok(ToResponse(updated));
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = $"{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<CredentialResponse>> Reject(Guid id, RejectCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "A reason is required to reject a credential." });
        }

        var credential = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();
        var currentUserId = CurrentUserId();
        if (DecisionRefusal(credential, currentUserId) is { } refusal) return refusal;

        try
        {
            await _issuance.RejectAsync(credential, currentUserId, request.Reason, ct);
        }
        catch (CredentialStateChangedException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        var updated = await Query().FirstAsync(c => c.Id == id, ct);
        return Ok(ToResponse(updated));
    }

    // Suspend and reactivate are one-person actions (suspend is the quick,
    // reversible stop), but only from the Revoke & suspend page: the caller
    // must type the certificate's code to confirm. Revoking is two-person —
    // see RevocationRequestsController.
    [HttpPost("{id:guid}/suspend")]
    [Authorize(Roles = $"{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<CredentialResponse>> Suspend(Guid id, SuspendCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "A reason is required to suspend a credential." });
        }

        var credential = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();
        if (!CodeMatches(request.ConfirmCode, credential)) return BadRequest(new { error = ConfirmCodeError });
        if (credential.Status != CredentialStatus.Active)
        {
            return Conflict(new { error = $"Credential is '{credential.Status}', not active." });
        }

        await _issuance.SuspendAsync(credential, CurrentUserId(), request.Reason.Trim(), ct);

        var updated = await Query().FirstAsync(c => c.Id == id, ct);
        return Ok(ToResponse(updated));
    }

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Roles = $"{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<CredentialResponse>> Reactivate(Guid id, ReactivateCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            return BadRequest(new { error = "Say why the certificate can be used again." });
        }

        var credential = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();
        if (!CodeMatches(request.ConfirmCode, credential)) return BadRequest(new { error = ConfirmCodeError });
        if (credential.Status != CredentialStatus.Suspended)
        {
            return Conflict(new { error = $"Credential is '{credential.Status}', not suspended." });
        }

        await _issuance.ReactivateAsync(credential, CurrentUserId(), request.Note.Trim(), ct);

        var updated = await Query().FirstAsync(c => c.Id == id, ct);
        return Ok(ToResponse(updated));
    }

    // Erase the recipient's personal data (a data-protection erasure request).
    // Institution Admins only; the certificate must already be revoked or
    // rejected, and its code typed to confirm. See
    // CredentialIssuanceService.ErasePersonalDataAsync for what is kept.
    [HttpPost("{id:guid}/erase-personal-data")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<CredentialResponse>> ErasePersonalData(Guid id, ErasePersonalDataRequest request, CancellationToken ct)
    {
        var credential = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();
        if (!CodeMatches(request.ConfirmCode, credential)) return BadRequest(new { error = ConfirmCodeError });
        if (credential.PersonalDataErasedAt is not null) return Conflict(new { error = "This certificate's personal data was already erased." });
        if (credential.Status is not (CredentialStatus.Revoked or CredentialStatus.Rejected or CredentialStatus.Cancelled))
            return Conflict(new { error = "Only a revoked, rejected or cancelled certificate can have its personal data erased. Revoke it first." });
        if (await _db.RevocationRequests.AnyAsync(r => r.CredentialId == id && r.Status == RevocationRequestStatus.Pending, ct))
            return Conflict(new { error = "A revocation request for this certificate is still waiting. Decide it first." });

        await _issuance.ErasePersonalDataAsync(credential, CurrentUserId(), ct);
        var updated = await Query().FirstAsync(c => c.Id == id, ct);
        return Ok(ToResponse(updated));
    }

    // Who may approve or reject a certificate now: first approval needs the
    // Approver role, final approval (PendingFinalApproval) the Institution
    // admin role; and nobody decides one they requested or already
    // approved (three different people for final approval).
    private ActionResult? DecisionRefusal(Credential credential, Guid userId) =>
        DecisionProblem(credential, userId) is { } p ? StatusCode(p.Status, new { error = p.Error }) : null;

    private (int Status, string Error)? DecisionProblem(Credential credential, Guid userId)
    {
        if (credential.Status is not (CredentialStatus.PendingApproval or CredentialStatus.PendingFinalApproval))
            return (StatusCodes.Status409Conflict, $"It's {Describe(credential.Status)}, not waiting for approval.");
        if (credential.ImportBatchId is not null) return (StatusCodes.Status409Conflict, ImportedOneByOneError);
        var final = credential.Status == CredentialStatus.PendingFinalApproval;
        if (!final && !User.IsInRole(SystemRole.Approver))
            return (StatusCodes.Status403Forbidden, "The first approval is given by an Approver.");
        if (final && !User.IsInRole(SystemRole.InstitutionAdmin))
            return (StatusCodes.Status403Forbidden, "Final approval is given by an institution admin.");
        if (credential.RequestedByUserId == userId)
            return (StatusCodes.Status403Forbidden, "You requested it; someone else must approve or reject it.");
        if (final && credential.FirstApprovedByUserId == userId)
            return (StatusCodes.Status403Forbidden, "You gave the first approval; a different institution admin must give the final one.");
        return null;
    }

    // Certificates this user may decide now (see DecisionRefusal); imported
    // ones are decided as a batch.
    private IQueryable<Credential> AwaitingDecisionBy(IQueryable<Credential> query, Guid userId)
    {
        var approver = User.IsInRole(SystemRole.Approver);
        var admin = User.IsInRole(SystemRole.InstitutionAdmin);
        return query.Where(c => c.ImportBatchId == null && c.RequestedByUserId != userId && (
            (approver && c.Status == CredentialStatus.PendingApproval)
            || (admin && c.Status == CredentialStatus.PendingFinalApproval && c.FirstApprovedByUserId != userId)));
    }

    internal const string ImportedOneByOneError = "This certificate is part of an import; approve or reject the whole import on the Import page.";

    internal const string ConfirmCodeError = "Type the certificate's code to confirm.";

    // The typed code, ignoring dashes, spaces and case.
    internal static bool CodeMatches(string? typed, Credential credential) =>
        VerificationCodes.Normalize(typed) is { } code && code == credential.VerificationCode;

    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int MinSearchLength = 2;
    private const int MaxExpiringWithinDays = 3650;

    // What the signed-in user may see. Staff roles see the whole
    // institution; someone who is ONLY a Learner sees certificates issued
    // to their own email or requested by them — never other recipients'.
    private IQueryable<Credential> Visible()
    {
        var all = _db.Credentials.AsNoTracking();
        var isStaff = User.IsInRole(SystemRole.InstitutionAdmin) || User.IsInRole(SystemRole.Registrar) || User.IsInRole(SystemRole.Approver);
        if (isStaff) return all;

        var userId = CurrentUserId();
        var email = (User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? User.FindFirstValue(ClaimTypes.Email) ?? "").Trim().ToLowerInvariant();
        return all.Where(c => c.RequestedByUserId == userId || (email != "" && c.RecipientEmail == email));
    }

    private IQueryable<Credential> ApplyNonStatusFilters(IQueryable<Credential> query, CredentialListQuery request, Guid currentUserId)
    {
        if (request.AchievementId is { } achievementId) query = query.Where(c => c.AchievementId == achievementId);
        if (request.RequestedBy is { } requestedBy) query = query.Where(c => c.RequestedByUserId == requestedBy);
        // "My requests" and "Needs my approval" (pending, not requested by me —
        // maker–checker means those are the only ones I can approve).
        if (request.Mine == true) query = query.Where(c => c.RequestedByUserId == currentUserId);
        if (request.ExcludeMine == true) query = query.Where(c => c.RequestedByUserId != currentUserId);
        if (request.AwaitingMe == true) query = AwaitingDecisionBy(query, currentUserId);
        if (request.ImportBatchId is { } batchId) query = query.Where(c => c.ImportBatchId == batchId);
        // "Needs my approval": imported certificates are approved as a batch, not here.
        if (request.ExcludeImports == true) query = query.Where(c => c.ImportBatchId == null);

        if (request.IssuedFrom is { } issuedFrom) query = query.Where(c => c.IssuedAt >= issuedFrom);
        if (request.IssuedBefore is { } issuedBefore) query = query.Where(c => c.IssuedAt < issuedBefore);
        if (request.ExpiringWithinDays is { } days)
        {
            var now = DateTimeOffset.UtcNow;
            var until = now.AddDays(Math.Clamp(days, 0, MaxExpiringWithinDays));
            query = query.Where(c => c.Status == CredentialStatus.Active && c.ExpiresOn != null && c.ExpiresOn >= now && c.ExpiresOn <= until);
        }
        // Source comes from the requester: every Partner API key acts as its
        // own service account (see ApiClient), so no extra column is needed.
        if (string.Equals(request.Source, "Api", StringComparison.OrdinalIgnoreCase))
            query = query.Where(c => c.RequestedByUser.IsServiceAccount);
        else if (string.Equals(request.Source, "Staff", StringComparison.OrdinalIgnoreCase))
            query = query.Where(c => !c.RequestedByUser.IsServiceAccount);
        if (request.ApiClientId is { } apiClientId)
            query = query.Where(c => _db.ApiClients.Where(a => a.Id == apiClientId).Select(a => a.ServiceUserId).Contains(c.RequestedByUserId));

        var q = request.Q?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < MinSearchLength) return query;

        // Contains, case-insensitive, over the recipient, achievement,
        // requester and the certificate's custom field values (roll number
        // etc.); the code with dashes/spaces/case ignored; or the id.
        // Trigram indexes keep these fast (see TenantDbContext).
        var pattern = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var compact = new string(q.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var idText = q.ToLowerInvariant();
        return query.Where(c =>
            EF.Functions.ILike(c.RecipientName, pattern, "\\") ||
            EF.Functions.ILike(c.RecipientEmail, pattern, "\\") ||
            EF.Functions.ILike(c.Achievement.Title, pattern, "\\") ||
            EF.Functions.ILike(c.RequestedByUser.FullName, pattern, "\\") ||
            EF.Functions.ILike(c.FieldValuesJson, pattern, "\\") ||
            (c.LegacyCertificateNumber != null && EF.Functions.ILike(c.LegacyCertificateNumber, pattern, "\\")) ||
            (compact.Length >= 3 && c.VerificationCode.Contains(compact)) ||
            (idText.Length >= 8 && c.Id.ToString().Contains(idText)));
    }

    private static bool TryApplyStatus(ref IQueryable<Credential> query, string? status, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        if (status == "Expired")
        {
            query = query.Where(c => c.Status == CredentialStatus.Active && c.ExpiresOn != null && c.ExpiresOn < now);
            return true;
        }
        if (!Enum.TryParse<CredentialStatus>(status, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed)) return false;
        query = parsed == CredentialStatus.Active
            ? query.Where(c => c.Status == CredentialStatus.Active && (c.ExpiresOn == null || c.ExpiresOn >= now))
            : query.Where(c => c.Status == parsed);
        return true;
    }

    // sort: created | issued | recipient | expires, "-" prefix = descending.
    // Default: newest first. Certificates without the date (not yet issued,
    // no expiry) always sort last — Postgres would otherwise put NULLs first
    // in descending order, burying "recently issued" under pending ones. Id
    // breaks ties so paging is stable.
    private static IQueryable<Credential> Sort(IQueryable<Credential> query, string? sort)
    {
        var descending = sort?.StartsWith('-') ?? true;
        var key = (sort ?? "-created").TrimStart('-').ToLowerInvariant();
        IOrderedQueryable<Credential> ordered = (key, descending) switch
        {
            ("issued", true) => query.OrderBy(c => c.IssuedAt == null).ThenByDescending(c => c.IssuedAt),
            ("issued", false) => query.OrderBy(c => c.IssuedAt == null).ThenBy(c => c.IssuedAt),
            ("recipient", true) => query.OrderByDescending(c => c.RecipientName),
            ("recipient", false) => query.OrderBy(c => c.RecipientName),
            ("expires", true) => query.OrderBy(c => c.ExpiresOn == null).ThenByDescending(c => c.ExpiresOn),
            ("expires", false) => query.OrderBy(c => c.ExpiresOn == null).ThenBy(c => c.ExpiresOn),
            (_, false) => query.OrderBy(c => c.CreatedAt),
            _ => query.OrderByDescending(c => c.CreatedAt),
        };
        return ordered.ThenBy(c => c.Id);
    }

    private IQueryable<Credential> Query() =>
        Visible()
            .Include(c => c.Achievement)
            .Include(c => c.RequestedByUser)
            .Include(c => c.DecidedByUser);

    private Guid CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub!);
    }

    private static CredentialResponse ToResponse(Credential c) => new(
        c.Id, c.AchievementId, c.Achievement.Title, c.RecipientName, c.RecipientEmail,
        c.Status.ToString(), c.RequestedByUserId, c.RequestedByUser.FullName,
        c.DecidedByUserId, c.DecidedByUser?.FullName, c.DecidedAt, c.RejectionReason,
        c.IssuedAt, c.ExpiresOn,
        c.SuspensionReason, c.SuspendedAt,
        c.RevocationReason, c.RevokedAt,
        c.CreatedAt, c.FieldValuesJson,
        c.Achievement.Description, c.FieldSchemaJson, c.Achievement.TemplateKind.ToString(),
        c.Achievement.TemplateTitleText, c.Achievement.TemplateSubtitleText,
        c.Achievement.TemplateAccentColor, c.Achievement.TemplateFontFamily, c.Achievement.TemplateLayoutJson, c.Achievement.TemplateBackgroundImageUrl,
        c.CanonicalHash, c.Signature, c.SignatureKeyId,
        VerificationCodes.Format(c.VerificationCode), c.OfflineProof,
        null, c.OriginalIssueDate, c.LegacyCertificateNumber, c.ImportBatchId, c.PersonalDataErasedAt, c.StudentId, c.PaidAt, c.CancelledAt, c.CancellationReason, c.PaymentDueAt, c.CancelledForNonPayment,
        SignatoryName.FromSnapshot(c.SignatoriesJson));
}
