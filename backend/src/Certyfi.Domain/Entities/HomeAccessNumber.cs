namespace Certyfi.Domain.Entities;

// Lives in the PLATFORM schema. A mobile number allowed to open the public
// home page (the presentation) while it's private during pilot talks; see
// AccessController. Managed by the platform operator. Removing a number
// blocks that person at their next visit.
public class HomeAccessNumber
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Digits only, 10–16 (as typed, without spaces or "+").
    public string Number { get; set; } = default!;
    // Who it is, for the operator ("Dr. Bello, Lagos State University").
    public string Label { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}
