using Certyfi.Domain.Enums;

namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. Maker-checker workflow: drafted by a
// Registrar/InstitutionAdmin (RequestedByUser), decided by a DIFFERENT
// Approver/InstitutionAdmin (DecidedByUser) — enforced in
// CredentialsController, never by this entity itself. See the capability
// note 4.5.1/4.5.2/4.5.6 for the fuller design this is a first slice of.
public class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AchievementId { get; set; }
    public Achievement Achievement { get; set; } = default!;

    public string RecipientName { get; set; } = default!;
    public string RecipientEmail { get; set; } = default!;

    public CredentialStatus Status { get; set; } = CredentialStatus.PendingApproval;

    public Guid RequestedByUserId { get; set; }
    public ApplicationUser RequestedByUser { get; set; } = default!;

    // Checker decision — approve OR reject; DecidedBy/At covers both so
    // rejection is audited too, not just approval.
    // The first of two approvals, when the template needs final approval
    // (PendingFinalApproval); DecidedBy is then the final approver.
    public Guid? FirstApprovedByUserId { get; set; }
    public DateTimeOffset? FirstApprovedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public ApplicationUser? DecidedByUser { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? RejectionReason { get; set; }

    public DateTimeOffset? IssuedAt { get; set; }
    // Set at approval from IssuedAt + Achievement.ValidityPeriodMonths, if
    // the achievement has one. "Expired" is derived from this at
    // verification time — never a stored status.
    public DateTimeOffset? ExpiresOn { get; set; }

    public Guid? SuspendedByUserId { get; set; }
    public ApplicationUser? SuspendedByUser { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }

    public Guid? RevokedByUserId { get; set; }
    public ApplicationUser? RevokedByUser { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    // A fixed-shape JSON payload's SHA-256 hex, computed once at approval —
    // NOT itself a signature (see the "not real JCS/RFC 8785
    // canonicalization" caveat on ComputeCanonicalHash), just the exact
    // bytes Signature below actually signs.
    public string? CanonicalHash { get; set; }

    // Real cryptographic signature over CanonicalHash's bytes, produced at
    // approval by the issuing tenant's own key (see
    // ITenantSigningKeyProvider) — base64. Independently verifiable by
    // anyone: fetch the public key for SignatureKeyId from
    // VerificationController's signing-keys endpoint and check the
    // signature themselves, without trusting this platform's own verify
    // response. Null for anything approved before this existed, or for a
    // credential that isn't Active/Suspended/Revoked yet.
    public string? Signature { get; set; }
    public string? SignatureKeyId { get; set; }

    // JSON object of values matching Achievement.FieldSchemaJson at draft
    // time — see FieldSchemaValidator for the shape rules (every leaf value
    // is a string or null; a table-type key's value is a JSON array of flat
    // objects). Default "{}".
    public string FieldValuesJson { get; set; } = "{}";

    // Snapshot of Achievement.FieldSchemaJson at draft time, same rationale
    // as FieldValuesJson above — amending an achievement's field schema
    // later (AchievementsController.Update) must never change how an
    // already-drafted/issued credential renders on the verify page.
    // Default "[]".
    public string FieldSchemaJson { get; set; } = "[]";

    // The template's signatories when this certificate was created, as
    // [{"name": ..., "designation": ...}] (SignatorySnapshot). Null for
    // certificates from before signatories existed and for digitised paper
    // certificates (the paper carried its own signatures).
    public string? SignatoriesJson { get; set; }

    // Partner-supplied identifier for this credential in the partner's own
    // system (e.g. an LMS enrolment id). Unique per requesting user, which
    // for API-created credentials means per ApiClient — this is what makes
    // a partner's retried POST idempotent instead of issuing twice. Null for
    // credentials drafted in the admin UI.
    public string? ExternalReference { get; set; }

    // Short random code printed on the certificate (shown as XXXX-XXXX-XXXX)
    // so a verifier holding only the paper can look it up with the
    // institution code — see VerificationCodes. Stored without dashes,
    // unique per tenant. Random rather than sequential so codes can't be
    // walked to find other recipients' certificates.
    public string VerificationCode { get; set; } = default!;

    // Signed summary carried in the QR code's URL fragment for offline
    // checks ("<payload>.<signature>", both base64url) — see
    // OfflineProofSigner. Set at approval; null while pending/rejected.
    public string? OfflineProof { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- Imported paper certificates (see ImportBatch) ----
    // The date printed on the paper certificate. IssuedAt stays the day it
    // was digitised and signed: the signature and timestamps vouch for
    // that day only, never for the paper's.
    public DateOnly? OriginalIssueDate { get; set; }
    // The number printed on the paper certificate, as typed.
    public string? LegacyCertificateNumber { get; set; }
    // LegacyCertificateNumber upper-cased without spaces: unique among
    // certificates that aren't rejected, and what the public paper look-up matches.
    public string? LegacyNumberKey { get; set; }
    public Guid? ImportBatchId { get; set; }

    // ---- Personal data protection ----
    // Random per-certificate secret for the fingerprints of personal data in
    // the audit trail and the signed canonical hash. Deleting it (with the
    // details) is what makes an erasure final. Null only for certificates
    // created before this existed.
    public string? PersonalDataSalt { get; set; }
    // Set when the recipient's personal data was erased; such a certificate
    // no longer resolves publicly.
    public DateTimeOffset? PersonalDataErasedAt { get; set; }
    public ImportBatch? ImportBatch { get; set; }

    // ---- Student portal ----
    // The student this certificate belongs to (see Student); null when it
    // has no email, or after its personal data was erased.
    public Guid? StudentId { get; set; }
    public Student? Student { get; set; }
    // When the student paid (dummy payment) for a certificate that was
    // AwaitingPayment.
    public DateTimeOffset? PaidAt { get; set; }

    // Withdrawn by the requester before issue (Status Cancelled).
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    // AwaitingPayment only: pay by this time or it's cancelled (set from the
    // institution's PaymentDays when requested; null = no deadline).
    public DateTimeOffset? PaymentDueAt { get; set; }
    public DateTimeOffset? PaymentReminderSentAt { get; set; }
    // Cancelled by the payment deadline rather than by the requester.
    public bool CancelledForNonPayment { get; set; }

    // "Expired" is never stored, so the webhook dispatcher marks here that it
    // has told the requesting partner this certificate expired.
    public DateTimeOffset? ExpiryEventQueuedAt { get; set; }
}
