using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.IntegrationTests;

// Test-only provisioning: an institution created with ONE admin (how most
// tests of other features set up) also gives that admin the Registrar and
// Approver roles, so those tests can keep using it to request and approve
// certificates. Real institutions get two admins with only the admin role
// (TenantsController); FinalApprovalTests provisions those.
public class SingleAdminTestProvisioning : ITenantProvisioningService
{
    private readonly TenantProvisioningService _inner;
    private readonly TenantDbContextFactory _tenantDbs;

    public SingleAdminTestProvisioning(TenantProvisioningService inner, TenantDbContextFactory tenantDbs)
    {
        _inner = inner;
        _tenantDbs = tenantDbs;
    }

    public async Task<Tenant> ProvisionAsync(string name, string slug, IReadOnlyList<NewInstitutionAdmin> admins, CancellationToken ct = default)
    {
        var tenant = await _inner.ProvisionAsync(name, slug, admins, ct);
        if (admins.Count == 1)
        {
            await using var db = _tenantDbs.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            var email = admins[0].Email.Trim().ToLowerInvariant();
            var admin = await db.Users.FirstAsync(u => u.Email == email, ct);
            foreach (var role in await db.Roles.Where(r => r.Name == SystemRole.Registrar || r.Name == SystemRole.Approver).ToListAsync(ct))
            {
                db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = role.Id });
            }
            await db.SaveChangesAsync(ct);
        }
        return tenant;
    }

    public Task<int> MigrateAllTenantSchemasAsync(CancellationToken ct = default) => _inner.MigrateAllTenantSchemasAsync(ct);
}
