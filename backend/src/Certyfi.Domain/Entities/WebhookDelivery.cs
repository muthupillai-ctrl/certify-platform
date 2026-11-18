namespace Certyfi.Domain.Entities;

// One "credential.updated" call to a partner's webhook (ApiClient.WebhookUrl),
// queued in the same transaction as the status change and sent in the
// background by WebhookDispatcher with retries. The partner treats each call
// as "go and look" (GET /credentials/{id}), so order and duplicates don't
// matter. Kept as the key's delivery log.
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApiClientId { get; set; }
    public Guid CredentialId { get; set; }
    // The credential's status when the change happened (informational).
    public string CredentialStatus { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public WebhookDeliveryState State { get; set; } = WebhookDeliveryState.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public enum WebhookDeliveryState
{
    Pending = 0,
    // Claimed by a dispatcher run; back to Pending if it fails.
    Sending = 1,
    Delivered = 2,
    // Retried until the last backoff step, still failing.
    Failed = 3,
    // Not retried: the partner refused it (400/401/other 4xx), or the key's
    // webhook was removed.
    Stopped = 4,
}
