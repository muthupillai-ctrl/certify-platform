namespace Certyfi.Domain.Entities;

// A change to an approval safeguard that one institution admin asks for and
// another must confirm (dual control): switching off a template's final
// approval, or letting an API key issue without approval. Lives in each
// tenant's schema; every step is also in the institution audit log.
public class ControlRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = default!;
    public Guid TargetId { get; set; }
    public string TargetName { get; set; } = default!;
    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = default!;
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = ControlRequestStatus.Pending;
    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
}

public static class ControlRequestKind
{
    public const string DisableFinalApproval = "DisableFinalApproval";
    public const string EnableAutoIssue = "EnableAutoIssue";
}

public static class ControlRequestStatus
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Refused = "Refused";
    public const string Withdrawn = "Withdrawn";
}
