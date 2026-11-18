using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Adds a WebhookDelivery for a certificate's status change when the API key
// that requested it has a webhook. Called inside the change's own
// transaction, before it saves, so the call is queued if and only if the
// change happened.
public class WebhookQueue
{
    private readonly TenantDbContext _db;

    public WebhookQueue(TenantDbContext db)
    {
        _db = db;
    }

    public async Task QueueAsync(Credential credential, CancellationToken ct, string? statusOverride = null)
    {
        var apiClientId = await _db.ApiClients.AsNoTracking()
            .Where(a => a.ServiceUserId == credential.RequestedByUserId && a.WebhookUrl != null && a.RevokedAt == null)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (apiClientId is null) return;

        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            ApiClientId = apiClientId.Value,
            CredentialId = credential.Id,
            // Final approval is internal: partners see PendingApproval.
            CredentialStatus = statusOverride ?? (credential.Status == Domain.Enums.CredentialStatus.PendingFinalApproval ? "PendingApproval" : credential.Status.ToString()),
        });
    }
}
