using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Anchoring;
using Certyfi.Infrastructure.Anchoring.OpenTimestamps;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Certyfi.Infrastructure.Services;

// Same "PlatformDbContext + TenantDbContextFactory, loop every active
// tenant" pattern as TenantProvisioningService.MigrateAllTenantSchemasAsync
// — this runs OUTSIDE any single tenant's HTTP request (across ALL
// tenants), so it builds each tenant's TenantDbContext directly via the
// factory rather than relying on the request-scoped ITenantContext DI
// registration.
public class LedgerCheckpointService : ILedgerCheckpointService
{
    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbFactory;
    private readonly OpenTimestampsCalendarClient _calendarClient;
    private readonly HttpClient _blockExplorerHttp;
    private readonly AnchoringRunStatus _runStatus;
    private readonly ILogger<LedgerCheckpointService> _logger;

    public LedgerCheckpointService(
        PlatformDbContext platformDb,
        TenantDbContextFactory tenantDbFactory,
        OpenTimestampsCalendarClient calendarClient,
        IHttpClientFactory httpClientFactory,
        AnchoringRunStatus runStatus,
        ILogger<LedgerCheckpointService> logger)
    {
        _platformDb = platformDb;
        _tenantDbFactory = tenantDbFactory;
        _calendarClient = calendarClient;
        _blockExplorerHttp = httpClientFactory.CreateClient("BlockExplorer");
        _runStatus = runStatus;
        _logger = logger;
    }

