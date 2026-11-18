namespace Certyfi.Domain.Entities;

// One changed field on a Student (name, email, phone, student ID): who,
// when, from what to what. Shown on the student's page; deleted with the
// student (erasure).
public class StudentChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Student Student { get; set; } = default!;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    // The staff user, or the API key's service account for a partner app.
    public Guid ChangedByUserId { get; set; }
    // Name at the time ("Jane Admin", or "LMS production (API)").
    public string ChangedByName { get; set; } = default!;
    public string Field { get; set; } = default!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
