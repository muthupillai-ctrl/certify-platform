namespace Certyfi.Domain.Entities;

// One change to a platform account: name, email, password (never the
// value), created, reset, deactivated or reactivated. Shown on the profile
// and Platform users pages.
public class PlatformAdminChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlatformAdminId { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    // Who did it (the account itself, or another platform admin).
    public Guid ChangedByAdminId { get; set; }
    public string ChangedByName { get; set; } = default!;
    public string Field { get; set; } = default!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
