using System.Security.Cryptography;
using System.Text;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Anchoring;
using Certyfi.Infrastructure.Anchoring.OpenTimestamps;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Read-only troubleshooting for the SuperAdmin Anchoring page. Nothing here
// changes a checkpoint: DiagnoseAsync upgrades a COPY of the stored proof so
// it can say what the calendars would return right now; applying that is
// LedgerCheckpointService.CheckOneAsync ("Check now").
public class AnchoringDiagnosticsService
{
    // Confirmation normally takes a few hours (a calendar aggregates for
    // ~1 hour, then waits for its transaction to be mined). A day with no
    // Bitcoin attestation means something is wrong.
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromHours(24);

    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbFactory;
    private readonly OpenTimestampsCalendarClient _calendarClient;
    private readonly ILedgerCheckpointService _checkpointService;
    private readonly HttpClient _blockExplorerHttp;

    public AnchoringDiagnosticsService(
        PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory, OpenTimestampsCalendarClient calendarClient,
        ILedgerCheckpointService checkpointService, IHttpClientFactory httpClientFactory)
    {
        _platformDb = platformDb;
        _tenantDbFactory = tenantDbFactory;
        _calendarClient = calendarClient;
        _checkpointService = checkpointService;
        _blockExplorerHttp = httpClientFactory.CreateClient("BlockExplorer");
    }

