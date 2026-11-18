namespace Certyfi.Domain.Enums;

// PendingApproval/Rejected are pre-issuance (maker-checker) states — not in
// the capability note's own vocabulary, added for that workflow step. Once
// approved a credential is Active, and can move to Suspended (reversible)
// or Revoked (permanent) per the note's lifecycle. "Expired" is
// deliberately NOT a member here — it's computed live from Credential's
// ExpiresOn at verification time, never stored, per the note's "status
// resolved live... never cached in the credential artefact itself."
//
// AwaitingPayment comes before PendingApproval for certificates requested
// through a Partner API key that requires payment: the student pays in the
// student portal, then it goes to an approver as usual.
//
// Cancelled: withdrawn by whoever requested it (a partner app) before it
// was issued — from AwaitingPayment or PendingApproval. Final, like Rejected.
public enum CredentialStatus
{
    PendingApproval = 0,
    Rejected = 1,
    Active = 2,
    Suspended = 3,
    Revoked = 4,
    AwaitingPayment = 5,
    Cancelled = 6,
    // Approved once, waiting for an institution admin's final approval (the
    // template's RequiresFinalApproval). Shown to partners and students as
    // PendingApproval: it's still being approved.
    PendingFinalApproval = 7
}
