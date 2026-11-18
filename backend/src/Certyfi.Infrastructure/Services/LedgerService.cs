using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Appends one entry to the tenant's hash-chained ledger — see LedgerEntry
// for the tamper-evidence design and the AddLedgerEntries migration for the
// append-only trigger. This service is deliberately constructor-injected
// with the SAME per-request TenantDbContext the calling controller uses
// (standard AddScoped DI already guarantees one instance per request) —
// never construct it against a TenantDbContextFactory-built context, which
// deliberately opens an independent connection for out-of-request work
// (provisioning) and would silently defeat the locking below.
public class LedgerService : ILedgerService
{
    // Sentinel PreviousEntryHash for the very first entry in a tenant's
    // ledger — no real entry can produce this as an EntryHash (SHA-256 of
    // any real payload), so it's safe as a fixed genesis marker.
    public static readonly string GenesisHash = new('0', 64);

    private readonly TenantDbContext _db;

    public LedgerService(TenantDbContext db)
    {
        _db = db;
    }

    public async Task<LedgerEntry> AppendAsync(Guid credentialId, string eventType, Guid actorUserId,
        DateTimeOffset occurredAt, string eventDataJson, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "LedgerService.AppendAsync must run inside the same explicit transaction as the caller's credential mutation.");
        }

        // Transaction-scoped (not session-scoped) advisory lock — required
        // because connections are pooled; a session-scoped lock could leak
        // past its intended request if a pooled connection gets reused.
        // Serializes ALL ledger appends within this one tenant schema: a
        // hash chain is a single total order by construction, so two
        // entries genuinely cannot be appended "concurrently" without one
        // waiting to learn the other's hash first — this lock directly
        // implements what "chain" means, it isn't a workaround. See the
        // plan's concurrency section for why SELECT ... FOR UPDATE on the
        // last row is actually broken here, not just weaker.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('ledger_append:' || {_db.CurrentSchema}, 0))", ct);

        // Hash exactly what Postgres will store: timestamptz keeps whole
        // microseconds, .NET timestamps carry 100 ns ticks. Hashing the
        // unrounded value made stored entries fail the integrity check on
        // Linux (whose clock has sub-microsecond ticks) — see
        // LedgerIntegrityService for how entries written before this fix
        // are still verified.
        occurredAt = ToStoragePrecision(occurredAt);

        var previousHash = await _db.LedgerEntries
            .OrderByDescending(e => e.SequenceNumber)
            .Select(e => e.EntryHash)
            .FirstOrDefaultAsync(ct) ?? GenesisHash;

        var entry = new LedgerEntry
        {
            CredentialId = credentialId,
            EventType = eventType,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            EventDataJson = eventDataJson,
            PreviousEntryHash = previousHash,
            EntryHash = ComputeEntryHash(previousHash, eventType, credentialId, actorUserId, occurredAt, eventDataJson),
        };
        _db.LedgerEntries.Add(entry);
        return entry;
    }

    public async Task<LedgerEntry> AppendAsync(Credential credential, string eventType, Guid actorUserId,
        DateTimeOffset occurredAt, object eventData, object? personalData, CancellationToken ct = default)
    {
        var data = JsonSerializer.SerializeToNode(eventData)?.AsObject() ?? new JsonObject();
        string? personalJson = null;
        if (personalData is not null)
        {
            personalJson = JsonSerializer.Serialize(personalData);
            data["personalData"] = PersonalDataFingerprint.Of(PersonalDataFingerprint.EnsureSalt(credential), personalJson);
        }

        var entry = await AppendAsync(credential.Id, eventType, actorUserId, occurredAt, data.ToJsonString(), ct);
        if (personalJson is not null)
            _db.LedgerEntryDetails.Add(new LedgerEntryDetail { LedgerEntryId = entry.Id, CredentialId = credential.Id, DataJson = personalJson });
        return entry;
    }

    // Shared with LedgerController's integrity-check walk — must be the SAME
    // function in both places, or verification would flag every entry as
    // broken due to drift, not real tampering. Not real JCS/RFC 8785
    // canonicalization (same caveat as Credential.CanonicalHash) — a fixed,
    // hand-controlled shape is enough here since System.Text.Json serializes
    // anonymous-type properties in declaration order deterministically.
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    public static string ComputeEntryHash(string previousEntryHash, string eventType, Guid credentialId,
        Guid actorUserId, DateTimeOffset occurredAt, string eventDataJson)
    {
        var canonical = JsonSerializer.Serialize(new { previousEntryHash, eventType, credentialId, actorUserId, occurredAt, eventDataJson });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
