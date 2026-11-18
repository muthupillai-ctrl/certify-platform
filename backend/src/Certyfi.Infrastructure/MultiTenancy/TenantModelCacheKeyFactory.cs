using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Certyfi.Infrastructure.MultiTenancy;

// EF Core normally builds and caches ONE model per DbContext type. That
// breaks schema-per-tenant, because TenantDbContext's OnModelCreating sets
// a different default schema per tenant — without this, every tenant after
// the first would silently get served the first tenant's cached model.
//
// Including the schema name in the cache key makes EF cache one model per
// distinct schema instead of one model total. Fine for a few hundred
// tenants; if the platform scales into the thousands this should move to
// database-per-shard rather than schema-per-tenant.
public class TenantModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is Persistence.TenantDbContext tenantContext)
        {
            return (context.GetType(), tenantContext.CurrentSchema, designTime);
        }

        return (context.GetType(), designTime);
    }
}
