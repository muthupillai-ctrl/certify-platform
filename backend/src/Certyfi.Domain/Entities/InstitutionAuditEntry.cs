namespace Certyfi.Domain.Entities;

// The institution's audit log: changes to who can do what and to the
// approval rules (users and roles, final approval, API key approval
// settings, dual-control requests). Append-only (a database trigger refuses
// changes and deletions). Certificate events are in each certificate's own
// ledger instead.
public class InstitutionAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    // Staff (a user of the institution) or Platform (Ahamsys operator).
    public string ActorKind { get; set; } = default!;
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string? Details { get; set; }
}
