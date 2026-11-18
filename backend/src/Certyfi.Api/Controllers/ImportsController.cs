using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Api.Imports;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Digitising paper certificates from a spreadsheet.
//
//  1. parse   — the file's headers and rows as text (nothing stored).
//  2. check   — a dry run of every row: errors, likely duplicates.
//  3. create a batch, send its rows in chunks (each row becomes a
//     PendingApproval certificate in the batch), then finish.
//  4. a DIFFERENT Approver/Institution Admin approves the batch (typing how
//     many certificates it holds), which signs them in chunks; or rejects it.
//
// Registrars and Institution Admins import; Approvers and Institution Admins
// decide. Imported certificates are never approved one by one.
[ApiController]
[Route("api/v1/imports")]
[Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
public class ImportsController : ControllerBase
{
    // Institution admins run the institution but don't import or give the
    // first approval; they give final approval where a template needs it.
    private const string Importers = SystemRole.Registrar;
    private const string Deciders = SystemRole.Approver;
    private const int MaxRowsPerChunk = 500;
    private const int ApproveChunk = 100;
    private const int RejectChunk = 250;

    private readonly TenantDbContext _db;
    private readonly CredentialIssuanceService _issuance;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<ImportsController> _logger;

    public ImportsController(TenantDbContext db, CredentialIssuanceService issuance, ITenantContext tenantContext, ILogger<ImportsController> logger)
    {
        _db = db;
        _issuance = issuance;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    // ---------- 1. Read the file ----------

    [HttpPost("parse")]
    [Authorize(Roles = Importers)]
    [RequestSizeLimit(ImportFileReader.MaxBytes + 64 * 1024)]
    public ActionResult<ImportParseResponse> Parse(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Choose a file." });
        if (file.Length > ImportFileReader.MaxBytes) return BadRequest(new { error = "The file is larger than 5 MB. Split it into smaller files." });
        try
        {
            using var stream = file.OpenReadStream();
            var sheet = ImportFileReader.Read(stream, file.FileName);
            if (sheet.Rows.Count == 0) return BadRequest(new { error = "The file has a header row but no certificates." });
            return Ok(new ImportParseResponse(Path.GetFileName(file.FileName), sheet.Headers, sheet.Rows, sheet.Truncated));
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read import file {FileName}.", file.FileName);
            return BadRequest(new { error = "The file could not be read. Save it as .xlsx or .csv (UTF-8) and try again." });
        }
    }

    // A CSV with the right column headings for a template.
    [HttpGet("template.csv")]
    [Authorize(Roles = Importers)]
    public async Task<IActionResult> TemplateCsv([FromQuery] Guid templateId, CancellationToken ct)
    {
        var template = await _db.CertificateTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct);
        if (template is null) return NotFound(new { error = "Unknown or inactive certificate template." });
        var fields = CsvExport.FieldLabels(template.FieldSchemaJson).Where(f => !IsTableField(template.FieldSchemaJson, f.Key)).ToList();

        var csv = new CsvExport();
        csv.Row(new[] { "Recipient name", "Recipient email", "Achievement", "Original issue date", "Paper certificate number", "Expires on" }
            .Concat(fields.Select(f => f.Label)));
        csv.Row(new[] { "Jane Doe", "jane@example.com", "Fire Safety Level 1", "12/03/2015", "ACME/2015/0042", "" }
            .Concat(fields.Select(_ => "")));
        return File(csv.ToUtf8WithBom(), "text/csv; charset=utf-8", $"paper-certificates-{Slug(template.Name)}.csv");
    }

    // ---------- 2. Dry run ----------

    [HttpPost("check")]
    [Authorize(Roles = Importers)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<ImportCheckResponse>> Check(ImportCheckRequest request, CancellationToken ct)
    {
        if (request.Rows is not { Count: > 0 }) return BadRequest(new { error = "No rows to check." });
        if (request.Rows.Count > ImportFileReader.MaxRows) return BadRequest(new { error = $"At most {ImportFileReader.MaxRows:N0} rows per import." });
        var template = await _db.CertificateTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.IsActive, ct);
        if (template is null) return BadRequest(new { error = "Unknown or inactive certificate template." });

        var results = await Evaluate(request.Rows, 0, request.Title, DateFormat(request.DateFormat), template.FieldSchemaJson, ct);
        return Ok(new ImportCheckResponse(
            results.Count(r => r.Valid is not null),
            results.Count(r => r.Valid is null),
            results.Count(r => r.Valid is not null && r.LikelyDuplicate),
            results.Where(r => r.Errors.Count > 0 || r.Warnings.Count > 0)
                .Select(r => new ImportRowResult(r.Row, r.Errors, r.Warnings)).ToList()));
    }

    private record Evaluated(int Row, ImportRowValidator.Valid? Valid, List<string> Errors, List<string> Warnings, bool LikelyDuplicate);

    // Validates rows and checks them against each other and against
    // certificates already in the system.
    private async Task<List<Evaluated>> Evaluate(IReadOnlyList<ImportRow> rows, int firstRow, string? title, string dateFormat, string schemaJson, CancellationToken ct)
    {
        var results = rows.Select((row, i) =>
        {
            var (valid, errors) = ImportRowValidator.Validate(row, title, dateFormat, schemaJson);
            return new Evaluated(firstRow + i, valid, errors, [], false);
        }).ToList();

        // The same paper number twice in the file, or already on a live certificate.
        var seen = new Dictionary<string, int>();
        foreach (var r in results.Where(r => r.Valid?.LegacyNumberKey is not null))
        {
            if (seen.TryGetValue(r.Valid!.LegacyNumberKey!, out var first))
                r.Errors.Add($"Same paper certificate number as row {first + 2}.");
            else seen[r.Valid.LegacyNumberKey!] = r.Row;
        }
        var existingKeys = new HashSet<string>();
        foreach (var chunk in seen.Keys.Chunk(1000))
        {
            var found = await _db.Credentials.AsNoTracking()
                .Where(c => c.LegacyNumberKey != null && chunk.Contains(c.LegacyNumberKey) && c.Status != CredentialStatus.Rejected)
                .Select(c => c.LegacyNumberKey!).ToListAsync(ct);
            existingKeys.UnionWith(found);
        }
        foreach (var r in results.Where(r => r.Valid?.LegacyNumberKey is { } key && existingKeys.Contains(key)))
            r.Errors.Add($"Paper certificate number {r.Valid!.LegacyNumber} is already on a certificate.");

        // Likely already imported: same person, achievement and paper date.
        var dates = results.Where(r => r.Valid is not null).Select(r => r.Valid!.OriginalIssueDate).Distinct().ToList();
        var existing = new HashSet<(string, string, DateOnly)>();
        foreach (var chunk in dates.Chunk(500))
        {
            var found = await _db.Credentials.AsNoTracking()
                .Where(c => c.OriginalIssueDate != null && chunk.Contains(c.OriginalIssueDate.Value) && c.Status != CredentialStatus.Rejected)
                .Select(c => new { c.RecipientName, c.Achievement.Title, Date = c.OriginalIssueDate!.Value }).ToListAsync(ct);
            existing.UnionWith(found.Select(f => (f.RecipientName.ToLowerInvariant(), f.Title.ToLowerInvariant(), f.Date)));
        }

        return results.Select(r =>
        {
            if (r.Errors.Count > 0) return r with { Valid = null };
            var v = r.Valid!;
            var duplicate = existing.Contains((v.RecipientName.ToLowerInvariant(), v.Title.ToLowerInvariant(), v.OriginalIssueDate));
            if (duplicate) r.Warnings.Add("A certificate for this person, achievement and date was already imported.");
            if (v.RecipientEmail.Length == 0) r.Warnings.Add("No email: the recipient won't see it in their account until one is added.");
            return r with { LikelyDuplicate = duplicate };
        }).ToList();
    }

    // ---------- 3. Import ----------

    [HttpPost]
    [Authorize(Roles = Importers)]
    public async Task<ActionResult<ImportBatchResponse>> Create(CreateImportBatchRequest request, CancellationToken ct)
    {
        var template = await _db.CertificateTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.IsActive, ct);
        if (template is null) return BadRequest(new { error = "Unknown or inactive certificate template." });
        if (request.ExpectedRows is < 1 or > ImportFileReader.MaxRows) return BadRequest(new { error = $"Import between 1 and {ImportFileReader.MaxRows:N0} rows." });

        var fileName = Path.GetFileName(request.FileName ?? "").Trim();
        var batch = new ImportBatch
        {
            FileName = fileName.Length is > 0 and <= 200 ? fileName : "import",
            TemplateId = template.Id,
            ExpectedRows = request.ExpectedRows,
            CreatedByUserId = CurrentUserId(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        return Ok(await One(batch.Id, ct));
    }

    [HttpPost("{id:guid}/rows")]
    [Authorize(Roles = Importers)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<ImportRowsResponse>> AddRows(Guid id, ImportRowsRequest request, CancellationToken ct)
    {
        var batch = await _db.ImportBatches.Include(b => b.Template).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound(new { error = "Import not found." });
        var userId = CurrentUserId();
        if (batch.CreatedByUserId != userId) return Forbidden("Only the person who started this import can add rows to it.");
        if (batch.Status != ImportBatchStatus.Importing) return Conflict(new { error = "This import has already been sent for approval." });
        if (request.Rows is not { Count: > 0 }) return BadRequest(new { error = "No rows." });
        if (request.Rows.Count > MaxRowsPerChunk) return BadRequest(new { error = $"At most {MaxRowsPerChunk} rows per request." });

        var evaluated = await Evaluate(request.Rows, request.FirstRowNumber, request.Title, DateFormat(request.DateFormat), batch.Template.FieldSchemaJson, ct);
        var problems = new List<ImportRowResult>();
        var achievements = new Dictionary<string, Achievement>(StringComparer.Ordinal);
        var imported = 0;
        foreach (var row in evaluated)
        {
            if (row.Valid is not { } v)
            {
                problems.Add(new ImportRowResult(row.Row, row.Errors, row.Warnings));
                continue;
            }
            if (row.LikelyDuplicate && request.SkipLikelyDuplicates)
            {
                problems.Add(new ImportRowResult(row.Row, ["Skipped: already imported."], []));
                continue;
            }
            try
            {
                if (!achievements.TryGetValue(v.Title, out var achievement))
                {
                    achievement = await _issuance.GetOrCreateAchievementForTemplateAsync(batch.Template, v.Title, ct);
                    achievements[v.Title] = achievement;
                }
                await _issuance.CreatePendingAsync(achievement, v.RecipientName, v.RecipientEmail, v.FieldValuesJson, userId, null, ct,
                    new CredentialIssuanceService.PaperDetails(batch.Id, v.OriginalIssueDate, v.LegacyNumber, v.LegacyNumberKey, v.ExpiresOn));
                imported++;
            }
            catch (DbUpdateException ex)
            {
                // Most likely the paper number was imported meanwhile (unique index).
                _logger.LogInformation(ex, "Import row {Row} of batch {BatchId} not saved.", row.Row, batch.Id);
                _db.ChangeTracker.Clear();
                achievements.Clear();
                batch = await _db.ImportBatches.Include(b => b.Template).FirstAsync(b => b.Id == id, ct);
                problems.Add(new ImportRowResult(row.Row, [$"Not imported: paper certificate number {v.LegacyNumber} is already on a certificate."], []));
            }
        }
        return Ok(new ImportRowsResponse(imported, problems.Count(p => p.Errors.Count > 0), problems));
    }

    // Sends the batch for approval (or closes it if nothing was imported).
    [HttpPost("{id:guid}/finish")]
    [Authorize(Roles = Importers)]
    public async Task<ActionResult<ImportBatchResponse>> Finish(Guid id, CancellationToken ct)
    {
        var batch = await _db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound(new { error = "Import not found." });
        if (batch.CreatedByUserId != CurrentUserId()) return Forbidden("Only the person who started this import can finish it.");
        if (batch.Status != ImportBatchStatus.Importing) return Conflict(new { error = "This import has already been sent for approval." });

        var any = await _db.Credentials.AnyAsync(c => c.ImportBatchId == id, ct);
        batch.Status = any ? ImportBatchStatus.PendingApproval : ImportBatchStatus.Withdrawn;
        if (!any)
        {
            batch.DecidedByUserId = batch.CreatedByUserId;
            batch.DecidedAt = DateTimeOffset.UtcNow;
            batch.DecisionNote = "Nothing was imported.";
        }
        await _db.SaveChangesAsync(ct);
        return Ok(await One(id, ct));
    }

    // ---------- 4. Decide ----------

    // First call: confirmCount must equal the certificates waiting (typed by
    // the approver). Each call signs up to 100; call again until Remaining is 0.
    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = Deciders)]
    public async Task<ActionResult<ImportProgressResponse>> Approve(Guid id, ApproveImportRequest request, CancellationToken ct)
    {
        var batch = await _db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound(new { error = "Import not found." });
        var userId = CurrentUserId();
        if (batch.CreatedByUserId == userId) return Forbidden("You imported these certificates, so a different approver must approve them.");
        if (batch.Status is not (ImportBatchStatus.PendingApproval or ImportBatchStatus.Approving))
            return Conflict(new { error = $"This import is {Describe(batch.Status)}." });

        var pending = _db.Credentials.Where(c => c.ImportBatchId == id && c.Status == CredentialStatus.PendingApproval);
        if (batch.Status == ImportBatchStatus.PendingApproval)
        {
            var count = await pending.CountAsync(ct);
            if (request.ConfirmCount != count) return BadRequest(new { error = $"Type the number of certificates in this import ({count}) to confirm." });
            batch.Status = ImportBatchStatus.Approving;
            batch.DecidedByUserId = userId;
            batch.DecidedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        var ids = await pending.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Id).Take(ApproveChunk).ToListAsync(ct);
        int done = 0, failed = 0;
        foreach (var credentialId in ids)
        {
            // Loaded one at a time: after a failure the tracker is cleared,
            // which would detach anything loaded in advance.
            var credential = await _db.Credentials.Include(c => c.Achievement).FirstAsync(c => c.Id == credentialId, ct);
            try
            {
                await _issuance.ApproveAsync(credential, userId, userId, autoIssueApiClientId: null, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Approving imported credential {CredentialId} failed.", credentialId);
                _db.ChangeTracker.Clear();
                failed++;
            }
        }
        // Done with the first approval: issued, or waiting for final approval.
        var next = await _db.Credentials.AnyAsync(c => c.ImportBatchId == id && c.Status == CredentialStatus.PendingFinalApproval, ct)
            ? ImportBatchStatus.PendingFinalApproval : ImportBatchStatus.Approved;
        return Ok(await Progress(id, done, failed, next, ct, CredentialStatus.PendingApproval));
    }

    // Final approval of an import whose template needs it: an institution
    // admin who neither imported nor first-approved it, typing the number of
    // certificates to confirm. Each call issues up to 100; call again until
    // Remaining is 0.
    [HttpPost("{id:guid}/final-approve")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<ImportProgressResponse>> FinalApprove(Guid id, ApproveImportRequest request, CancellationToken ct)
    {
        var batch = await _db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound(new { error = "Import not found." });
        var userId = CurrentUserId();
        if (batch.CreatedByUserId == userId || batch.DecidedByUserId == userId)
            return Forbidden("You imported or first approved these certificates, so a different institution admin must give final approval.");
        if (batch.Status is not (ImportBatchStatus.PendingFinalApproval or ImportBatchStatus.FinalApproving))
            return Conflict(new { error = $"This import is {Describe(batch.Status)}." });

        var waiting = _db.Credentials.Where(c => c.ImportBatchId == id && c.Status == CredentialStatus.PendingFinalApproval);
        if (batch.Status == ImportBatchStatus.PendingFinalApproval)
        {
            var count = await waiting.CountAsync(ct);
            if (request.ConfirmCount != count) return BadRequest(new { error = $"Type the number of certificates in this import ({count}) to confirm." });
            batch.Status = ImportBatchStatus.FinalApproving;
            batch.FinalApprovedByUserId = userId;
            batch.FinalApprovedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        var ids = await waiting.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Id).Take(ApproveChunk).ToListAsync(ct);
        int done = 0, failed = 0;
        foreach (var credentialId in ids)
        {
            var credential = await _db.Credentials.Include(c => c.Achievement).FirstAsync(c => c.Id == credentialId, ct);
            try
            {
                await _issuance.FinalApproveAsync(credential, userId, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Final approval of imported credential {CredentialId} failed.", credentialId);
                _db.ChangeTracker.Clear();
                failed++;
            }
        }
        return Ok(await Progress(id, done, failed, ImportBatchStatus.Approved, ct, CredentialStatus.PendingFinalApproval));
    }

    // An approver rejects (note required), or the importer withdraws. Each
    // call rejects up to 250 waiting certificates; call again until Remaining is 0.
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<ImportProgressResponse>> Reject(Guid id, RejectImportRequest request, CancellationToken ct)
    {
        var batch = await _db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound(new { error = "Import not found." });
        var userId = CurrentUserId();
        var isImporter = batch.CreatedByUserId == userId;
        // First stage: an Approver (or the importer withdrawing). Final stage:
        // an institution admin who didn't import or first-approve it.
        var finalStage = batch.Status == ImportBatchStatus.PendingFinalApproval;
        var canDecide = finalStage
            ? User.IsInRole(SystemRole.InstitutionAdmin) && !isImporter && batch.DecidedByUserId != userId
            : User.IsInRole(SystemRole.Approver);
        if (!isImporter && !canDecide)
            return Forbidden(finalStage ? "A different institution admin gives or refuses final approval." : "Only an approver can reject an import.");
        if (finalStage && isImporter) return Forbidden("You imported these certificates; an institution admin decides the final approval.");

        if (batch.Status is ImportBatchStatus.Importing or ImportBatchStatus.PendingApproval or ImportBatchStatus.PendingFinalApproval)
        {
            if (!isImporter && batch.Status == ImportBatchStatus.Importing) return Conflict(new { error = "This import is still being uploaded." });
            var note = request.Note?.Trim() ?? "";
            if (!isImporter && note.Length == 0) return BadRequest(new { error = "Say why the import is rejected." });
            if (note.Length > 1000) return BadRequest(new { error = "The note must be at most 1000 characters." });
            batch.Status = ImportBatchStatus.Rejecting;
            batch.DecidedByUserId = userId;
            batch.DecidedAt = DateTimeOffset.UtcNow;
            batch.DecisionNote = note.Length > 0 ? note : null;
            await _db.SaveChangesAsync(ct);
        }
        else if (batch.Status != ImportBatchStatus.Rejecting)
        {
            return Conflict(new { error = $"This import is {Describe(batch.Status)}." });
        }

        var withdrawn = batch.DecidedByUserId == batch.CreatedByUserId;
        var reason = withdrawn ? "Import withdrawn by the importer." : $"Import rejected: {batch.DecisionNote}";
        var ids = await _db.Credentials.Where(c => c.ImportBatchId == id && (c.Status == CredentialStatus.PendingApproval || c.Status == CredentialStatus.PendingFinalApproval))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Id).Take(RejectChunk).ToListAsync(ct);
        int done = 0, failed = 0;
        foreach (var credentialId in ids)
        {
            var credential = await _db.Credentials.FirstAsync(c => c.Id == credentialId, ct);
            try
            {
                await _issuance.RejectAsync(credential, batch.DecidedByUserId!.Value, reason, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Rejecting imported credential {CredentialId} failed.", credentialId);
                _db.ChangeTracker.Clear();
                failed++;
            }
        }
        return Ok(await Progress(id, done, failed, withdrawn ? ImportBatchStatus.Withdrawn : ImportBatchStatus.Rejected, ct,
            CredentialStatus.PendingApproval, CredentialStatus.PendingFinalApproval));
    }

    // Remaining: the batch's certificates still in the given stage(s).
    private async Task<ImportProgressResponse> Progress(Guid id, int done, int failed, string finalStatus, CancellationToken ct, params CredentialStatus[] stage)
    {
        var remaining = await _db.Credentials.CountAsync(c => c.ImportBatchId == id && stage.Contains(c.Status), ct);
        var batch = await _db.ImportBatches.FirstAsync(b => b.Id == id, ct);
        if (remaining == 0 && batch.Status != finalStatus)
        {
            batch.Status = finalStatus;
            await _db.SaveChangesAsync(ct);
        }
        return new ImportProgressResponse(batch.Status, done, remaining, failed);
    }

    // ---------- Read ----------

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ImportBatchResponse>>> List(CancellationToken ct) =>
        Ok(await Load(_db.ImportBatches.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(100), ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ImportBatchDetailResponse>> Get(Guid id, CancellationToken ct)
    {
        var batch = (await Load(_db.ImportBatches.AsNoTracking().Where(b => b.Id == id), ct)).SingleOrDefault();
        if (batch is null) return NotFound(new { error = "Import not found." });
        var inBatch = _db.Credentials.AsNoTracking().Where(c => c.ImportBatchId == id);
        var sample = await inBatch.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(20)
            .Select(c => new ImportSampleRow(c.Id, c.RecipientName, c.RecipientEmail, c.Achievement.Title, c.OriginalIssueDate, c.LegacyCertificateNumber))
            .ToListAsync(ct);
        var byAchievement = (await inBatch.GroupBy(c => c.Achievement.Title).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .Select(g => new ReportCount(g.Key, g.Count)).OrderByDescending(g => g.Count).ToList();
        return Ok(new ImportBatchDetailResponse(batch, sample, byAchievement));
    }

    private async Task<ImportBatchResponse> One(Guid id, CancellationToken ct) =>
        (await Load(_db.ImportBatches.AsNoTracking().Where(b => b.Id == id), ct)).Single();

    private async Task<List<ImportBatchResponse>> Load(IQueryable<ImportBatch> query, CancellationToken ct)
    {
        var batches = await query.Select(b => new
        {
            b.Id, b.FileName, TemplateName = b.Template.Name, b.ExpectedRows, b.Status, b.CreatedByUserId,
            CreatedByName = b.CreatedByUser.FullName, b.CreatedAt,
            DecidedByName = b.DecidedByUser != null ? b.DecidedByUser.FullName : null, b.DecidedAt, b.DecisionNote, b.DecidedByUserId,
        }).ToListAsync(ct);
        var ids = batches.Select(b => b.Id).ToList();
        var counts = await _db.Credentials.AsNoTracking().Where(c => c.ImportBatchId != null && ids.Contains(c.ImportBatchId.Value))
            .GroupBy(c => new { c.ImportBatchId, c.Status })
            .Select(g => new { g.Key.ImportBatchId, g.Key.Status, Count = g.Count(), Min = g.Min(c => c.OriginalIssueDate), Max = g.Max(c => c.OriginalIssueDate) })
            .ToListAsync(ct);

        var userId = CurrentUserId();
        var isApprover = User.IsInRole(SystemRole.Approver);
        var isAdmin = User.IsInRole(SystemRole.InstitutionAdmin);
        return batches.Select(b =>
        {
            var mine = counts.Where(c => c.ImportBatchId == b.Id).ToList();
            int Count(Func<CredentialStatus, bool> match) => mine.Where(c => match(c.Status)).Sum(c => c.Count);
            var isImporter = b.CreatedByUserId == userId;
            return new ImportBatchResponse(
                b.Id, b.FileName, b.TemplateName, b.ExpectedRows, b.Status, b.CreatedByName, b.CreatedAt, b.DecidedByName, b.DecidedAt, b.DecisionNote,
                Count(s => s is CredentialStatus.PendingApproval or CredentialStatus.PendingFinalApproval),
                Count(s => s is CredentialStatus.Active or CredentialStatus.Suspended or CredentialStatus.Revoked),
                Count(s => s == CredentialStatus.Rejected),
                mine.Min(c => c.Min), mine.Max(c => c.Max),
                CanApprove: isApprover && !isImporter && b.Status is ImportBatchStatus.PendingApproval or ImportBatchStatus.Approving,
                CanWithdraw: isImporter && b.Status is ImportBatchStatus.Importing or ImportBatchStatus.PendingApproval,
                CanContinueImport: isImporter && b.Status == ImportBatchStatus.Importing,
                CanFinalApprove: isAdmin && !isImporter && b.DecidedByUserId != userId
                    && b.Status is ImportBatchStatus.PendingFinalApproval or ImportBatchStatus.FinalApproving);
        }).ToList();
    }

    // ---------- helpers ----------

    private static string DateFormat(string? format) => format is "MDY" or "YMD" ? format : "DMY";

    private static bool IsTableField(string schemaJson, string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);
        return doc.RootElement.EnumerateArray().Any(f =>
            f.GetProperty("key").GetString() == key && f.TryGetProperty("type", out var t) && t.GetString() == "table");
    }

    private static string Describe(string status) => status switch
    {
        ImportBatchStatus.Importing => "still being uploaded",
        ImportBatchStatus.PendingApproval => "waiting for approval",
        ImportBatchStatus.Approving => "being approved",
        ImportBatchStatus.PendingFinalApproval => "waiting for final approval",
        ImportBatchStatus.FinalApproving => "being given final approval",
        ImportBatchStatus.Rejecting => "being rejected",
        _ => status.ToLowerInvariant(),
    };

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, new { error = message });

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