    public Task<Tenant?> FindTenantAsync(string slug, CancellationToken ct) =>
        _platformDb.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug.Trim().ToLowerInvariant(), ct);

    // `status`: all | pending | confirmed | failed | attention.
    public async Task<IReadOnlyList<CheckpointRow>> ListCheckpointsAsync(string? status, string? tenantSlug, int limit, CancellationToken ct)
    {
        var tenants = await _platformDb.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended)
            .Where(t => tenantSlug == null || t.Slug == tenantSlug)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var rows = new List<CheckpointRow>();
        foreach (var tenant in tenants)
        {
            await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            var query = tenantDb.LedgerCheckpoints.AsNoTracking();
            query = status switch
            {
                "pending" => query.Where(c => c.Status == "Pending"),
                "confirmed" => query.Where(c => c.Status == "Confirmed"),
                "failed" => query.Where(c => c.Status == "Failed"),
                _ => query,
            };
            // Projection leaves out OtsProof — it isn't needed for the list.
            var checkpoints = await query.OrderByDescending(c => c.CreatedAt).Take(limit)
                .Select(c => new { c.Id, c.UpToSequenceNumber, c.Status, c.CreatedAt, c.ConfirmedAt, c.BitcoinBlockHeight,
                    c.MerkleRootVerified, c.LastCheckedAt, c.CheckAttempts, c.LastCheckResult })
                .ToListAsync(ct);

            rows.AddRange(checkpoints.Select(c =>
            {
                var attention = AttentionReason(c.Status, c.CreatedAt, c.MerkleRootVerified, now);
                return new CheckpointRow(tenant.Slug, tenant.Name, c.Id, c.UpToSequenceNumber, c.Status, c.CreatedAt, c.ConfirmedAt,
                    c.BitcoinBlockHeight, c.MerkleRootVerified, c.LastCheckedAt, c.CheckAttempts, c.LastCheckResult, attention);
            }));
        }

        IEnumerable<CheckpointRow> result = rows;
        if (status == "attention") result = result.Where(r => r.AttentionReason is not null);
        return result.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public static string? AttentionReason(string status, DateTimeOffset createdAt, bool merkleRootVerified, DateTimeOffset now) => status switch
    {
        "Failed" => "The stored proof could not be read.",
        "Pending" when now - createdAt > OverdueAfter => $"Pending for {(int)(now - createdAt).TotalHours} hours; confirmation normally takes a few.",
        "Confirmed" when !merkleRootVerified => "Confirmed, but the Bitcoin block check did not pass.",
        _ => null,
    };

    public async Task<AnchoringCounts> CountAsync(CancellationToken ct)
    {
        var rows = await ListCheckpointsAsync(null, null, int.MaxValue, ct);
        return new AnchoringCounts(
            rows.Count(r => r.Status == "Pending"), rows.Count(r => r.Status == "Confirmed"), rows.Count(r => r.Status == "Failed"),
            rows.Count(r => r.AttentionReason is not null));
    }

    public async Task<ConnectivityReport> ProbeConnectivityAsync(CancellationToken ct)
    {
        var calendars = await _calendarClient.ProbeCalendarsAsync(ct);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var (tip, error) = await GetTipHeightAsync(ct);
        var explorer = new EndpointProbe(_blockExplorerHttp.BaseAddress?.ToString().TrimEnd('/') ?? "block explorer",
            tip is not null, tip is not null ? 200 : null, error, started.ElapsedMilliseconds);
        return new ConnectivityReport(calendars, explorer, tip);
    }

    public async Task<byte[]?> GetProofAsync(Tenant tenant, Guid checkpointId, CancellationToken ct)
    {
        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        return await tenantDb.LedgerCheckpoints.AsNoTracking()
            .Where(c => c.Id == checkpointId).Select(c => c.OtsProof).FirstOrDefaultAsync(ct);
    }

    public async Task<CheckpointDiagnosis?> DiagnoseAsync(Tenant tenant, Guid checkpointId, CancellationToken ct)
    {
        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var checkpoint = await tenantDb.LedgerCheckpoints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == checkpointId, ct);
        if (checkpoint is null) return null;

        var steps = new List<DiagnosticStep>();
        var now = DateTimeOffset.UtcNow;

        // 1. The ledger still holds the entry this checkpoint anchored.
        var entry = await tenantDb.LedgerEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.SequenceNumber == checkpoint.UpToSequenceNumber, ct);
        steps.Add(entry is null
            ? new DiagnosticStep("Ledger entry", StepStatus.Fail, $"Ledger entry #{checkpoint.UpToSequenceNumber} no longer exists.")
            : entry.EntryHash == checkpoint.CheckpointHash
                ? new DiagnosticStep("Ledger entry", StepStatus.Ok, $"Entry #{checkpoint.UpToSequenceNumber} still has the anchored hash.")
                : new DiagnosticStep("Ledger entry", StepStatus.Fail, $"Entry #{checkpoint.UpToSequenceNumber}'s hash differs from the anchored one: the ledger has changed since it was anchored."));

        // 2. The stored proof is readable and is for that hash.
        DetachedTimestampFile proof;
        try
        {
            proof = DetachedTimestampFile.Deserialize(checkpoint.OtsProof);
        }
        catch (Exception ex)
        {
            steps.Add(new DiagnosticStep("Proof file", StepStatus.Fail, $"The stored .ots proof can't be read: {ex.Message}"));
            return Finish(tenant, checkpoint, steps, Array.Empty<CalendarUpgradeOutcome>(), null, null);
        }
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint.CheckpointHash));
        steps.Add(proof.Timestamp.Msg.AsSpan().SequenceEqual(expectedDigest)
            ? new DiagnosticStep("Proof file", StepStatus.Ok, $"Readable ({checkpoint.OtsProof.Length} bytes) and commits to the checkpoint hash.")
            : new DiagnosticStep("Proof file", StepStatus.Fail, "The proof commits to a different digest than this checkpoint's hash."));

        // 3. What the calendars say right now — on a copy, so nothing is saved.
        var copy = DetachedTimestampFile.Deserialize(checkpoint.OtsProof);
        var storedBitcoin = LedgerCheckpointService.FindBitcoinAttestation(proof.Timestamp);
        var report = await _calendarClient.UpgradePendingAttestationsAsync(copy.Timestamp, ct);
        var pendingCount = proof.Timestamp.AllAttestations().Count(a => a.Attestation is PendingAttestation);
        if (report.Calendars.Count == 0)
        {
            steps.Add(new DiagnosticStep("Calendar servers", StepStatus.Info, "The proof has no pending attestations to ask about."));
        }
        else
        {
            var errors = report.Calendars.Count(c => c.Status == CalendarUpgradeStatus.Error);
            var status = errors == 0 ? StepStatus.Ok : errors == report.Calendars.Count ? StepStatus.Fail : StepStatus.Warn;
            steps.Add(new DiagnosticStep("Calendar servers", status,
                $"Asked {report.Calendars.Count} calendar(s) about their own commitments: {report.Summarize()}"));
        }

        // 4. Is there a Bitcoin attestation (stored, or available now)?
        var liveBitcoin = LedgerCheckpointService.FindBitcoinAttestation(copy.Timestamp);
        var bitcoin = storedBitcoin ?? liveBitcoin;
        if (storedBitcoin is not null)
        {
            steps.Add(new DiagnosticStep("Bitcoin attestation", StepStatus.Ok, $"The stored proof is attested in Bitcoin block {storedBitcoin.Value.Attestation.Height}."));
        }
        else if (liveBitcoin is not null)
        {
            steps.Add(new DiagnosticStep("Bitcoin attestation", StepStatus.Warn,
                $"Bitcoin has confirmed this (block {liveBitcoin.Value.Attestation.Height}), but the stored proof hasn't been updated. Use \"Check now\" to apply it."));
        }
        else
        {
            var age = now - checkpoint.CreatedAt;
            steps.Add(age > OverdueAfter
                ? new DiagnosticStep("Bitcoin attestation", StepStatus.Warn, $"No Bitcoin attestation after {(int)age.TotalHours} hours; this normally takes a few.")
                : new DiagnosticStep("Bitcoin attestation", StepStatus.Info, $"Not confirmed yet ({Math.Max(1, (int)age.TotalMinutes)} minutes old). This normally takes a few hours."));
        }

        // 5. Independent check against the real Bitcoin block.
        BitcoinBlockCheck? blockCheck = null;
        int? tip = null;
        if (bitcoin is not null)
        {
            var height = checked((int)bitcoin.Value.Attestation.Height);
            blockCheck = await _checkpointService.VerifyAgainstBitcoinBlockAsync(height, bitcoin.Value.LeafMsg, ct);
            (tip, _) = await GetTipHeightAsync(ct);
            var confirmations = tip is { } t ? $" ({t - height + 1} confirmations)" : "";
            steps.Add(blockCheck.Verified
                ? new DiagnosticStep("Bitcoin block", StepStatus.Ok, $"Block {height}'s merkle root matches the proof{confirmations}.")
                : blockCheck.ExplorerMerkleRootHex is null
                    ? new DiagnosticStep("Bitcoin block", StepStatus.Warn, $"Couldn't check block {height}: {blockCheck.Error}")
                    : new DiagnosticStep("Bitcoin block", StepStatus.Fail, $"Block {height}'s merkle root does NOT match the proof."));

            if (checkpoint.Status == "Confirmed" && !checkpoint.MerkleRootVerified && blockCheck.Verified)
            {
                steps.Add(new DiagnosticStep("Stored result", StepStatus.Warn,
                    "The block check passes now but is recorded as not verified. Use \"Re-verify\" to update it."));
            }
        }

        return Finish(tenant, checkpoint, steps, report.Calendars, blockCheck, tip, pendingCount);
    }

    private static CheckpointDiagnosis Finish(
        Tenant tenant, LedgerCheckpoint checkpoint, List<DiagnosticStep> steps, IReadOnlyList<CalendarUpgradeOutcome> calendars,
        BitcoinBlockCheck? blockCheck, int? tip, int pendingAttestations = 0)
    {
        var worst = steps.FirstOrDefault(s => s.Status == StepStatus.Fail) ?? steps.FirstOrDefault(s => s.Status == StepStatus.Warn);
        var summary = worst is null ? "Everything checks out." : $"{worst.Name}: {worst.Detail}";
        return new CheckpointDiagnosis(tenant.Slug, checkpoint.Id, checkpoint.Status, checkpoint.UpToSequenceNumber, checkpoint.CreatedAt,
            pendingAttestations, summary, steps, calendars, blockCheck, tip);
    }

    private async Task<(int? Tip, string? Error)> GetTipHeightAsync(CancellationToken ct)
    {
        try
        {
            var text = await _blockExplorerHttp.GetStringAsync("/api/blocks/tip/height", ct);
            return int.TryParse(text.Trim(), out var tip) ? (tip, null) : (null, "Unexpected response from the block explorer.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, ex.InnerException?.Message ?? ex.Message);
        }
    }
}

public static class StepStatus
{
    public const string Ok = "ok";
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

public record DiagnosticStep(string Name, string Status, string Detail);

public record CheckpointDiagnosis(
    string TenantSlug, Guid CheckpointId, string Status, long UpToSequenceNumber, DateTimeOffset CreatedAt,
    int PendingAttestations, string Summary, IReadOnlyList<DiagnosticStep> Steps,
    IReadOnlyList<CalendarUpgradeOutcome> Calendars, BitcoinBlockCheck? BitcoinBlock, int? BitcoinTipHeight);

public record CheckpointRow(
    string TenantSlug, string TenantName, Guid Id, long UpToSequenceNumber, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt, int? BitcoinBlockHeight, bool MerkleRootVerified,
    DateTimeOffset? LastCheckedAt, int CheckAttempts, string? LastCheckResult, string? AttentionReason);

public record AnchoringCounts(int Pending, int Confirmed, int Failed, int NeedsAttention);

public record ConnectivityReport(IReadOnlyList<EndpointProbe> Calendars, EndpointProbe BlockExplorer, int? BitcoinTipHeight);
