namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. Revoking is permanent, so it follows
// the same maker–checker rule as issuing: one person (or a Partner API key)
// asks, a different Approver/InstitutionAdmin approves, and only then is the
// credential revoked. At most one Pending request per credential (partial
// unique index). Decided requests are kept as history.
public class RevocationRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CredentialId { get; set; }
    public Credential Credential { get; set; } = default!;

    // See RevocationReasonCategory.
    public string ReasonCategory { get; set; } = default!;
    public string Note { get; set; } = default!;

    public Guid RequestedByUserId { get; set; }
    public ApplicationUser RequestedByUser { get; set; } = default!;
    public DateTimeOffset RequestedAt { get; set; }

    // See RevocationRequestStatus.
    public string Status { get; set; } = RevocationRequestStatus.Pending;

    public Guid? DecidedByUserId { get; set; }
    public ApplicationUser? DecidedByUser { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    // Why it was rejected (optional when approved).
    public string? DecisionNote { get; set; }
}

public static class RevocationRequestStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";
}

public static class RevocationReasonCategory
{
    public const string IssuedInError = "IssuedInError";
    public const string IncorrectDetails = "IncorrectDetails";
    public const string Misconduct = "Misconduct";
    public const string HolderRequest = "HolderRequest";
    public const string PartnerSystem = "PartnerSystem";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [IssuedInError, IncorrectDetails, Misconduct, HolderRequest, PartnerSystem, Other];

    public static string Label(string category) => category switch
    {
        IssuedInError => "Issued in error",
        IncorrectDetails => "Incorrect details",
        Misconduct => "Fraud or misconduct",
        HolderRequest => "Requested by the holder",
        PartnerSystem => "Requested by a partner system",
        _ => "Other",
    };
}
