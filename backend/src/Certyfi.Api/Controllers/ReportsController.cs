using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Audit and accreditation reports for one period [from, before): the browser
// sends its own local midnights, like the Certificates page's date filter.
// Each report has a JSON view (summaries plus the first rows) and a CSV with
// every row (up to 10,000). Status changes and staff activity come from the
// tamper-evident ledger, so they show what actually happened, by whom.
[ApiController]
[Route("api/v1/reports")]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class ReportsController : ControllerBase
{
    private const int PreviewRows = 100;
    private const int MaxRows = 10_000;

    // Ledger events that change what the public sees, or ask to.
    private static readonly string[] StatusEvents =
        ["Paid", "Cancelled", "Suspended", "Reactivated", "RevocationRequested", "RevocationRequestRejected", "RevocationRequestWithdrawn", "Revoked"];
    // Everything a person (or Partner API key) does to a certificate.
    private static readonly string[] ActionEvents = ["Requested", "FirstApproved", "Approved", "Rejected", .. StatusEvents];

    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenantContext;

    public ReportsController(TenantDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    // ---------- Issuance ----------

    [HttpGet("issuance")]
    public async Task<ActionResult<IssuanceReportResponse>> Issuance([FromQuery] ReportPeriod period, CancellationToken ct)
    {
        if (Invalid(period) is { } error) return error;
        var names = await Names(ct);
        var issued = IssuedIn(period);

        var byAchievement = (await issued.GroupBy(c => c.Achievement.Title).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .Select(g => new ReportCount(g.Key, g.Count)).OrderByDescending(x => x.Count).ThenBy(x => x.Name).ToList();
        var byRequester = await issued.GroupBy(c => c.RequestedByUserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var byApprover = await issued.GroupBy(c => c.DecidedByUserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var rows = await IssuanceRows(issued).Take(PreviewRows).ToListAsync(ct);

        return Ok(new IssuanceReportResponse(
            byAchievement.Sum(x => x.Count),
            byAchievement,
            Named(byRequester.Select(x => (x.Key, x.Count)), names),
            Named(byApprover.Where(x => x.Key != null).Select(x => (x.Key!.Value, x.Count)), names),
            byRequester.GroupBy(x => names.IsApi(x.Key) ? "Partner API: " + names.Name(x.Key) : "Staff")
                .Select(g => new ReportCount(g.Key, g.Sum(x => x.Count))).OrderByDescending(x => x.Count).ToList(),
            rows.Select(r => ToIssuanceRow(r, names)).ToList()));
    }

    [HttpGet("issuance.csv")]
    public async Task<IActionResult> IssuanceCsv([FromQuery] ReportPeriod period, CancellationToken ct)
    {
        if (Invalid(period) is { } error) return error;
        var issued = IssuedIn(period);
        if (await issued.CountAsync(ct) > MaxRows) return TooMany();
        var names = await Names(ct);
        var rows = await IssuanceRows(issued).ToListAsync(ct);

        var csv = new CsvExport();
        csv.Row(["Issued (UTC)", "Recipient", "Email", "Achievement", "Certificate code", "Status now", "Requested by", "Source", "Approved by"]);
        foreach (var row in rows.Select(r => ToIssuanceRow(r, names)))
            csv.Row([Utc(row.IssuedAt), row.RecipientName, row.RecipientEmail, row.AchievementTitle, row.VerificationCode, row.Status,
                row.RequestedBy, row.Source, row.ApprovedBy]);
        return Csv(csv, "issuance", period);
    }

    private IQueryable<Credential> IssuedIn(ReportPeriod p) =>
        _db.Credentials.AsNoTracking().Where(c => c.IssuedAt != null && c.IssuedAt >= p.From && c.IssuedAt < p.Before);

    private record IssuanceRowData(DateTimeOffset IssuedAt, string RecipientName, string RecipientEmail, string AchievementTitle,
        string VerificationCode, CredentialStatus Status, DateTimeOffset? ExpiresOn, Guid RequestedByUserId, Guid? DecidedByUserId);

    private static IQueryable<IssuanceRowData> IssuanceRows(IQueryable<Credential> issued) =>
        issued.OrderBy(c => c.IssuedAt).ThenBy(c => c.Id).Select(c => new IssuanceRowData(
            c.IssuedAt!.Value, c.RecipientName, c.RecipientEmail, c.Achievement.Title, c.VerificationCode, c.Status, c.ExpiresOn,
            c.RequestedByUserId, c.DecidedByUserId));

    private static IssuanceReportRow ToIssuanceRow(IssuanceRowData r, PeopleNames names) => new(
        r.IssuedAt, r.RecipientName, r.RecipientEmail, r.AchievementTitle, VerificationCodes.Format(r.VerificationCode),
        r.Status == CredentialStatus.Active && r.ExpiresOn < DateTimeOffset.UtcNow ? "Expired" : r.Status.ToString(),
        names.Name(r.RequestedByUserId), names.IsApi(r.RequestedByUserId) ? "Partner API" : "Staff",
        r.DecidedByUserId is { } d ? names.Name(d) : null);

    // ---------- Status changes ----------

    [HttpGet("status-changes")]
    public async Task<ActionResult<StatusChangesReportResponse>> StatusChanges([FromQuery] ReportPeriod period, CancellationToken ct)
    {
        if (Invalid(period) is { } error) return error;
        var names = await Names(ct);
        var events = LedgerIn(period, StatusEvents);
        var counts = await events.GroupBy(e => e.EventType).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var rows = await LedgerRows(events).Take(PreviewRows).ToListAsync(ct);

        return Ok(new StatusChangesReportResponse(
            counts.Sum(x => x.Count),
            StatusEvents.Select(t => new ReportCount(EventLabel(t), counts.FirstOrDefault(x => x.Key == t)?.Count ?? 0)).ToList(),
            rows.Select(r => ToStatusRow(r, names)).ToList()));
    }

    [HttpGet("status-changes.csv")]
    public async Task<IActionResult> StatusChangesCsv([FromQuery] ReportPeriod period, CancellationToken ct)
    {
        if (Invalid(period) is { } error) return error;
        var events = LedgerIn(period, StatusEvents);
        if (await events.CountAsync(ct) > MaxRows) return TooMany();
        var names = await Names(ct);
        var rows = await LedgerRows(events).ToListAsync(ct);

        var csv = new CsvExport();
        csv.Row(["When (UTC)", "Change", "Recipient", "Achievement", "Certificate code", "By", "Asked by", "Reason / note"]);
        foreach (var row in rows.Select(r => ToStatusRow(r, names)))
            csv.Row([Utc(row.OccurredAt), row.Change, row.RecipientName, row.AchievementTitle, row.VerificationCode, row.By, row.RequestedBy, row.Reason]);
        return Csv(csv, "status-changes", period);
    }

    private static StatusChangeReportRow ToStatusRow(LedgerRowData r, PeopleNames names)
    {
        // Reasons and notes live off the chain (LedgerEntryDetail; gone once
        // erased); older entries still carry them in the event data.
        using var detail = Parse(r.DetailJson ?? "{}");
        using var data = Parse(r.EventDataJson);
        static string? From(JsonDocument? doc, string name) =>
            doc?.RootElement is { ValueKind: JsonValueKind.Object } o && o.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } p
                ? p.Value.GetString() : null;
        string? Field(string name) => From(detail, name) ?? From(data, name);

        var reason = r.EventType switch
        {
            "Cancelled" => Field("CancellationReason"),
            "Suspended" => Field("SuspensionReason"),
            "Revoked" => Field("RevocationReason"),
            "RevocationRequested" => Field("reasonCategory") is { } category ? $"{RevocationReasonCategory.Label(category)}: {Field("note")}" : Field("note"),
            _ => Field("note"),
        };
        var requestedBy = r.EventType == "Revoked" && Guid.TryParse(Field("requestedByUserId"), out var requester) ? names.Name(requester) : null;
        // Payments are the student's; deadline cancellations are automatic
        // (both recorded under the requesting key's account).
        var byDeadline = r.EventType == "Cancelled" && data?.RootElement is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("paymentDeadline", out var deadline) && deadline.ValueKind == JsonValueKind.True;
        var by = r.EventType == "Paid" ? "Student" : byDeadline ? "Payment deadline (automatic)" : names.Name(r.ActorUserId);
        return new StatusChangeReportRow(r.OccurredAt, EventLabel(r.EventType), r.RecipientName, r.AchievementTitle,
            VerificationCodes.Format(r.VerificationCode), by, requestedBy, reason);
    }

    // ---------- Staff activity (maker–checker audit) ----------

    [HttpGet("staff-activity")]
    public async Task<ActionResult<StaffActivityReportResponse>> StaffActivity([FromQuery] ReportPeriod period, CancellationToken ct)
    {
        if (Invalid(period) is { } error) return error;
        var names = await Names(ct);
        var byPerson = await LedgerIn(period, ActionEvents)
            .GroupBy(e => new { e.ActorUserId, e.EventType }).Select(g => new { g.Key.ActorUserId, g.Key.EventType, Count = g.Count() })
            .ToListAsync(ct);
        var people = byPerson.GroupBy(x => x.ActorUserId)
            .Select(g => new StaffActivityPerson(names.Name(g.Key), names.IsApi(g.Key),
                ActionEvents.ToDictionary(t => t, t => g.Where(x => x.EventType == t).Sum(x => x.Count))))
            .OrderByDescending(p => p.Counts.Values.Sum()).ThenBy(p => p.Name).ToList();

        // Two-person rule: every certificate approved in the period was
        // approved by someone other than who requested it, and every
        // revocation by someone other than who asked. (Rejections aren't
        // checked: an importer withdrawing their own import rejects its
        // certificates themselves, which is harmless.)
        var decided = await _db.LedgerEntries.AsNoTracking()
            .Where(e => e.EventType == "Approved" && e.OccurredAt >= period.From && e.OccurredAt < period.Before)
            .Select(e => new { e.CredentialId, e.Credential.RecipientName, e.Credential.RequestedByUserId, e.Credential.DecidedByUserId })
            .Distinct().ToListAsync(ct);
        var revocations = await _db.RevocationRequests.AsNoTracking()
            .Where(r => r.Status == RevocationRequestStatus.Approved && r.DecidedAt >= period.From && r.DecidedAt < period.Before)
            .Select(r => new { r.CredentialId, r.Credential.RecipientName, r.RequestedByUserId, r.DecidedByUserId })
            .ToListAsync(ct);

        return Ok(new StaffActivityReportResponse(
            people,
            decided.Count,
            decided.Where(d => d.DecidedByUserId == d.RequestedByUserId)
                .Select(d => new TwoPersonViolation(d.CredentialId, d.RecipientName, names.Name(d.RequestedByUserId), "Requested and approved by the same person")).ToList(),
            revocations.Count,
            revocations.Where(r => r.DecidedByUserId == r.RequestedByUserId)
                .Select(r => new TwoPersonViolation(r.CredentialId, r.RecipientName, names.Name(r.RequestedByUserId), "Revocation asked for and approved by the same person")).ToList()));
    }

    [HttpGet("staff-activity.csv")]
    public async Task<IActionResult> StaffActivityCsv([FromQuery] ReportPeriod period, CancellationToken ct)
    {
        if (Invalid(period) is { } error) return error;
        var events = LedgerIn(period, ActionEvents);
        if (await events.CountAsync(ct) > MaxRows) return TooMany();
        var names = await Names(ct);
        var rows = await LedgerRows(events).ToListAsync(ct);

        var csv = new CsvExport();
        csv.Row(["When (UTC)", "Person", "Action", "Recipient", "Achievement", "Certificate code"]);
        foreach (var r in rows)
            csv.Row([Utc(r.OccurredAt), names.Name(r.ActorUserId), EventLabel(r.EventType), r.RecipientName, r.AchievementTitle, VerificationCodes.Format(r.VerificationCode)]);
        return Csv(csv, "staff-activity", period);
    }

    // ---------- shared ----------

    private IQueryable<LedgerEntry> LedgerIn(ReportPeriod p, string[] types) =>
        _db.LedgerEntries.AsNoTracking().Where(e => types.Contains(e.EventType) && e.OccurredAt >= p.From && e.OccurredAt < p.Before);

    private record LedgerRowData(DateTimeOffset OccurredAt, string EventType, Guid ActorUserId, string EventDataJson, string? DetailJson,
        string RecipientName, string AchievementTitle, string VerificationCode);

    private IQueryable<LedgerRowData> LedgerRows(IQueryable<LedgerEntry> events) =>
        from e in events
        join d in _db.LedgerEntryDetails on e.Id equals d.LedgerEntryId into details
        from d in details.DefaultIfEmpty()
        orderby e.SequenceNumber
        select new LedgerRowData(
            e.OccurredAt, e.EventType, e.ActorUserId, e.EventDataJson, d == null ? null : d.DataJson,
            e.Credential.RecipientName, e.Credential.Achievement.Title, e.Credential.VerificationCode);

    public static string EventLabel(string type) => type switch
    {
        "Requested" => "Requested",
        "Paid" => "Paid by the student",
        "Cancelled" => "Cancelled",
        "FirstApproved" => "First approval (final approval next)",
        "Approved" => "Approved (issued)",
        "Rejected" => "Rejected",
        "Suspended" => "Suspended",
        "Reactivated" => "Reactivated",
        "RevocationRequested" => "Revocation asked for",
        "RevocationRequestRejected" => "Revocation request rejected",
        "RevocationRequestWithdrawn" => "Revocation request withdrawn",
        "Revoked" => "Revoked",
        _ => type,
    };

    // Display names: people by name, Partner API keys (service accounts) by key name.
    private sealed class PeopleNames(Dictionary<Guid, string> names, HashSet<Guid> api)
    {
        public string Name(Guid id) => names.GetValueOrDefault(id, "Unknown");
        public bool IsApi(Guid id) => api.Contains(id);
    }

    private async Task<PeopleNames> Names(CancellationToken ct)
    {
        var users = await _db.Users.AsNoTracking().Select(u => new { u.Id, u.FullName, u.IsServiceAccount }).ToListAsync(ct);
        var keys = await _db.ApiClients.AsNoTracking().ToDictionaryAsync(a => a.ServiceUserId, a => a.Name, ct);
        return new PeopleNames(
            users.ToDictionary(u => u.Id, u => u.IsServiceAccount && keys.TryGetValue(u.Id, out var key) ? key : u.FullName),
            users.Where(u => u.IsServiceAccount).Select(u => u.Id).ToHashSet());
    }

    private static List<ReportCount> Named(IEnumerable<(Guid Id, int Count)> rows, PeopleNames names) =>
        rows.Select(r => new ReportCount(names.IsApi(r.Id) ? "Partner API: " + names.Name(r.Id) : names.Name(r.Id), r.Count))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name).ToList();

    private ActionResult? Invalid(ReportPeriod p)
    {
        if (p.From is null || p.Before is null) return BadRequest(new { error = "Choose a period (from and before)." });
        if (p.From >= p.Before) return BadRequest(new { error = "The period ends before it starts." });
        return null;
    }

    private BadRequestObjectResult TooMany() => BadRequest(new { error = $"More than {MaxRows:N0} rows; choose a shorter period." });

    private FileContentResult Csv(CsvExport csv, string name, ReportPeriod p) =>
        File(csv.ToUtf8WithBom(), "text/csv; charset=utf-8", $"{name}-{_tenantContext.TenantSlug}-{p.From:yyyyMMdd}-{p.Before:yyyyMMdd}.csv");

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm");

    private static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json); } catch (JsonException) { return null; }
    }
}
