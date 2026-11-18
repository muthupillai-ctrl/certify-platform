namespace Certyfi.Infrastructure.MultiTenancy;

// Scoped-per-request. Populated by TenantResolutionMiddleware before any
// controller or DbContext runs. Deliberately a thin interface — everything
// downstream (DbContext schema selection, authorization) reads from this
// rather than re-deriving tenant identity from the request itself.
public interface ITenantContext
{
    bool IsResolved { get; }
    Guid TenantId { get; }
    string TenantSlug { get; }
    string SchemaName { get; }

    void Set(Guid tenantId, string slug, string schemaName);
}
