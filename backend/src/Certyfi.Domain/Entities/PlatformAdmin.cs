namespace Certyfi.Domain.Entities;

// Lives in the platform schema. Separate from ApplicationUser deliberately —
// a platform operator account must never be reachable through a tenant login
// path, and vice versa (see technical note 4.6, separation of duties).
public class PlatformAdmin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    // Signed in with a temporary password (new account or reset): the token
    // can only change the password until a new one is chosen.
    public bool MustChangePassword { get; set; }
    // Carried in the token ("sv"); bumped when the password changes, the
    // account is reset or deactivated, so every other session ends at once
    // (checked on each request, see Program.cs OnTokenValidated).
    public int SessionVersion { get; set; }
    public Guid? CreatedByAdminId { get; set; }
}
