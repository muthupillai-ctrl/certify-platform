using Certyfi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Persistence;

// The one context that is NOT schema-per-tenant — it owns the "platform"
// schema: the tenant registry itself, plus platform-operator accounts.
// A tenant-scoped request should never touch this context beyond the
// read-only lookup TenantResolutionMiddleware does.
public class PlatformDbContext : DbContext
{
    public const string SchemaName = "platform";

    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<TenantSigningKey> TenantSigningKeys => Set<TenantSigningKey>();
    public DbSet<HomeAccessNumber> HomeAccessNumbers => Set<HomeAccessNumber>();
    public DbSet<EmailOutboxEntry> EmailOutbox => Set<EmailOutboxEntry>();
    public DbSet<PlatformAdminChange> PlatformAdminChanges => Set<PlatformAdminChange>();
    public DbSet<StudentSignInEntry> StudentSignInIndex => Set<StudentSignInEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasIndex(t => t.Slug).IsUnique();
            e.HasIndex(t => t.SchemaName).IsUnique();
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.Property(t => t.Slug).HasMaxLength(63).IsRequired();
            e.Property(t => t.SchemaName).HasMaxLength(63).IsRequired();
            e.Property(t => t.SuspensionReason).HasMaxLength(500);
            // Limits match TenantProfileValidator.
            e.Property(t => t.LegalName).HasMaxLength(300);
            e.Property(t => t.WebsiteUrl).HasMaxLength(500);
            e.Property(t => t.RegistrationNumber).HasMaxLength(100);
            e.Property(t => t.AccreditationBody).HasMaxLength(200);
            e.Property(t => t.About).HasMaxLength(1000);
            e.Property(t => t.City).HasMaxLength(100);
            e.Property(t => t.Country).HasMaxLength(100);
            e.Property(t => t.AddressLine1).HasMaxLength(200);
            e.Property(t => t.AddressLine2).HasMaxLength(200);
            e.Property(t => t.Region).HasMaxLength(100);
            e.Property(t => t.PostalCode).HasMaxLength(20);
            e.Property(t => t.ContactEmail).HasMaxLength(320);
            e.Property(t => t.ContactPhone).HasMaxLength(40);
        });

        modelBuilder.Entity<StudentSignInEntry>(e =>
        {
            e.ToTable("student_sign_in_index");
            e.HasKey(x => new { x.EmailHash, x.TenantId });
            e.Property(x => x.EmailHash).HasMaxLength(64);
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<HomeAccessNumber>(e =>
        {
            e.ToTable("home_access_numbers");
            e.Property(x => x.Number).HasMaxLength(16).IsRequired();
            e.Property(x => x.Label).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
        });

        modelBuilder.Entity<PlatformAdminChange>(e =>
        {
            e.ToTable("platform_admin_changes");
            e.Property(c => c.ChangedByName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Field).HasMaxLength(50).IsRequired();
            e.Property(c => c.OldValue).HasMaxLength(320);
            e.Property(c => c.NewValue).HasMaxLength(320);
            e.HasIndex(c => new { c.PlatformAdminId, c.ChangedAt });
        });

        modelBuilder.Entity<EmailOutboxEntry>(e =>
        {
            e.ToTable("email_outbox");
            e.Property(x => x.Kind).HasMaxLength(50).IsRequired();
            e.Property(x => x.ToEmail).HasMaxLength(320).IsRequired();
            e.Property(x => x.ToName).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            e.Property(x => x.HtmlBody).HasColumnType("text");
            e.Property(x => x.TextBody).HasColumnType("text");
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.ProviderMessageId).HasMaxLength(200);
            // The dispatcher's "what's due" query.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.StudentId);
        });

        modelBuilder.Entity<PlatformAdmin>(e =>
        {
            e.ToTable("platform_admins");
            e.HasIndex(a => a.Email).IsUnique();
            e.Property(a => a.FullName).HasMaxLength(200);
        });

        modelBuilder.Entity<TenantSigningKey>(e =>
        {
            e.ToTable("tenant_signing_keys");
            // A tenant can never have two keys sharing an id — this is also
            // what LocalTenantSigningKeyProvider's lazy-create race handling
            // relies on (the losing concurrent insert fails this index,
            // not silently duplicates a row).
            e.HasIndex(k => new { k.TenantId, k.KeyId }).IsUnique();
            e.Property(k => k.KeyId).HasMaxLength(100).IsRequired();
            e.Property(k => k.Algorithm).HasMaxLength(20).IsRequired();
            e.Property(k => k.PublicKeyPem).IsRequired();
        });
    }
}
