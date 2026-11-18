namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. Append-only, hash-chained audit trail of
// every Credential lifecycle event — EntryHash covers PreviousEntryHash plus
// this event's own data, so any retroactive edit or delete breaks the chain
// and is detectable by recomputing it (see LedgerController.Integrity).
// Enforced at the Postgres level by a trigger (see the AddLedgerEntries
// migration) that rejects UPDATE/DELETE outright — a Postgres superuser could
// still disable that trigger, tamper, and re-enable it; this is
// tamper-EVIDENCE, not an unforgeable external root of trust (that would
// need real per-institution signing + external anchoring, deliberately out
// of scope for this in-house ledger).
public class LedgerEntry
{
    // The real primary key — see TenantDbContext.OnModelCreating for why this
    // needs an explicit HasKey (EF's default convention would otherwise pick
    // Id below instead, silently breaking the append-order design).
    public long SequenceNumber { get; set; }

    // Opaque external id, matching every other entity's convention. NOT the
    // primary key.
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CredentialId { get; set; }
    public Credential Credential { get; set; } = default!;

    // "Requested" | "Approved" | "Rejected" | "Suspended" | "Reactivated" | "Revoked"
    public string EventType { get; set; } = default!;

    public Guid ActorUserId { get; set; }
    public ApplicationUser ActorUser { get; set; } = default!;

    public DateTimeOffset OccurredAt { get; set; }

    // A small, fixed-shape JSON snapshot of this event's relevant fields
    // (e.g. resulting status, reason text) — see CredentialsController for
    // the exact shape per event type.
    public string EventDataJson { get; set; } = default!;

    // 64 lowercase hex chars (SHA-256), or LedgerService.GenesisHash for the
    // very first entry in a tenant's ledger.
    public string PreviousEntryHash { get; set; } = default!;
    public string EntryHash { get; set; } = default!;
}
