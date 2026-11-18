namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. A partner system (another Ahamsys
// product, an institution's own SIS, ...) calling the Partner API under one
// API key. The plaintext key is shown exactly once at creation and never
// stored — only KeyPrefix (to look the row up) and SecretHash (SHA-256 of
// the whole key) are kept, same principle as a password hash.
//
// Every ApiClient owns its own ServiceUser (an ApplicationUser with
// IsServiceAccount = true) so that credentials and ledger entries it
// creates keep pointing at a real users row, exactly like human-made ones —
// the ledger's ActorUserId and Credential.RequestedByUserId never need a
// second "or maybe an API client" column.
public class ApiClient
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = default!;

    // Public, non-secret part of the key ("cfy_<KeyPrefix>_<secret>"),
    // unique per tenant — safe to show in lists and logs.
    public string KeyPrefix { get; set; } = default!;
    public string SecretHash { get; set; } = default!;

    // See PartnerScope for the allowed values.
    public List<string> Scopes { get; set; } = new();

    // When true, credentials this client creates are signed and activated
    // immediately instead of waiting for a human Approver. Only an
    // InstitutionAdmin can set it, at creation — CreatedByUserId is recorded
    // as the approver of every auto-issued credential (the admin approved
    // the policy; the partner system is the maker), so maker-checker still
    // involves two parties.
    public bool AutoIssue { get; set; }
    // The second institution admin who confirmed AutoIssue (dual control,
    // ControlRequest); recorded as the policy approver of what it issues.
    public Guid? AutoIssueConfirmedByUserId { get; set; }

    // Certificates this key requests wait as AwaitingPayment until the
    // student pays in the student portal; then they go to an approver (or
    // straight to Active with AutoIssue).
    public bool RequiresPayment { get; set; }

    // Optional: the partner's endpoint to call when one of this key's
    // certificates changes status (see WebhookDelivery), and the secret the
    // calls are signed with (encrypted; see SecretProtector).
    public string? WebhookUrl { get; set; }
    public string? WebhookSecretEncrypted { get; set; }
    public DateTimeOffset? WebhookUpdatedAt { get; set; }

    public Guid ServiceUserId { get; set; }
    public ApplicationUser ServiceUser { get; set; } = default!;

    public Guid CreatedByUserId { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }
    public ApplicationUser? RevokedByUser { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
