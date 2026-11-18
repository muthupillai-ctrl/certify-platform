using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Certyfi.Infrastructure.Services;

// Dual control for approval safeguards (ControlRequest): one institution
// admin asks, a different one confirms or refuses; the asker may withdraw.
// Every step goes to the institution audit log.
public class ControlRequestService
{
    private readonly TenantDbContext _db;

    public ControlRequestService(TenantDbContext db)
    {
        _db = db;
    }

    public record Result(int Status, string? Error, ControlRequest? Request)
    {
        public static Result Ok(ControlRequest r) => new(200, null, r);
        public static Result Fail(int status, string error) => new(status, error, null);
    }

    // Opens a request (or returns the one already open for the same safeguard).
    public async Task<ControlRequest> RequestAsync(string kind, Guid targetId, string targetName, TenantUserService.Actor actor, CancellationToken ct)
    {
        var open = await _db.ControlRequests.FirstOrDefaultAsync(r => r.Kind == kind && r.TargetId == targetId && r.Status == ControlRequestStatus.Pending, ct);
        if (open is not null) return open;
        var request = new ControlRequest
        {
            Kind = kind, TargetId = targetId, TargetName = Clip(targetName),
            RequestedByUserId = actor.UserId!.Value, RequestedByName = Clip(actor.Name),
        };
        _db.ControlRequests.Add(request);
        TenantUserService.Audit(_db, actor, kind + "Requested", targetName, "Waiting for another institution admin to confirm");
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
            return await _db.ControlRequests.FirstAsync(r => r.Kind == kind && r.TargetId == targetId && r.Status == ControlRequestStatus.Pending, ct);
        }
        return request;
    }

    public Task<bool> IsPendingAsync(string kind, Guid targetId, CancellationToken ct) =>
        _db.ControlRequests.AnyAsync(r => r.Kind == kind && r.TargetId == targetId && r.Status == ControlRequestStatus.Pending, ct);

    public async Task<Result> ConfirmAsync(Guid id, TenantUserService.Actor actor, CancellationToken ct)
    {
        var request = await _db.ControlRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Result.Fail(404, "Request not found.");
        if (request.RequestedByUserId == actor.UserId) return Result.Fail(403, "You asked for this change; a different institution admin must confirm it.");
        if (request.Status != ControlRequestStatus.Pending) return Result.Fail(409, $"This request was already {request.Status.ToLowerInvariant()}.");

        string? applyError = null;
        switch (request.Kind)
        {
            case ControlRequestKind.DisableFinalApproval:
                var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == request.TargetId, ct);
                if (template is null) applyError = "The template no longer exists.";
                else template.RequiresFinalApproval = false;
                break;
            case ControlRequestKind.EnableAutoIssue:
                var client = await _db.ApiClients.FirstOrDefaultAsync(c => c.Id == request.TargetId, ct);
                if (client is null || client.RevokedAt is not null) applyError = "The API key has been removed.";
                else
                {
                    client.AutoIssue = true;
                    client.AutoIssueConfirmedByUserId = actor.UserId;
                }
                break;
        }
        if (applyError is not null) return Result.Fail(409, applyError);
        return await DecideAsync(request, ControlRequestStatus.Confirmed, actor, null, "Confirmed", ct);
    }

    public async Task<Result> RefuseAsync(Guid id, TenantUserService.Actor actor, string? note, CancellationToken ct)
    {
        var request = await _db.ControlRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Result.Fail(404, "Request not found.");
        if (request.RequestedByUserId == actor.UserId) return Result.Fail(403, "Withdraw your own request instead.");
        if (request.Status != ControlRequestStatus.Pending) return Result.Fail(409, $"This request was already {request.Status.ToLowerInvariant()}.");
        return await DecideAsync(request, ControlRequestStatus.Refused, actor, note, "Refused", ct);
    }

    public async Task<Result> WithdrawAsync(Guid id, TenantUserService.Actor actor, CancellationToken ct)
    {
        var request = await _db.ControlRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Result.Fail(404, "Request not found.");
        if (request.RequestedByUserId != actor.UserId) return Result.Fail(403, "Only the admin who asked can withdraw it.");
        if (request.Status != ControlRequestStatus.Pending) return Result.Fail(409, $"This request was already {request.Status.ToLowerInvariant()}.");
        return await DecideAsync(request, ControlRequestStatus.Withdrawn, actor, null, "Withdrawn", ct);
    }

    // Saves the decision only if the request is still pending (two admins
    // deciding at once: the first wins).
    private async Task<Result> DecideAsync(ControlRequest request, string status, TenantUserService.Actor actor, string? note, string verb, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var claimed = await _db.ControlRequests.Where(r => r.Id == request.Id && r.Status == ControlRequestStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.DecidedByUserId, actor.UserId)
                .SetProperty(r => r.DecidedByName, Clip(actor.Name))
                .SetProperty(r => r.DecidedAt, now)
                .SetProperty(r => r.Note, note == null ? null : note.Length > 500 ? note.Substring(0, 500) : note), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Result.Fail(409, "Someone decided this request just now. Reload.");
        }
        _db.Entry(request).State = EntityState.Unchanged;
        TenantUserService.Audit(_db, actor, request.Kind + verb, request.TargetName,
            $"Asked by {request.RequestedByName}" + (string.IsNullOrWhiteSpace(note) ? "" : $"; note: {note}"));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Result.Ok(await _db.ControlRequests.AsNoTracking().FirstAsync(r => r.Id == request.Id, ct));
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;
}
