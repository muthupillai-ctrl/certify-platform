namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. Seeded from SystemRole.All at tenant
// provisioning time; institutions cannot delete the seeded roles, but this
// table exists (rather than a hardcoded enum-only check) so tenant-specific
// custom roles can be added later without another migration.
public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public bool IsSystemRole { get; set; } = true;

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
