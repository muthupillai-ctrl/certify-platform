using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Persistence;

// Every tenant's data lives in its own Postgres schema, but through the SAME
// .NET type — CurrentSchema is read from ITenantContext at construction time
// and applied in OnModelCreating. See TenantModelCacheKeyFactory for why
// this is safe to cache per-schema rather than rebuilding the model on every
// request.
//
// Credential/Badge/Issuance entities (Certyfi.Domain, section 4.5 of the
// technical note) plug into this same context as the platform is built out —
// this scaffold wires the tenancy + RBAC tables only.
public class TenantDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    // Deliberately invalid as a real schema name — fails loudly at query time
    // rather than defaulting to a real tenant's data. Also doubles as the
    // fixed placeholder EF Core bakes into compiled migrations at design
    // time (see TenantDbContextFactory.CreateTenantSchemaAsync for how that
    // gets substituted with the real schema per tenant at provisioning).
    public const string UnresolvedSchemaPlaceholder = "unresolved";

    public string CurrentSchema => _tenantContext.IsResolved
        ? _tenantContext.SchemaName
        : UnresolvedSchemaPlaceholder;

    public TenantDbContext(DbContextOptions<TenantDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<LedgerCheckpoint> LedgerCheckpoints => Set<LedgerCheckpoint>();
    public DbSet<CertificateTemplate> CertificateTemplates => Set<CertificateTemplate>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<RevocationRequest> RevocationRequests => Set<RevocationRequest>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<LedgerEntryDetail> LedgerEntryDetails => Set<LedgerEntryDetail>();
    public DbSet<VerificationEvent> VerificationEvents => Set<VerificationEvent>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<StudentChange> StudentChanges => Set<StudentChange>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ControlRequest> ControlRequests => Set<ControlRequest>();
    public DbSet<InstitutionAuditEntry> InstitutionAuditLog => Set<InstitutionAuditEntry>();
    public DbSet<Signatory> Signatories => Set<Signatory>();
    public DbSet<PortalBanner> PortalBanners => Set<PortalBanner>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(CurrentSchema);
        // Trigram matching for the certificates list's "contains" search
        // (see the credentials indexes below). Database-wide, created once
        // in public; every tenant migration re-asserts it idempotently.
        modelBuilder.HasPostgresExtension("public", "pg_trgm");

        modelBuilder.Entity<ApplicationUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(320).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(ur => new { ur.UserId, ur.RoleId });
            e.HasOne(ur => ur.User).WithMany(u => u.UserRoles).HasForeignKey(ur => ur.UserId);
            e.HasOne(ur => ur.Role).WithMany(r => r.UserRoles).HasForeignKey(ur => ur.RoleId);
        });

        modelBuilder.Entity<Achievement>(e =>
        {
            e.ToTable("achievements");
            e.Property(a => a.Title).HasMaxLength(300).IsRequired();
            e.Property(a => a.FieldSchemaJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            e.Property(a => a.TemplateTitleText).HasMaxLength(300).IsRequired();
            e.Property(a => a.TemplateSubtitleText).HasMaxLength(500);
            e.Property(a => a.TemplateAccentColor).HasMaxLength(9);
            e.Property(a => a.TemplateFontFamily).HasMaxLength(20);
            e.Property(a => a.TemplateLayoutJson).HasColumnType("text").IsRequired();

            // SetNull, not Restrict — TemplateId is optional traceability
            // only, and templates are never hard-deleted anyway (only
            // deactivated), so this is a safety net rather than a
            // load-bearing constraint.
            e.HasOne(a => a.Template).WithMany().HasForeignKey(a => a.TemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CertificateTemplate>(e =>
        {
            e.ToTable("certificate_templates");
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.Property(t => t.FieldSchemaJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            e.Property(t => t.SignatoryIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]").IsRequired();
            e.Property(t => t.TitleText).HasMaxLength(300).IsRequired();
            e.Property(t => t.SubtitleText).HasMaxLength(500);
            e.Property(t => t.AccentColor).HasMaxLength(9);
            e.Property(t => t.FontFamily).HasMaxLength(20);
            e.Property(t => t.LayoutJson).HasColumnType("text").IsRequired();
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.ToTable("credentials");
            e.Property(c => c.SignatoriesJson).HasColumnType("text");
            e.Property(c => c.RecipientName).HasMaxLength(200).IsRequired();
            e.Property(c => c.RecipientEmail).HasMaxLength(320).IsRequired();
            e.Property(c => c.FieldValuesJson).HasColumnType("text").HasDefaultValue("{}").IsRequired();
            e.Property(c => c.FieldSchemaJson).HasColumnType("text").HasDefaultValue("[]").IsRequired();
            e.HasIndex(c => c.Status);
            // Certificates list: case-insensitive "contains" search (ILIKE)
            // on recipient and custom field values uses these trigram
            // indexes; sorting uses the date indexes.
            e.HasIndex(c => c.RecipientName).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("IX_credentials_RecipientName_trgm");
            e.HasIndex(c => c.RecipientEmail).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("IX_credentials_RecipientEmail_trgm");
            e.HasIndex(c => c.FieldValuesJson).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("IX_credentials_FieldValuesJson_trgm");
            e.HasIndex(c => c.CreatedAt);
            e.HasIndex(c => c.IssuedAt);
            e.HasIndex(c => c.ExpiresOn);
            e.Property(c => c.ExternalReference).HasMaxLength(200);
            e.Property(c => c.LegacyCertificateNumber).HasMaxLength(100);
            e.Property(c => c.LegacyNumberKey).HasMaxLength(100);
            e.Property(c => c.PersonalDataSalt).HasMaxLength(64);
            // One live certificate per paper number (a rejected import can be
            // imported again). Status 1 = Rejected.
            e.HasIndex(c => c.LegacyNumberKey).IsUnique().HasFilter("\"LegacyNumberKey\" IS NOT NULL AND \"Status\" <> 1");
            e.HasIndex(c => c.ImportBatchId);
            e.HasOne(c => c.ImportBatch).WithMany().HasForeignKey(c => c.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => c.StudentId);
            e.Property(c => c.CancellationReason).HasMaxLength(500);
            // The deadline job's "what's due" query.
            e.HasIndex(c => new { c.Status, c.PaymentDueAt });
            e.HasOne(c => c.Student).WithMany(s => s.Credentials).HasForeignKey(c => c.StudentId).OnDelete(DeleteBehavior.SetNull);
            e.Property(c => c.VerificationCode).HasMaxLength(12).IsFixedLength().IsRequired();
            e.HasIndex(c => c.VerificationCode).IsUnique();
            e.Property(c => c.OfflineProof).HasColumnType("text");
            // Declared explicitly: EF would otherwise treat the composite
            // index below as covering RequestedByUserId and drop this one,
            // but that index is partial, so it can't serve lookups by
            // requester (the Partner API's "own credentials" query).
            e.HasIndex(c => c.RequestedByUserId);
            // Idempotency for Partner API retries — see
            // Credential.ExternalReference. Partial so UI-drafted credentials
            // (no reference) never collide with each other.
            e.HasIndex(c => new { c.RequestedByUserId, c.ExternalReference })
                .IsUnique()
                .HasFilter("\"ExternalReference\" IS NOT NULL");

            // Restrict, not Cascade: credentials are historical/audit
            // records that must not vanish or dangle if a referenced user
            // is later deactivated (no user-delete endpoint exists yet, but
            // the constraint should hold regardless).
            e.HasOne(c => c.Achievement).WithMany().HasForeignKey(c => c.AchievementId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.RequestedByUser).WithMany().HasForeignKey(c => c.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.DecidedByUser).WithMany().HasForeignKey(c => c.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.SuspendedByUser).WithMany().HasForeignKey(c => c.SuspendedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.RevokedByUser).WithMany().HasForeignKey(c => c.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            // EF's default convention would otherwise pick the Guid Id
            // property as the primary key — this MUST be explicit, or the
            // append-order design breaks silently with no compile error.
            e.HasKey(x => x.SequenceNumber);
            e.Property(x => x.EventType).HasMaxLength(30).IsRequired();
            e.Property(x => x.PreviousEntryHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.EntryHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(x => x.Id).IsUnique();
            // Reports (status changes, staff activity) select a period.
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.CredentialId);
            e.HasIndex(x => x.EntryHash).IsUnique();
            // Two entries can never legitimately share a parent hash — this
            // is real defense-in-depth, not decoration: it turns a future
            // forgotten-transaction-wrapper bug into a loud constraint
            // violation instead of a silent forked chain.
            e.HasIndex(x => x.PreviousEntryHash).IsUnique();

            e.HasOne(x => x.Credential).WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ActorUser).WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Student>(e =>
        {
            e.ToTable("students");
            e.Property(s => s.StudentNumber).HasMaxLength(100);
            e.HasIndex(s => s.StudentNumber).IsUnique().HasFilter("\"StudentNumber\" IS NOT NULL");
            e.Property(s => s.FullName).HasMaxLength(200).IsRequired();
            e.Property(s => s.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(s => s.Email).IsUnique();
            e.Property(s => s.Phone).HasMaxLength(40);
            e.Property(s => s.PasswordHash).HasMaxLength(100);
            // Students list: case-insensitive "contains" search, like credentials.
            e.HasIndex(s => s.FullName).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("IX_students_FullName_trgm");
        });

        modelBuilder.Entity<StudentChange>(e =>
        {
            e.ToTable("student_changes");
            e.Property(c => c.ChangedByName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Field).HasMaxLength(50).IsRequired();
            e.Property(c => c.OldValue).HasMaxLength(320);
            e.Property(c => c.NewValue).HasMaxLength(320);
            e.HasIndex(c => new { c.StudentId, c.ChangedAt });
            e.HasOne(c => c.Student).WithMany(s => s.Changes).HasForeignKey(c => c.StudentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ControlRequest>(e =>
        {
            e.ToTable("control_requests");
            e.Property(r => r.Kind).HasMaxLength(40).IsRequired();
            e.Property(r => r.TargetName).HasMaxLength(200).IsRequired();
            e.Property(r => r.RequestedByName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(20).IsRequired();
            e.Property(r => r.DecidedByName).HasMaxLength(200);
            e.Property(r => r.Note).HasMaxLength(500);
            e.HasIndex(r => new { r.Status, r.RequestedAt });
            // At most one open request per safeguard.
            e.HasIndex(r => new { r.Kind, r.TargetId }).IsUnique().HasFilter("\"Status\" = 'Pending'");
        });

        modelBuilder.Entity<InstitutionAuditEntry>(e =>
        {
            e.ToTable("institution_audit_log");
            e.Property(a => a.ActorKind).HasMaxLength(20).IsRequired();
            e.Property(a => a.ActorName).HasMaxLength(200).IsRequired();
            e.Property(a => a.Action).HasMaxLength(60).IsRequired();
            e.Property(a => a.Subject).HasMaxLength(300).IsRequired();
            e.Property(a => a.Details).HasMaxLength(1000);
            e.HasIndex(a => a.At);
        });

        modelBuilder.Entity<Signatory>(e =>
        {
            e.ToTable("signatories");
            e.Property(s => s.Name).HasMaxLength(120).IsRequired();
            e.Property(s => s.Designation).HasMaxLength(120).IsRequired();
        });

        modelBuilder.Entity<PortalBanner>(e =>
        {
            e.ToTable("portal_banners");
            e.Property(b => b.Title).HasMaxLength(120).IsRequired();
            e.Property(b => b.ImageUrl).HasMaxLength(500).IsRequired();
            e.Property(b => b.LinkUrl).HasMaxLength(500);
            e.HasIndex(b => new { b.Side, b.SortOrder });
        });

        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.Property(d => d.CredentialStatus).HasMaxLength(30).IsRequired();
            e.Property(d => d.LastError).HasMaxLength(500);
            e.HasIndex(d => new { d.State, d.NextAttemptAt });
            e.HasIndex(d => new { d.ApiClientId, d.CreatedAt });
        });

        modelBuilder.Entity<ApiClient>(e =>
        {
            e.ToTable("api_clients");
            e.Property(x => x.WebhookUrl).HasMaxLength(500);
            e.Property(x => x.WebhookSecretEncrypted).HasMaxLength(500);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(32).IsRequired();
            e.Property(x => x.SecretHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(x => x.KeyPrefix).IsUnique();

            e.HasOne(x => x.ServiceUser).WithMany().HasForeignKey(x => x.ServiceUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ServiceUserId).IsUnique();
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RevokedByUser).WithMany().HasForeignKey(x => x.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // Off-chain personal details of audit-trail entries (deletable, unlike
        // ledger_entries, which is append-only).
        modelBuilder.Entity<LedgerEntryDetail>(e =>
        {
            e.ToTable("ledger_entry_details");
            e.HasKey(x => x.LedgerEntryId);
            e.Property(x => x.DataJson).IsRequired();
            e.HasIndex(x => x.CredentialId);
        });

        modelBuilder.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.DecisionNote).HasMaxLength(1000);
            e.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DecidedByUser).WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        modelBuilder.Entity<RevocationRequest>(e =>
        {
            e.ToTable("revocation_requests");
            e.Property(x => x.ReasonCategory).HasMaxLength(40).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.DecisionNote).HasMaxLength(1000);
            e.HasOne(x => x.Credential).WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RequestedByUser).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DecidedByUser).WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
            // One open request per certificate; the queue lists by status and age.
            e.HasIndex(x => x.CredentialId).IsUnique().HasFilter("\"Status\" = 'Pending'");
            e.HasIndex(x => new { x.Status, x.RequestedAt });
        });

        modelBuilder.Entity<VerificationEvent>(e =>
        {
            e.ToTable("verification_events");
            e.Property(x => x.Channel).HasMaxLength(30).IsRequired();
            e.Property(x => x.Result).HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.CredentialId, x.OccurredAt });
            // The Verification page's activity log and the dashboard, by date.
            e.HasIndex(x => x.OccurredAt);
            // No FK to credentials on purpose: NotFound look-ups have no
            // credential, and history must never block anything on the
            // credential itself.
        });

        modelBuilder.Entity<LedgerCheckpoint>(e =>
        {
            e.ToTable("ledger_checkpoints");
            e.Property(x => x.CheckpointHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.LastCheckResult).HasMaxLength(1000);
            e.HasIndex(x => x.UpToSequenceNumber).IsUnique();
            e.HasIndex(x => x.Status);
        });
    }
}
