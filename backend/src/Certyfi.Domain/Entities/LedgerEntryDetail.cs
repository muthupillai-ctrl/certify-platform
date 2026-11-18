namespace Certyfi.Domain.Entities;

// The personal part of one audit-trail entry (names, emails, free-text
// reasons), kept OUTSIDE the hash chain. The chained LedgerEntry holds only
// a salted fingerprint of this JSON (see PersonalDataFingerprint), so:
//  - the chain still proves the details weren't changed (the integrity check
//    recomputes the fingerprint), and
//  - the details can be erased (data-protection erasure requests) by
//    deleting this row and the credential's salt, without breaking the
//    chain; what stays in it can't be linked back to the person.
public class LedgerEntryDetail
{
    // LedgerEntry.Id of the entry these details belong to.
    public Guid LedgerEntryId { get; set; }
    public Guid CredentialId { get; set; }
    public string DataJson { get; set; } = default!;
}
