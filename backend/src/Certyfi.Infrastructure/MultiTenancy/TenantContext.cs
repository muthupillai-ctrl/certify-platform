namespace Certyfi.Infrastructure.MultiTenancy;

public class TenantContext : ITenantContext
{
    public bool IsResolved { get; private set; }
    public Guid TenantId { get; private set; }
    public string TenantSlug { get; private set; } = string.Empty;
    public string SchemaName { get; private set; } = string.Empty;

    public void Set(Guid tenantId, string slug, string schemaName)
    {
        TenantId = tenantId;
        TenantSlug = slug;
        SchemaName = schemaName;
        IsResolved = true;
    }
}
