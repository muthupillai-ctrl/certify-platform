using Certyfi.Domain.Enums;

namespace Certyfi.Domain.Entities;

// Lives in the platform (super-admin) schema — the registry of institutions.
// SchemaName is the physical Postgres schema holding this tenant's own data
// (users, credentials, badges, etc.), created at provisioning time.
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = default!;

    // Used to resolve the tenant from the request, e.g. "acme" for acme.certyfi.io
    // and/or an explicit X-Tenant-Slug header for API/partner integrations.
    public string Slug { get; set; } = default!;

    // Physical Postgres schema name for this tenant's data. Kept distinct
    // from Slug so schema-naming constraints don't leak into the public slug.
    public string SchemaName { get; set; } = default!;

    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;

    public string? BrandingLogoUrl { get; set; }
    public string? BrandingPrimaryColor { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }

    // Suspended: staff can't sign in and the Partner API is refused, but
    // certificates already issued stay publicly verifiable — they were
    // issued legitimately and must not start looking fake.
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }

    // ---- Institution profile (edited by its Institution Admin or a SuperAdmin) ----
    // Public: shown on every certificate's verification page and in the Open
    // Badges issuer profile, to help verifiers trust the issuer.
    public string? LegalName { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? AccreditationBody { get; set; }
    public string? About { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    // Private: visible to the institution's admins and SuperAdmins only.
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public DateTimeOffset? ProfileUpdatedAt { get; set; }

    // Certificates that need payment (Partner API keys with RequiresPayment)
    // are cancelled if not paid within this many days, with a reminder
    // first (see PaymentDeadlineRunner). 0 = never cancelled. Each
    // certificate keeps the due date it was given when requested.
    public int PaymentDays { get; set; } = 14;
}
