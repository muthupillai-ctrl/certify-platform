namespace Certyfi.Infrastructure.Services;

// Anchors every active tenant's ledger tip to Bitcoin via OpenTimestamps,
// and upgrades pending checkpoints once a Bitcoin confirmation becomes
// available. Two callers: LedgerAnchorBackgroundService (scheduled,
// automatic) and AnchoringController's manual admin endpoints (immediate,
// audited — same "deliberate, SuperAdmin-only action" pattern as
// TenantsController.MigrateSchemas, and what makes this directly
// integration-testable without waiting on the background timer).
public interface ILedgerCheckpointService
{
    // Returns the number of NEW checkpoints created (tenants with ledger
    // activity since their last checkpoint).
    Task<int> RunCheckpointStepAsync(string trigger = "Scheduled", CancellationToken ct = default);

    // Returns the number of Pending checkpoints that flipped to Confirmed
    // this run.
    Task<int> RunUpgradeStepAsync(string trigger = "Scheduled", CancellationToken ct = default);

    // Single-checkpoint versions for the SuperAdmin Anchoring page. Null
    // when the checkpoint doesn't exist in that tenant.
    Task<Domain.Entities.LedgerCheckpoint?> CheckOneAsync(Domain.Entities.Tenant tenant, Guid checkpointId, CancellationToken ct = default);
    Task<Domain.Entities.LedgerCheckpoint?> ReverifyAsync(Domain.Entities.Tenant tenant, Guid checkpointId, CancellationToken ct = default);

    Task<BitcoinBlockCheck> VerifyAgainstBitcoinBlockAsync(int height, byte[] expectedMerkleRootInternalOrder, CancellationToken ct);
}
