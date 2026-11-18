namespace Certyfi.Domain.Entities;

// A certificate recipient with their own sign-in to the student portal,
// one list per institution (tenant schema). Separate from ApplicationUser on
// purpose: students are never staff and never hold a staff role.
//
// Every certificate with an email is linked to the student with that email
// (CredentialIssuanceService creates the student on first sight). A partner
// app also sends its own id for the student (StudentNumber), matched first.
// A student signs in only after setting a password from a portal link
// (StudentPortalLink); until then PasswordHash is null.
public class Student
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // The partner's (or institution's) own id for the student, as sent.
    // Unique when present.
    public string? StudentNumber { get; set; }
    public string FullName { get; set; } = default!;
    // Lower-cased; unique. The student signs in with it.
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }

    public StudentSource Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? PasswordHash { get; set; }
    public DateTimeOffset? PasswordSetAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    // Bumped when staff issue a fresh sign-in link, so older links stop
    // working (see StudentPortalLink).
    public int SignInVersion { get; set; }
    // Last time an email with a set-up link went to them (any email: a
    // certificate request, staff "email link", a bulk invitation). Bulk
    // invitations skip anyone invited recently.
    public DateTimeOffset? LastInvitedAt { get; set; }

    public List<Credential> Credentials { get; set; } = new();
    public List<StudentChange> Changes { get; set; } = new();
}

public enum StudentSource
{
    // Created from an existing certificate's recipient (staff, import, or
    // the first migration).
    Certificate = 0,
    // Created by a partner app's issue request.
    PartnerApi = 1,
}
