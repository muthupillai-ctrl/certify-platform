using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Every public check of the institution's certificates (the Verification
// page's activity log): when, how, which certificate, and what the checker
// saw. Like VerificationEvent itself, nothing about WHO checked. Checks of
// revoked or suspended certificates, and codes that matched nothing
// (mistypes or forgeries), are "needs attention".
[ApiController]
[Route("api/v1/verification-activity")]
[Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
public class VerificationActivityController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int MaxExport = 10_000;

    // What a check found, as grouped on the page. "Found" is a code look-up
    // that matched (the verification page view that follows records the status).
    public static readonly IReadOnlyDictionary<string, string[]> ResultGroups = new Dictionary<string, string[]>
    {
        ["Valid"] = ["Active"],
        ["CodeMatched"] = ["Found"],
        ["Suspended"] = ["Suspended"],
        ["Revoked"] = ["Revoked"],
        ["Expired"] = ["Expired"],
        ["NotFound"] = ["NotFound"],
        ["Attention"] = ["Suspended", "Revoked", "NotFound"],
    };

    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenantContext;

    public VerificationActivityController(TenantDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    public async Task<ActionResult<VerificationActivityPageResponse>> List([FromQuery] VerificationActivityQuery request, CancellationToken ct)
    {
        if (request.Result is { } r && !ResultGroups.ContainsKey(r)) return BadRequest(new { error = $"Unknown result '{r}'." });

        var filtered = Filter(request, includeResult: false);
        // Counts by result with every other filter applied, for the result dropdown.
        var byResult = await filtered.GroupBy(v => v.Result).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var counts = ResultGroups.ToDictionary(g => g.Key, g => byResult.Where(x => g.Value.Contains(x.Key)).Sum(x => x.Count));

        var query = Filter(request, includeResult: true);
        var total = await query.CountAsync(ct);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var page = Math.Max(1, request.Page ?? 1);
        var items = await Rows(query).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new VerificationActivityPageResponse(items.Select(ToResponse).ToList(), total, page, pageSize,
            byResult.Sum(x => x.Count), counts));
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] VerificationActivityQuery request, CancellationToken ct)
    {
        if (request.Result is { } r && !ResultGroups.ContainsKey(r)) return BadRequest(new { error = $"Unknown result '{r}'." });
        var query = Filter(request, includeResult: true);
        if (await query.CountAsync(ct) > MaxExport)
            return BadRequest(new { error = $"At most {MaxExport:N0} rows at a time. Narrow the dates or filters." });

        var rows = await Rows(query).ToListAsync(ct);
        var csv = new CsvExport();
        csv.Row(["When (UTC)", "How", "Result", "Recipient", "Achievement", "Certificate code"]);
        foreach (var row in rows)
        {
            csv.Row([row.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"), ChannelLabel(row.Channel), ResultLabel(row.Result),
                row.RecipientName, row.AchievementTitle, row.VerificationCode is null ? null : VerificationCodes.Format(row.VerificationCode)]);
        }
        return File(csv.ToUtf8WithBom(), "text/csv; charset=utf-8", $"verifications-{_tenantContext.TenantSlug}-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    private IQueryable<VerificationEvent> Filter(VerificationActivityQuery request, bool includeResult)
    {
        var query = _db.VerificationEvents.AsNoTracking();
        if (request.From is { } from) query = query.Where(v => v.OccurredAt >= from);
        if (request.Before is { } before) query = query.Where(v => v.OccurredAt < before);
        if (!string.IsNullOrWhiteSpace(request.Channel)) query = query.Where(v => v.Channel == request.Channel);
        if (request.CredentialId is { } credentialId) query = query.Where(v => v.CredentialId == credentialId);
        if (includeResult && request.Result is { } result)
        {
            var values = ResultGroups[result];
            query = query.Where(v => values.Contains(v.Result));
        }
        return query;
    }

    private record Row(Guid Id, DateTimeOffset OccurredAt, string Channel, string Result, Guid? CredentialId,
        string? RecipientName, string? AchievementTitle, string? VerificationCode);

    // Newest first; a look-up that matched nothing has no certificate.
    private IQueryable<Row> Rows(IQueryable<VerificationEvent> query) =>
        from v in query
        join c in _db.Credentials on v.CredentialId equals c.Id into matched
        from c in matched.DefaultIfEmpty()
        orderby v.OccurredAt descending, v.Id
        select new Row(v.Id, v.OccurredAt, v.Channel, v.Result, v.CredentialId,
            c == null ? null : c.RecipientName, c == null ? null : c.Achievement.Title, c == null ? null : c.VerificationCode);

    private static VerificationActivityItemResponse ToResponse(Row r) => new(
        r.Id, r.OccurredAt, r.Channel, r.Result, r.CredentialId, r.RecipientName, r.AchievementTitle,
        r.VerificationCode is null ? null : VerificationCodes.Format(r.VerificationCode));

    public static string ChannelLabel(string channel) => channel switch
    {
        VerificationChannel.VerifyPage => "Verification page (link or QR)",
        VerificationChannel.CodeLookup => "Code look-up",
        VerificationChannel.CredentialDownload => "Digital credential downloaded",
        VerificationChannel.Api => "API",
        _ => channel,
    };

    public static string ResultLabel(string result) => result switch
    {
        "Active" => "Valid",
        "Found" => "Code matched",
        "NotFound" => "No such certificate",
        _ => result,
    };
}
