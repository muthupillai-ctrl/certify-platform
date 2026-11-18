using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Certyfi.Api.Controllers;

// The institution's state and trends at a glance (certificates plan phase
// 7): what needs attention, issuing activity and public verifications.
// Everything is grouped counts computed in SQL; the only per-certificate
// rows are the five most-verified. Months are the caller's calendar months
// (tz = the browser's IANA zone, resolved by .NET), so "this month" matches what the
// user sees on the Certificates page. Cached a minute per tenant and zone
// (the numbers are institution-wide, the same for every staff member), but
// only while the ledger hasn't moved: every certificate change (requested,
// approved, paid, revoked...) appends to it, so a new certificate shows at
// once. Verifications, imports and anchoring catch up within the minute.
[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
public class DashboardController : ControllerBase
{
    private const int Months = 12;
    private const int TopAchievements = 10;
    private const int TopVerified = 5;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    // Bitcoin confirmation usually takes a few hours; a day means stuck.
    private static readonly TimeSpan AnchorDelayed = TimeSpan.FromHours(24);

    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IMemoryCache _cache;

    public DashboardController(TenantDbContext db, ITenantContext tenantContext, IMemoryCache cache)
    {
        _db = db;
        _tenantContext = tenantContext;
        _cache = cache;
    }

    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> Get([FromQuery] string? tz, CancellationToken ct)
    {
        var zoneId = string.IsNullOrWhiteSpace(tz) ? "UTC" : tz.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
            return BadRequest(new { error = "Unknown time zone." });

        var ledgerHead = await _db.LedgerEntries.MaxAsync(e => (long?)e.SequenceNumber, ct) ?? 0;
        var key = $"dashboard:{_tenantContext.TenantId}:{zoneId}:{ledgerHead}";
        if (_cache.TryGetValue(key, out DashboardResponse? cached) && cached is not null) return Ok(cached);

        var response = await Build(zoneId, zone, ct);
        _cache.Set(key, response, CacheFor);
        return Ok(response);
    }

    private async Task<DashboardResponse> Build(string zoneId, TimeZoneInfo zone, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var thisMonth = new DateTime(localNow.Year, localNow.Month, 1);
        var firstMonth = thisMonth.AddMonths(-(Months - 1));
        var rangeStart = LocalToUtc(firstMonth, zone);
        var thisMonthStart = LocalToUtc(thisMonth, zone);
        var monthKeys = Enumerable.Range(0, Months).Select(i => firstMonth.AddMonths(i)).ToList();
        // Each month's first instant, worked out here with .NET's zone rules
        // (so browser aliases like Asia/Calcutta work, whatever tz data the
        // database has). A row's month = how many of these it has passed.
        var monthStarts = monthKeys.Select(m => LocalToUtc(m, zone)).ToArray();

        var credentials = _db.Credentials.AsNoTracking();

        // ---- Needs attention ----
        var pending = await credentials.Where(c => c.Status == CredentialStatus.PendingApproval)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Oldest = g.Min(c => c.CreatedAt) })
            .FirstOrDefaultAsync(ct);
        var pendingFinal = await credentials.CountAsync(c => c.Status == CredentialStatus.PendingFinalApproval, ct);
        var controlRequests = await _db.ControlRequests.CountAsync(r => r.Status == ControlRequestStatus.Pending, ct);
        var rejectedSince = now.AddDays(-30);
        var rejectedRecently = await credentials.CountAsync(c =>
            c.Status == CredentialStatus.Rejected && (c.DecidedAt ?? c.CreatedAt) >= rejectedSince, ct);
        var expiringUntil = now.AddDays(30);
        var expiringSoon = await credentials.CountAsync(c =>
            c.Status == CredentialStatus.Active && c.ExpiresOn != null && c.ExpiresOn >= now && c.ExpiresOn <= expiringUntil, ct);
        var delayedBefore = now - AnchorDelayed;
        var anchorFailed = await _db.LedgerCheckpoints.CountAsync(c => c.Status == "Failed", ct);
        var importsPending = await _db.ImportBatches.CountAsync(b => b.Status == Certyfi.Domain.Entities.ImportBatchStatus.PendingApproval, ct);
        var revocationRequests = await _db.RevocationRequests.CountAsync(r => r.Status == Certyfi.Domain.Entities.RevocationRequestStatus.Pending, ct);
        var anchorDelayed = await _db.LedgerCheckpoints.CountAsync(c => c.Status == "Pending" && c.CreatedAt < delayedBefore, ct);
        var dueSoonUntil = now.AddDays(3);
        var unpaid = await credentials.Where(c => c.Status == CredentialStatus.AwaitingPayment)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), DueSoon = g.Count(c => c.PaymentDueAt != null && c.PaymentDueAt <= dueSoonUntil) })
            .FirstOrDefaultAsync(ct);
        var cancelledUnpaid = await credentials.CountAsync(c =>
            c.Status == CredentialStatus.Cancelled && c.CancelledForNonPayment && c.CancelledAt >= rejectedSince, ct);
        // Keys with a webhook whose most recent finished call failed.
        var webhooksFailing = await _db.ApiClients.AsNoTracking()
            .Where(a => a.WebhookUrl != null && a.RevokedAt == null)
            .CountAsync(a => _db.WebhookDeliveries
                .Where(w => w.ApiClientId == a.Id && w.State != Certyfi.Domain.Entities.WebhookDeliveryState.Pending && w.State != Certyfi.Domain.Entities.WebhookDeliveryState.Sending)
                .OrderByDescending(w => w.LastAttemptAt)
                .Select(w => w.State == Certyfi.Domain.Entities.WebhookDeliveryState.Stopped || w.State == Certyfi.Domain.Entities.WebhookDeliveryState.Failed)
                .FirstOrDefault(), ct);
        var students = await _db.Students.CountAsync(ct);
        var studentsSignedUp = await _db.Students.CountAsync(s => s.PasswordHash != null, ct);

        // ---- Totals ----
        var active = await credentials.CountAsync(c =>
            c.Status == CredentialStatus.Active && (c.ExpiresOn == null || c.ExpiresOn >= now), ct);

        // ---- Issued: by month, achievement and source (last 12 months) ----
        var issued = credentials.Where(c => c.IssuedAt != null && c.IssuedAt >= rangeStart);
        var issuedByMonth = await issued
            .GroupBy(c => monthStarts.Count(start => start <= c.IssuedAt!.Value))
            .Select(g => new { Month = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byAchievement = await issued
            .GroupBy(c => new { c.AchievementId, c.Achievement.Title })
            .Select(g => new { g.Key.AchievementId, g.Key.Title, Count = g.Count() })
            .OrderByDescending(g => g.Count).ThenBy(g => g.Title).Take(TopAchievements)
            .ToListAsync(ct);
        var byRequester = await issued
            .GroupBy(c => new { c.RequestedByUserId, c.RequestedByUser.IsServiceAccount })
            .Select(g => new { g.Key.RequestedByUserId, g.Key.IsServiceAccount, Count = g.Count() })
            .ToListAsync(ct);
        var apiClients = await _db.ApiClients.AsNoTracking()
            .Select(a => new { a.Id, a.Name, a.ServiceUserId }).ToListAsync(ct);
        var bySource = new List<DashboardSourceCount>();
        var staff = byRequester.Where(r => !r.IsServiceAccount).Sum(r => r.Count);
        if (staff > 0) bySource.Add(new DashboardSourceCount("Staff", null, "Staff", staff));
        foreach (var client in apiClients)
        {
            var count = byRequester.Where(r => r.IsServiceAccount && r.RequestedByUserId == client.ServiceUserId).Sum(r => r.Count);
            if (count > 0) bySource.Add(new DashboardSourceCount("Api", client.Id, client.Name, count));
        }
        bySource.Sort((a, b) => b.Count.CompareTo(a.Count));

        // ---- Verifications (last 12 months) ----
        var verifications = _db.VerificationEvents.AsNoTracking().Where(v => v.OccurredAt >= rangeStart);
        var verificationsByMonth = await verifications
            .GroupBy(v => monthStarts.Count(start => start <= v.OccurredAt))
            .Select(g => new { Month = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byChannel = await verifications
            .GroupBy(v => v.Channel)
            .Select(g => new DashboardChannelCount(g.Key, g.Count()))
            .ToListAsync(ct);
        var unmatchedLookups = await verifications.CountAsync(v => v.CredentialId == null, ct);
        var verificationsThisMonth = await verifications.CountAsync(v => v.OccurredAt >= thisMonthStart, ct);
        var topIds = await verifications
            .Where(v => v.CredentialId != null)
            .GroupBy(v => v.CredentialId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).ThenBy(g => g.Id).Take(TopVerified)
            .ToListAsync(ct);
        var ids = topIds.Select(t => t.Id).ToList();
        var topNames = await credentials.Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.RecipientName, c.Achievement.Title })
            .ToDictionaryAsync(c => c.Id, ct);
        var mostVerified = topIds
            .Where(t => topNames.ContainsKey(t.Id))
            .Select(t => new DashboardTopVerified(t.Id, topNames[t.Id].RecipientName, topNames[t.Id].Title, t.Count))
            .ToList();

        // Bucket n (1..12) = the n-th month.
        List<DashboardMonthCount> Fill(IEnumerable<(int Bucket, int Count)> rows)
        {
            var lookup = rows.ToDictionary(r => r.Bucket, r => r.Count);
            return monthKeys.Select((m, i) => new DashboardMonthCount($"{m:yyyy-MM}", lookup.GetValueOrDefault(i + 1))).ToList();
        }
        var issuedMonths = Fill(issuedByMonth.Select(r => (r.Month, r.Count)));

        return new DashboardResponse(
            now,
            zoneId,
            $"{firstMonth:yyyy-MM-dd}",
            new DashboardAttention(pending?.Count ?? 0, pending?.Oldest, rejectedRecently, expiringSoon, anchorFailed, anchorDelayed, revocationRequests, importsPending,
                unpaid?.Count ?? 0, unpaid?.DueSoon ?? 0, cancelledUnpaid, webhooksFailing, pendingFinal, controlRequests),
            new DashboardTotals(active, issuedMonths[^1].Count, issuedMonths[^2].Count, verificationsThisMonth, students, studentsSignedUp),
            issuedMonths,
            byAchievement.Select(a => new DashboardNamedCount(a.AchievementId, a.Title, a.Count)).ToList(),
            bySource,
            Fill(verificationsByMonth.Select(r => (r.Month, r.Count))),
            byChannel.OrderByDescending(c => c.Count).ToList(),
            mostVerified,
            unmatchedLookups);
    }

    // A local wall-clock midnight as a UTC instant (Npgsql only writes UTC
    // offsets to timestamptz). DST-safe: an invalid local
    // time can't occur at midnight on the 1st in any zone we'd meet, but
    // ambiguous ones resolve to standard time).
    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone) =>
        new DateTimeOffset(local, zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified))).ToUniversalTime();
}
