using Certyfi.Domain.Entities;

namespace Certyfi.Infrastructure.Services;

public interface ITenantProvisioningService
{
    // Creates the tenant registry row, creates its Postgres schema, runs the
    // tenant-schema migrations against it, seeds the baseline roles, and
    // creates the first InstitutionAdmin user. Returns the created tenant.
    Task<Tenant> ProvisionAsync(string name, string slug, string institutionAdminEmail, string institutionAdminInitialPassword, CancellationToken ct = default) =>
        ProvisionAsync(name, slug, [new NewInstitutionAdmin(institutionAdminEmail, "Institution Administrator", institutionAdminInitialPassword)], ct);

    // The same with several Institution Admins (the platform API requires two).
    Task<Tenant> ProvisionAsync(string name, string slug, IReadOnlyList<NewInstitutionAdmin> admins, CancellationToken ct = default);

    // Backfills every active tenant's schema with any tenant-schema
    // migrations it doesn't have yet. Returns the number of tenants processed.
    Task<int> MigrateAllTenantSchemasAsync(CancellationToken ct = default);
}

public record NewInstitutionAdmin(string Email, string FullName, string InitialPassword);
