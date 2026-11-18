using Certyfi.Domain.Entities;

namespace Certyfi.Infrastructure.Services;

public interface ILedgerService
{
    // Must run inside a transaction already opened by the caller, on the
    // same TenantDbContext instance/connection — see LedgerService for why.
    Task<LedgerEntry> AppendAsync(Guid credentialId, string eventType, Guid actorUserId,
        DateTimeOffset occurredAt, string eventDataJson, CancellationToken ct = default);

    // Same, with the event's personal data (names, emails, free-text reasons)
    // kept out of the chain: the entry stores only a salted fingerprint of
    // it, and the data itself goes to LedgerEntryDetails (erasable).
    // eventData must hold no personal data. The credential must be tracked.
    Task<LedgerEntry> AppendAsync(Credential credential, string eventType, Guid actorUserId,
        DateTimeOffset occurredAt, object eventData, object? personalData, CancellationToken ct = default);
}