    private async Task<List<Tenant>> GetActiveTenantsAsync(CancellationToken ct) =>
        await _platformDb.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended)
            .ToListAsync(ct);

    public async Task<int> RunCheckpointStepAsync(string trigger = "Scheduled", CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var created = 0;
        var errors = new List<string>();
        var tenants = await GetActiveTenantsAsync(ct);
        foreach (var tenant in tenants)
        {
            await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            try
            {
                if (await CheckpointTenantAsync(tenantDb, ct))
                {
                    created++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checkpoint step failed for tenant schema {SchemaName}.", tenant.SchemaName);
                errors.Add($"{tenant.Slug}: {ex.Message}");
            }
        }
        _runStatus.RecordCheckpointRun(new AnchoringRun(trigger, startedAt, DateTimeOffset.UtcNow, tenants.Count, created, errors));
        return created;
    }

    private async Task<bool> CheckpointTenantAsync(TenantDbContext tenantDb, CancellationToken ct)
    {
        var latestEntry = await tenantDb.LedgerEntries.AsNoTracking()
            .OrderByDescending(e => e.SequenceNumber)
            .FirstOrDefaultAsync(ct);
        if (latestEntry is null)
        {
            return false; // nothing in the ledger yet
        }

        var lastCheckpoint = await tenantDb.LedgerCheckpoints.AsNoTracking()
            .OrderByDescending(c => c.UpToSequenceNumber)
            .FirstOrDefaultAsync(ct);
        if (lastCheckpoint is not null && lastCheckpoint.UpToSequenceNumber == latestEntry.SequenceNumber)
        {
            return false; // idempotency: no ledger activity since the last checkpoint
        }

        // OpenTimestamps calendars expect exactly 32 raw digest bytes, not
        // the 64-character hex string LedgerEntry.EntryHash stores — a
        // fresh SHA-256 over that hex string's UTF-8 bytes gives us that.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(latestEntry.EntryHash));

        Timestamp submitted;
        try
        {
            submitted = await _calendarClient.SubmitToAllAsync(digest, ct);
        }
        catch (Exception ex)
        {
            // Rethrown (after logging) so the run status shows which tenant
            // failed to submit — otherwise a calendar outage at checkpoint
            // time would be invisible on the Anchoring page.
            _logger.LogWarning(ex, "OpenTimestamps submission failed for tenant schema {SchemaName}.", tenantDb.CurrentSchema);
            throw new InvalidOperationException($"Submission to the calendar servers failed: {ex.Message}", ex);
        }

        var proofFile = new DetachedTimestampFile(new OpSha256(), submitted);
        tenantDb.LedgerCheckpoints.Add(new LedgerCheckpoint
        {
            UpToSequenceNumber = latestEntry.SequenceNumber,
            CheckpointHash = latestEntry.EntryHash,
            Status = "Pending",
            OtsProof = proofFile.Serialize(),
        });
        await tenantDb.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RunUpgradeStepAsync(string trigger = "Scheduled", CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var confirmed = 0;
        var errors = new List<string>();
        var tenants = await GetActiveTenantsAsync(ct);
        foreach (var tenant in tenants)
        {
            await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            try
            {
                confirmed += await UpgradeTenantCheckpointsAsync(tenantDb, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upgrade step failed for tenant schema {SchemaName}.", tenant.SchemaName);
                errors.Add($"{tenant.Slug}: {ex.Message}");
            }
        }
        _runStatus.RecordUpgradeRun(new AnchoringRun(trigger, startedAt, DateTimeOffset.UtcNow, tenants.Count, confirmed, errors));
        return confirmed;
    }

    public async Task<LedgerCheckpoint?> CheckOneAsync(Tenant tenant, Guid checkpointId, CancellationToken ct = default)
    {
        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var checkpoint = await tenantDb.LedgerCheckpoints.FirstOrDefaultAsync(c => c.Id == checkpointId, ct);
        if (checkpoint is null) return null;

        if (checkpoint.Status == "Pending")
        {
            await UpgradeCheckpointAsync(checkpoint, ct);
            await tenantDb.SaveChangesAsync(ct);
        }
        return checkpoint;
    }

    // For a Confirmed checkpoint whose merkle-root check didn't pass (often
    // just the block explorer being unreachable at the time): repeats only
    // the independent Bitcoin check, never contacts the calendars.
    public async Task<LedgerCheckpoint?> ReverifyAsync(Tenant tenant, Guid checkpointId, CancellationToken ct = default)
    {
        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var checkpoint = await tenantDb.LedgerCheckpoints.FirstOrDefaultAsync(c => c.Id == checkpointId, ct);
        if (checkpoint is null || checkpoint.Status != "Confirmed") return checkpoint;

        var proof = DetachedTimestampFile.Deserialize(checkpoint.OtsProof);
        var bitcoin = FindBitcoinAttestation(proof.Timestamp);
        if (bitcoin is null) return checkpoint;

        var check = await VerifyAgainstBitcoinBlockAsync(checked((int)bitcoin.Value.Attestation.Height), bitcoin.Value.LeafMsg, ct);
        checkpoint.MerkleRootVerified = check.Verified;
        RecordCheck(checkpoint, $"Re-verified block {bitcoin.Value.Attestation.Height}: {check.Describe()}");
        await tenantDb.SaveChangesAsync(ct);
        return checkpoint;
    }

    private async Task<int> UpgradeTenantCheckpointsAsync(TenantDbContext tenantDb, CancellationToken ct)
    {
        var pending = await tenantDb.LedgerCheckpoints
            .Where(c => c.Status == "Pending")
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var confirmed = 0;
        foreach (var checkpoint in pending)
        {
            if (await UpgradeCheckpointAsync(checkpoint, ct))
            {
                confirmed++;
            }
        }
        await tenantDb.SaveChangesAsync(ct);
        return confirmed;
    }

    private async Task<bool> UpgradeCheckpointAsync(LedgerCheckpoint checkpoint, CancellationToken ct)
    {
        DetachedTimestampFile existing;
        try
        {
            existing = DetachedTimestampFile.Deserialize(checkpoint.OtsProof);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stored OpenTimestamps proof for checkpoint {CheckpointId} is corrupt.", checkpoint.Id);
            checkpoint.Status = "Failed";
            RecordCheck(checkpoint, $"Stored proof could not be read: {ex.Message}");
            return false;
        }

        CalendarUpgradeReport report;
        try
        {
            report = await _calendarClient.UpgradePendingAttestationsAsync(existing.Timestamp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenTimestamps upgrade poll failed for checkpoint {CheckpointId}.", checkpoint.Id);
            RecordCheck(checkpoint, $"Upgrade failed: {ex.Message}");
            return false;
        }
        if (!report.Changed)
        {
            RecordCheck(checkpoint, report.Summarize()); // still pending, nothing new from any calendar yet
            return false;
        }

        checkpoint.OtsProof = new DetachedTimestampFile(existing.FileHashOp, existing.Timestamp).Serialize();

        var bitcoin = FindBitcoinAttestation(existing.Timestamp);
        if (bitcoin is null)
        {
            RecordCheck(checkpoint, report.Summarize() + " No Bitcoin attestation yet.");
            return false; // upgraded, but still no Bitcoin confirmation yet
        }

        var height = checked((int)bitcoin.Value.Attestation.Height);
        var check = await VerifyAgainstBitcoinBlockAsync(height, bitcoin.Value.LeafMsg, ct);

        checkpoint.BitcoinBlockHeight = height;
        checkpoint.MerkleRootVerified = check.Verified;
        checkpoint.Status = "Confirmed";
        checkpoint.ConfirmedAt = DateTimeOffset.UtcNow;
        RecordCheck(checkpoint, $"Confirmed in block {height}: {check.Describe()}");
        return true;
    }

    private static void RecordCheck(LedgerCheckpoint checkpoint, string result)
    {
        checkpoint.LastCheckedAt = DateTimeOffset.UtcNow;
        checkpoint.CheckAttempts++;
        checkpoint.LastCheckResult = result.Length > 1000 ? result[..1000] : result;
    }

    public static (BitcoinBlockHeaderAttestation Attestation, byte[] LeafMsg)? FindBitcoinAttestation(Timestamp timestamp)
    {
        foreach (var (leafMsg, attestation) in timestamp.AllAttestations())
        {
            if (attestation is BitcoinBlockHeaderAttestation btc)
            {
                return (btc, leafMsg);
            }
        }
        return null;
    }

    // Independently fetches the real Bitcoin block at `height` from a
    // public explorer (Blockstream's Esplora API) and checks its merkle
    // root against `expectedMerkleRootInternalOrder` — the leaf message
    // OpenTimestamps' merkle path resolves to. This is what turns "a
    // calendar server says it's confirmed" into an actual,
    // independently-checkable cryptographic fact rather than a trusted
    // claim.
    //
    // Esplora's JSON `merkle_root` field is in the conventional
    // byte-reversed "display" hex order (same convention as block hash and
    // txid fields across all standard Bitcoin JSON APIs), while
    // OpenTimestamps' attestation leaf message is the merkle root in
    // internal (non-reversed) byte order — confirmed against the reference
    // `ots` CLI's own verify path (otsclient/cmds.py uses `b2lx`, i.e.
    // "bytes to lower-case hex, reversed", specifically to print the
    // internal-order attestation message in the same order a block explorer
    // would display it), not assumed.
    public async Task<BitcoinBlockCheck> VerifyAgainstBitcoinBlockAsync(int height, byte[] expectedMerkleRootInternalOrder, CancellationToken ct)
    {
        var expectedDisplayHex = Convert.ToHexString(expectedMerkleRootInternalOrder.Reverse().ToArray()).ToLowerInvariant();
        try
        {
            var blockHash = (await _blockExplorerHttp.GetStringAsync($"/api/block-height/{height}", ct)).Trim();
            var blockJson = await _blockExplorerHttp.GetStringAsync($"/api/block/{blockHash}", ct);

            using var doc = JsonDocument.Parse(blockJson);
            var merkleRootHex = doc.RootElement.GetProperty("merkle_root").GetString();
            if (merkleRootHex is null)
            {
                return new BitcoinBlockCheck(false, blockHash, expectedDisplayHex, null, "Block explorer returned no merkle_root.");
            }

            var merkleRootDisplayOrder = Convert.FromHexString(merkleRootHex);
            var merkleRootInternalOrder = merkleRootDisplayOrder.Reverse().ToArray();
            var matches = merkleRootInternalOrder.AsSpan().SequenceEqual(expectedMerkleRootInternalOrder);
            return new BitcoinBlockCheck(matches, blockHash, expectedDisplayHex, merkleRootHex.ToLowerInvariant(),
                matches ? null : "The block's merkle root does not match the proof.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Bitcoin block header verification failed for height {Height}.", height);
            return new BitcoinBlockCheck(false, null, expectedDisplayHex, null, $"Block explorer unreachable or returned an error: {ex.Message}");
        }
    }
}

// Verified is false both for a real mismatch (Error says so, both roots
// shown) and for "couldn't reach the explorer" (ExplorerMerkleRootHex null)
// — the Anchoring page tells them apart, and only the second is fixed by
// re-verifying.
public record BitcoinBlockCheck(bool Verified, string? BlockHash, string ExpectedMerkleRootHex, string? ExplorerMerkleRootHex, string? Error)
{
    public string Describe() => Verified ? "merkle root verified against the block explorer." : $"NOT verified — {Error}";
}
