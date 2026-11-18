using Certyfi.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Certyfi.Infrastructure.Persistence;

// Used by TenantProvisioningService (and anything else operating on a tenant
// schema OUTSIDE a resolved-tenant HTTP request). The standard
// IDbContextFactory<TenantDbContext> can't do this job: it opens its own
// internal DI scope per CreateDbContext() call and resolves TenantDbContext's
// ITenantContext constructor parameter from THAT scope — which just produces
// a fresh, unresolved ITenantContext, not the specific tenant schema the
// caller actually wants. This factory sidesteps DI scope resolution for that
// one parameter entirely: it holds a single DbContextOptions<TenantDbContext>
// (built once, wired to the same TenantModelCacheKeyFactory as the per-request
// registration) and constructs TenantDbContext directly against whatever
// ITenantContext the caller supplies.
public class TenantDbContextFactory
{
    private readonly DbContextOptions<TenantDbContext> _options;

    public TenantDbContextFactory(DbContextOptions<TenantDbContext> options)
    {
        _options = options;
    }

    public TenantDbContext CreateForSchema(Guid tenantId, string slug, string schemaName)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, slug, schemaName);
        return new TenantDbContext(_options, tenantContext);
    }

    // EF Core bakes the schema name as a literal string into compiled
    // Migration classes at `dotnet ef migrations add` time — it isn't a
    // runtime parameter of Database.MigrateAsync(). Since every tenant needs
    // its own physically distinct schema, that call can't be used directly
    // for schema-per-tenant provisioning: it would always target whatever
    // schema was active when the migration was generated (see
    // TenantDbContext.UnresolvedSchemaPlaceholder), no matter which tenant is
    // being provisioned.
    //
    // Instead: ask IMigrator for the full SQL script (generated fresh from
    // the compiled migrations each call, so it can never drift from them),
    // substitute the placeholder schema for the real one as plain text, and
    // execute the result raw. This is the standard workaround for dynamic
    // per-tenant schemas with EF Core migrations.
    //
    // Generated with MigrationsSqlGenerationOptions.Idempotent: every
    // migration's DDL is wrapped in
    // "IF NOT EXISTS(SELECT 1 FROM <schema>.__ef_migrations_history WHERE
    // "MigrationId" = '...')" guards, so this same method is safe to call
    // against a schema that's ALREADY migrated — it applies only whatever
    // migrations are new, making it dual-purpose: brand-new tenant creation
    // (called from TenantProvisioningService.ProvisionAsync) and backfilling
    // existing tenants when a later migration is added (called from
    // TenantProvisioningService.MigrateAllTenantSchemasAsync). Confirmed
    // against the actual installed EF Core/Npgsql packages, not just docs.
    public async Task EnsureTenantSchemaMigratedAsync(string schemaName, CancellationToken ct = default)
    {
        await using var placeholderDb = CreateForSchema(Guid.Empty, "placeholder", TenantDbContext.UnresolvedSchemaPlaceholder);

        var migrator = placeholderDb.GetService<IMigrator>();
        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        var tenantScript = script.Replace(TenantDbContext.UnresolvedSchemaPlaceholder, schemaName);

        var connection = placeholderDb.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = tenantScript;
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
