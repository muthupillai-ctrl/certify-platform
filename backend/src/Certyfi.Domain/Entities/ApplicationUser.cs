namespace Certyfi.Domain.Entities;

// Lives inside each tenant's own schema. A person who needs access to more
// than one institution gets one ApplicationUser row per tenant — deliberate,
// since credentials/permissions should never silently cross tenant boundaries.
public class ApplicationUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;

    public string FullName { get; set; } = default!;
    public bool IsActive { get; set; } = true;

    // The identity an ApiClient acts as (see ApiClient.ServiceUserId) —
    // never a person. Service accounts cannot log in (AuthController rejects
    // them regardless of password) and are hidden from the Users list.
    public bool IsServiceAccount { get; set; } = false;

    // MFA is called out as mandatory for administrative roles in the
    // technical approach note; the scaffold carries the flag/secret now so
    // it isn't a schema migration later.
    public bool MfaEnabled { get; set; } = false;
    public string? MfaSecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    // Set when an admin resets the password to a temporary one: the user
    // can sign in but can do nothing except choose a new password (see
    // PasswordChangeRequiredMiddleware).
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
