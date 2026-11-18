namespace Certyfi.Domain.Entities;

// An email waiting to be sent, or the record that it was (platform schema,
// all institutions). The API only ever queues here; EmailDispatcher sends in
// the background with retries, so a slow or failing mail provider never
// fails or slows the request that caused the email. Bodies can carry sign-in
// links, so they're deleted once the email is sent or given up on; the rest
// is kept a while as a record and then purged.
public class EmailOutboxEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    // What it's for, e.g. "CertificateRequested" (see EmailTemplates).
    public string Kind { get; set; } = default!;
    public Guid? StudentId { get; set; }
    public Guid? CredentialId { get; set; }

    public string ToEmail { get; set; } = default!;
    public string? ToName { get; set; }
    public string Subject { get; set; } = default!;
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }

    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
    // The provider's id for the sent message (Resend), for support queries.
    public string? ProviderMessageId { get; set; }
}

public enum EmailStatus
{
    Pending = 0,
    // Claimed by a dispatcher run; back to Pending if it fails.
    Sending = 1,
    Sent = 2,
    // Gave up after the maximum attempts.
    Failed = 3,
    // No email provider configured (Email:Provider = None); never sent.
    Skipped = 4,
}
