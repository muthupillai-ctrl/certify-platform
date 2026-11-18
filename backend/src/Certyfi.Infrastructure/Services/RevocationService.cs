using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Revocation is permanent, so it's two-person, like issuing: RequestAsync
// records the ask (staff or a Partner API key), ApproveAsync by a DIFFERENT
// Approver/InstitutionAdmin revokes the certificate. Every step is on the
// certificate's ledger. Role checks are the caller's; the maker–checker and
// state rules live here so every entry point (portal, Partner API) shares them.
public class RevocationService
{
    private readonly TenantDbContext _db;
    private readonly ILedgerService _ledger;
    private readonly StudentNotifications _notifications;
    private readonly WebhookQueue _webhooks;

    public RevocationService(TenantDbContext db, ILedgerService ledger, StudentNotifications notifications, WebhookQueue webhooks)
    {
        _webhooks = webhooks;
        _db = db;
        _ledger = ledger;
        _notifications = notifications;
    }

    public record Outcome(RevocationRequest? Request, OutcomeError? Error);
    public enum OutcomeErrorKind { NotFound, Conflict, Forbidden, Invalid }
    public record OutcomeError(OutcomeErrorKind Kind, string Message);

    private static Outcome Fail(OutcomeErrorKind kind, string message) => new(null, new OutcomeError(kind, message));

    // An existing Pending request for the same certificate is returned as-is
    // (Existing = true), so a retried call doesn't fail.
    public async Task<(Outcome Outcome, bool Existing)> RequestAsync(Guid credentialId, Guid requesterUserId, string category, string note, CancellationToken ct)
    {
        note = note.Trim();
        if (!RevocationReasonCategory.All.Contains(category)) return (Fail(OutcomeErrorKind.Invalid, "Choose a reason."), false);
        if (note.Length == 0) return (Fail(OutcomeErrorKind.Invalid, "Explain why the certificate should be revoked."), false);
        if (note.Length > 1000) return (Fail(OutcomeErrorKind.Invalid, "The explanation must be at most 1000 characters."), false);

        var credential = await _db.Credentials.FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        if (credential is null) return (Fail(OutcomeErrorKind.NotFound, "Certificate not found."), false);
        var pending = await _db.RevocationRequests
            .FirstOrDefaultAsync(r => r.CredentialId == credentialId && r.Status == RevocationRequestStatus.Pending, ct);
        if (pending is not null) return (new Outcome(pending, null), true);
        if (credential.Status is not (CredentialStatus.Active or CredentialStatus.Suspended))
            return (Fail(OutcomeErrorKind.Conflict, $"The certificate is {credential.Status}; only active or suspended certificates can be revoked."), false);

        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        var request = new RevocationRequest
        {
            CredentialId = credentialId, ReasonCategory = category, Note = note,
            RequestedByUserId = requesterUserId, RequestedAt = now,
        };
        _db.RevocationRequests.Add(request);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _ledger.AppendAsync(credential, "RevocationRequested", requesterUserId, now,
            new { revocationRequestId = request.Id, reasonCategory = category }, new { note }, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Someone else asked at the same moment (unique Pending index).
            return (Fail(OutcomeErrorKind.Conflict, "A revocation request for this certificate is already waiting."), false);
        }
        await tx.CommitAsync(ct);
        return (new Outcome(request, null), false);
    }

    public async Task<Outcome> ApproveAsync(Guid requestId, Guid deciderUserId, CancellationToken ct)
    {
        var (request, error) = await LoadPending(requestId, ct);
        if (error is not null) return error;
        if (request!.RequestedByUserId == deciderUserId)
            return Fail(OutcomeErrorKind.Forbidden, "You asked for this revocation, so a different approver must approve it.");
        var credential = request.Credential;
        if (credential.Status is not (CredentialStatus.Active or CredentialStatus.Suspended))
            return Fail(OutcomeErrorKind.Conflict, $"The certificate is now {credential.Status}; reject this request instead.");

        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        request.Status = RevocationRequestStatus.Approved;
        request.DecidedByUserId = deciderUserId;
        request.DecidedAt = now;
        credential.Status = CredentialStatus.Revoked;
        credential.RevokedByUserId = deciderUserId;
        credential.RevokedAt = now;
        credential.RevocationReason = $"{RevocationReasonCategory.Label(request.ReasonCategory)}: {request.Note}";

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _ledger.AppendAsync(credential, "Revoked", deciderUserId, now,
            new
            {
                status = credential.Status.ToString(), reasonCategory = request.ReasonCategory,
                revocationRequestId = request.Id, requestedByUserId = request.RequestedByUserId,
            },
            new { credential.RevocationReason }, ct);
        await _webhooks.QueueAsync(credential, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _notifications.CertificateRevokedAsync(credential, ct);
        return new Outcome(request, null);
    }

    public async Task<Outcome> RejectAsync(Guid requestId, Guid deciderUserId, string note, CancellationToken ct)
    {
        note = note.Trim();
        if (note.Length == 0) return Fail(OutcomeErrorKind.Invalid, "Say why the certificate should not be revoked.");
        if (note.Length > 1000) return Fail(OutcomeErrorKind.Invalid, "The note must be at most 1000 characters.");
        var (request, error) = await LoadPending(requestId, ct);
        if (error is not null) return error;
        if (request!.RequestedByUserId == deciderUserId)
            return Fail(OutcomeErrorKind.Forbidden, "You asked for this revocation; withdraw it instead.");
        return await Close(request, RevocationRequestStatus.Rejected, "RevocationRequestRejected", deciderUserId, note, ct);
    }

    public async Task<Outcome> WithdrawAsync(Guid requestId, Guid userId, CancellationToken ct)
    {
        var (request, error) = await LoadPending(requestId, ct);
        if (error is not null) return error;
        if (request!.RequestedByUserId != userId)
            return Fail(OutcomeErrorKind.Forbidden, "Only the person who asked can withdraw a request.");
        return await Close(request, RevocationRequestStatus.Withdrawn, "RevocationRequestWithdrawn", userId, null, ct);
    }

    private async Task<Outcome> Close(RevocationRequest request, string status, string eventType, Guid userId, string? note, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        request.Status = status;
        request.DecidedByUserId = userId;
        request.DecidedAt = now;
        request.DecisionNote = note;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _ledger.AppendAsync(request.Credential, eventType, userId, now,
            new { revocationRequestId = request.Id }, note is null ? null : new { note }, ct);
        // Status unchanged, but the partner's "revocation requested" flag is.
        await _webhooks.QueueAsync(request.Credential, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Outcome(request, null);
    }

    private async Task<(RevocationRequest? Request, Outcome? Error)> LoadPending(Guid requestId, CancellationToken ct)
    {
        var request = await _db.RevocationRequests.Include(r => r.Credential).FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null) return (null, Fail(OutcomeErrorKind.NotFound, "Request not found."));
        if (request.Status != RevocationRequestStatus.Pending)
            return (null, Fail(OutcomeErrorKind.Conflict, $"This request was already {request.Status.ToLowerInvariant()}."));
        return (request, null);
    }
}
