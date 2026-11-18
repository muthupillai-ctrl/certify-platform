namespace Certyfi.Domain.Entities;

// Platform-wide: which institutions have a student with this email, so a
// student can sign in with just email and password (no institution code).
// The email is stored only as a keyed hash (StudentSignInIndex.Hash). An
// entry is only a hint: sign-in still checks the institution's own
// students table, so a stale entry costs a lookup and nothing more.
public class StudentSignInEntry
{
    public string EmailHash { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
