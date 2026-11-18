namespace Certyfi.Infrastructure.Services;

// Singleton, in-memory record of the anchoring job's recent runs for the
// SuperAdmin Anchoring page ("is the job running, and did its last run
// work?"). Deliberately not persisted: it describes THIS process — after a
// restart it's empty until the first run, which is itself useful to see.
// Per-checkpoint history that must survive restarts lives on
// LedgerCheckpoint (LastCheckedAt / LastCheckResult).
public class AnchoringRunStatus
{
    private readonly object _lock = new();
    private AnchoringRun? _lastCheckpointRun;
    private AnchoringRun? _lastUpgradeRun;

    public DateTimeOffset ProcessStartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? BackgroundJobStartedAt { get; private set; }
    public TimeSpan CheckpointInterval { get; private set; }
    public TimeSpan UpgradeInterval { get; private set; }

    public AnchoringRun? LastCheckpointRun { get { lock (_lock) return _lastCheckpointRun; } }
    public AnchoringRun? LastUpgradeRun { get { lock (_lock) return _lastUpgradeRun; } }

    public void MarkBackgroundJobStarted(TimeSpan checkpointInterval, TimeSpan upgradeInterval)
    {
        BackgroundJobStartedAt = DateTimeOffset.UtcNow;
        CheckpointInterval = checkpointInterval;
        UpgradeInterval = upgradeInterval;
    }

    public void RecordCheckpointRun(AnchoringRun run) { lock (_lock) _lastCheckpointRun = run; }
    public void RecordUpgradeRun(AnchoringRun run) { lock (_lock) _lastUpgradeRun = run; }
}

// Trigger: "Scheduled" (background job) or "Manual" (SuperAdmin button).
// Count: checkpoints created, or confirmed. TenantErrors: one line per
// tenant whose step threw.
public record AnchoringRun(
    string Trigger, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int TenantsProcessed, int Count,
    IReadOnlyList<string> TenantErrors);
