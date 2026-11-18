namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. One upload of paper certificates
// (a spreadsheet) turned into digital ones. The importer's certificates are
// created PendingApproval and belong to the batch; a DIFFERENT Approver or
// Institution Admin approves the batch as a whole (maker–checker, like any
// issue), which signs and activates them. Imported certificates can't be
// approved one by one.
public class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FileName { get; set; } = default!;
    public Guid TemplateId { get; set; }
    public CertificateTemplate Template { get; set; } = default!;
    // Rows in the file the importer chose to import (for progress).
    public int ExpectedRows { get; set; }

    public Guid CreatedByUserId { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }

    // See ImportBatchStatus.
    public string Status { get; set; } = ImportBatchStatus.Importing;

    public Guid? DecidedByUserId { get; set; }
    public ApplicationUser? DecidedByUser { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    // Final approval of the import (template's RequiresFinalApproval).
    public Guid? FinalApprovedByUserId { get; set; }
    public DateTimeOffset? FinalApprovedAt { get; set; }
}

// Importing -> PendingApproval -> Approving -> Approved, or -> Rejecting ->
// Rejected / Withdrawn. The -ing states are worked through in chunks by the
// page (each call signs or rejects the next few hundred), so a very large
// batch never needs one long request and can be resumed.
public static class ImportBatchStatus
{
    public const string Importing = "Importing";
    public const string PendingApproval = "PendingApproval";
    public const string Approving = "Approving";
    public const string Approved = "Approved";
    // Approved by an approver; an institution admin approves it finally.
    public const string PendingFinalApproval = "PendingFinalApproval";
    public const string FinalApproving = "FinalApproving";
    public const string Rejecting = "Rejecting";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";
}
