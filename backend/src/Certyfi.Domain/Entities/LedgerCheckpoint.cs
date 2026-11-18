namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema, alongside LedgerEntry — a periodic
// external anchor of the ledger's current hash-chain tip via OpenTimestamps
// (Bitcoin), so a full database compromise (able to rewrite LedgerEntry
// rows consistently, see that entity's own comment on the limits of a
// purely in-house hash chain) can't rewrite history without it being
// independently detectable: once Confirmed, OtsProof ties CheckpointHash to
// a specific Bitcoin block that existed at a specific time, published
// entirely outside this database. See LedgerAnchorBackgroundService.
public class LedgerCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // The LedgerEntry.SequenceNumber this checkpoint covers (inclusive) —
    // everything up to and including this sequence number is committed to
    // by CheckpointHash. Unique: never re-checkpoint the same ledger tip.
    public long UpToSequenceNumber { get; set; }

    // = LedgerEntries[UpToSequenceNumber].EntryHash, re-hashed to 32 bytes
    // before submission (see LedgerAnchorBackgroundService) — the ledger
    // tip being anchored.
    public string CheckpointHash { get; set; } = default!;

    // "Pending" | "Confirmed" | "Failed"
    public string Status { get; set; } = "Pending";

    // Serialized DetachedTimestampFile bytes — the actual downloadable
    // .ots proof (see VerificationController's anchor-proof endpoint),
    // overwritten in place as UpgradeAsync polls bring in more data.
    public byte[] OtsProof { get; set; } = default!;

    // Populated only once Status flips to Confirmed — the real Bitcoin
    // block whose merkle root the checkpoint's proof resolves to.
    public int? BitcoinBlockHeight { get; set; }

    // True only once LedgerAnchorBackgroundService has independently
    // fetched that block's header from a public explorer and confirmed its
    // merkle root matches the proof's leaf message byte-for-byte — this is
    // what makes "Confirmed" a real cryptographic claim rather than just
    // "a proof blob exists".
    public bool MerkleRootVerified { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmedAt { get; set; }

    // Troubleshooting trail for the SuperAdmin Anchoring page: when the
    // proof was last checked with the calendars / block explorer, how many
    // times, and a one-line outcome ("Still pending at 3 of 4 calendars",
    // "finney: timeout", "Confirmed in block 968300, merkle root verified").
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int CheckAttempts { get; set; }
    public string? LastCheckResult { get; set; }
}
