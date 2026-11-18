using System.Text.RegularExpressions;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

public class TenantProvisioningService : ITenantProvisioningService
{
    // schemaName (derived from this) is interpolated directly into raw SQL
    // (CREATE SCHEMA, and the substituted migration script) rather than
    // passed as a query parameter, since Postgres doesn't allow parameterized
    // schema/identifier names. This pattern is what makes that safe — reject
    // anything that isn't a short lowercase-alphanumeric-hyphen slug before
    // it ever reaches SQL, rather than escaping it.
    private static readonly Regex SlugPattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private const int MaxSlugLength = 56; // "tenant_" (7) + slug must fit Postgres's 63-byte identifier limit

    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbFactory;
    // NOTE: TenantDbContext normally reads its schema from ITenantContext
    // (request-scoped). Provisioning happens OUTSIDE a resolved-tenant
    // request, so TenantDbContextFactory builds it against an explicit
    // schema instead of the request-scoped one — see TenantDbContextFactory.

    public TenantProvisioningService(PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory)
    {
        _platformDb = platformDb;
        _tenantDbFactory = tenantDbFactory;
    }

    public async Task<Tenant> ProvisionAsync(string name, string slug, IReadOnlyList<NewInstitutionAdmin> admins, CancellationToken ct = default)
    {
        if (admins.Count == 0) throw new ArgumentException("At least one Institution Admin is required.");
        if (admins.Select(a => a.Email.Trim().ToLowerInvariant()).Distinct().Count() != admins.Count)
            throw new ArgumentException("Each Institution Admin needs a different email address.");
        slug = slug.Trim().ToLowerInvariant();
        if (slug.Length == 0 || slug.Length > MaxSlugLength || !SlugPattern.IsMatch(slug))
        {
            throw new ArgumentException(
                $"Tenant slug must be lowercase alphanumeric segments separated by single hyphens (e.g. 'acme-university'), {MaxSlugLength} characters or fewer.");
        }
        var schemaName = $"tenant_{slug.Replace('-', '_')}";

        if (await _platformDb.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            throw new InvalidOperationException($"Tenant slug '{slug}' is already taken.");
        }

        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            SchemaName = schemaName,
            Status = TenantStatus.Provisioning
        };
        _platformDb.Tenants.Add(tenant);
        await _platformDb.SaveChangesAsync(ct);

        // 1. Create the physical schema and run tenant-schema migrations
        //    against it — see TenantDbContextFactory.EnsureTenantSchemaMigratedAsync
        //    for why this isn't a plain Database.MigrateAsync() call.
        await _tenantDbFactory.EnsureTenantSchemaMigratedAsync(schemaName, ct);

        // 2. Seed baseline roles and the first InstitutionAdmin.
        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);

        foreach (var roleName in SystemRole.TenantAssignable)
        {
            tenantDb.Roles.Add(new Role { Name = roleName, IsSystemRole = true });
        }
        await tenantDb.SaveChangesAsync(ct);

        var adminRole = await tenantDb.Roles.FirstAsync(r => r.Name == SystemRole.InstitutionAdmin, ct);
        foreach (var admin in admins)
        {
            var adminUser = new ApplicationUser
            {
                Email = admin.Email.Trim().ToLowerInvariant(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(admin.InitialPassword),
                FullName = string.IsNullOrWhiteSpace(admin.FullName) ? "Institution Administrator" : admin.FullName.Trim(),
                IsActive = true,
                // They choose their own password when they first sign in.
                MustChangePassword = admins.Count > 1,
            };
            tenantDb.Users.Add(adminUser);
            await tenantDb.SaveChangesAsync(ct);
            tenantDb.UserRoles.Add(new UserRole { UserId = adminUser.Id, RoleId = adminRole.Id });
            InstitutionAudit.Add(tenantDb, InstitutionAudit.Platform, null, "Ahamsys", "AdminAdded", $"{adminUser.FullName} ({adminUser.Email})", "Institution created");
            await tenantDb.SaveChangesAsync(ct);
        }

        // Seed a couple of starter certificate templates so the new
        // tenant's catalog isn't empty on day one — the InstitutionAdmin
        // can still edit/deactivate/replace these like any other template.
        const string certificateFieldSchema = "[]";
        const string marksCardFieldSchema = """
            [
              {"key":"rollNumber","label":"Roll Number","type":"text"},
              {"key":"subjects","label":"Subjects","type":"table","columns":[
                {"key":"subject","label":"Subject","type":"text"},
                {"key":"maxMarks","label":"Max Marks","type":"number"},
                {"key":"obtainedMarks","label":"Obtained Marks","type":"number"},
                {"key":"grade","label":"Grade","type":"text"}
              ]}
            ]
            """;
        tenantDb.CertificateTemplates.AddRange(
            new CertificateTemplate
            {
                Name = "Certificate of Achievement",
                Description = "A general-purpose certificate for course completions, workshops, or recognitions.",
                Kind = TemplateKind.Certificate,
                FieldSchemaJson = certificateFieldSchema,
                TitleText = "Certificate of Achievement",
                LayoutJson = Certyfi.Infrastructure.Validation.DefaultLayouts.Generate(TemplateKind.Certificate, certificateFieldSchema),
            },
            new CertificateTemplate
            {
                Name = "Marks Card",
                Description = "A transcript-style mark sheet with roll number and a repeating subjects table.",
                Kind = TemplateKind.MarksCard,
                FieldSchemaJson = marksCardFieldSchema,
                TitleText = "Official Transcript",
                LayoutJson = Certyfi.Infrastructure.Validation.DefaultLayouts.Generate(TemplateKind.MarksCard, marksCardFieldSchema),
            });
        await tenantDb.SaveChangesAsync(ct);

        // 3. Flip the tenant active once its schema is fully seeded — a
        //    request should never be able to resolve a half-provisioned tenant.
        tenant.Status = TenantStatus.Active;
        tenant.ActivatedAt = DateTimeOffset.UtcNow;
        await _platformDb.SaveChangesAsync(ct);

        return tenant;
    }

    // Backfills every active tenant's schema with whatever tenant-schema
    // migrations exist that it doesn't have yet — see
    // TenantDbContextFactory.EnsureTenantSchemaMigratedAsync for why this is
    // safe to call repeatedly (idempotent, pure DDL, no data touched).
    // Needed because a migration added after a tenant is provisioned
    // doesn't reach that tenant's schema on its own.
    public async Task<int> MigrateAllTenantSchemasAsync(CancellationToken ct = default)
    {
        var tenants = await _platformDb.Tenants.AsNoTracking()
            // Suspended too, so a reactivated institution isn't behind.
            .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended)
            .ToListAsync(ct);

        foreach (var tenant in tenants)
        {
            await _tenantDbFactory.EnsureTenantSchemaMigratedAsync(tenant.SchemaName, ct);
        }

        return tenants.Count;
    }
}
