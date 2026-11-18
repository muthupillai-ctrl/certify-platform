using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Two-person revocation (the Revoke & suspend page): one Approver or
// Institution Admin asks, a different one approves; only then is the
// certificate revoked. Asking and approving both need the certificate's
// code typed to confirm. Partner API keys can ask too (PartnerController).
[ApiController]
[Route("api/v1/revocation-requests")]
[Authorize(Roles = $"{SystemRole.Approver},{SystemRole.InstitutionAdmin}")]
public class RevocationRequestsController : ControllerBase
{
    private const int MaxDecided = 50;

    private readonly TenantDbContext _db;
    private readonly RevocationService _revocations;

    public RevocationRequestsController(TenantDbContext db, RevocationService revocations)
    {
        _db = db;
        _revocations = revocations;
    }

    // state=pending (default): the queue, oldest first. state=decided: the
    // latest decisions. credentialId narrows to one certificate's history.
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RevocationRequestResponse>>> List(
        [FromQuery] string? state, [FromQuery] Guid? credentialId, CancellationToken ct)
    {
        var decided = string.Equals(state, "decided", StringComparison.OrdinalIgnoreCase);
        var query = _db.RevocationRequests.AsNoTracking()
            .Where(r => decided ? r.Status != RevocationRequestStatus.Pending : r.Status == RevocationRequestStatus.Pending);
        if (credentialId is { } cid) query = query.Where(r => r.CredentialId == cid);
        query = decided
            ? query.OrderByDescending(r => r.DecidedAt).Take(MaxDecided)
            : query.OrderBy(r => r.RequestedAt);
        return Ok(await Load(query, ct));
    }

    private async Task<List<RevocationRequestResponse>> Load(IQueryable<RevocationRequest> query, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var rows = await query.Select(r => new
        {
            r.Id, r.CredentialId, r.Credential.RecipientName, AchievementTitle = r.Credential.Achievement.Title,
            r.Credential.VerificationCode, CredentialStatus = r.Credential.Status,
            r.ReasonCategory, r.Note, r.RequestedByUserId, RequestedByName = r.RequestedByUser.FullName,
            r.RequestedByUser.IsServiceAccount, r.RequestedAt, r.Status,
            DecidedByName = r.DecidedByUser != null ? r.DecidedByUser.FullName : null, r.DecidedAt, r.DecisionNote,
        }).ToListAsync(ct);

        // A Partner API request shows the key's name rather than its service account's.
        var serviceUserIds = rows.Where(r => r.IsServiceAccount).Select(r => r.RequestedByUserId).Distinct().ToList();
        var keyNames = await _db.ApiClients.AsNoTracking().Where(a => serviceUserIds.Contains(a.ServiceUserId))
            .ToDictionaryAsync(a => a.ServiceUserId, a => a.Name, ct);

        return rows.Select(r =>
        {
            var pending = r.Status == RevocationRequestStatus.Pending;
            return new RevocationRequestResponse(
                r.Id, r.CredentialId, r.RecipientName, r.AchievementTitle, VerificationCodes.Format(r.VerificationCode), r.CredentialStatus.ToString(),
                r.ReasonCategory, RevocationReasonCategory.Label(r.ReasonCategory), r.Note,
                r.IsServiceAccount ? keyNames.GetValueOrDefault(r.RequestedByUserId, r.RequestedByName) : r.RequestedByName,
                r.IsServiceAccount, r.RequestedAt,
                r.Status, r.DecidedByName, r.DecidedAt, r.DecisionNote,
                CanDecide: pending && r.RequestedByUserId != userId,
                CanWithdraw: pending && r.RequestedByUserId == userId);
        }).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<RevocationRequestResponse>> Create(CreateRevocationRequest request, CancellationToken ct)
    {
        var credential = await _db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CredentialId, ct);
        if (credential is null) return NotFound(new { error = "Certificate not found." });
        if (!CredentialsController.CodeMatches(request.ConfirmCode, credential))
            return BadRequest(new { error = CredentialsController.ConfirmCodeError });
        if (request.ReasonCategory == RevocationReasonCategory.PartnerSystem)
            return BadRequest(new { error = "Choose a reason." });

        var (outcome, existing) = await _revocations.RequestAsync(request.CredentialId, CurrentUserId(), request.ReasonCategory ?? "", request.Note ?? "", ct);
        if (outcome.Error is { } error) return Problem(error);
        if (existing) return Conflict(new { error = "A revocation request for this certificate is already waiting for approval." });
        return Ok(await One(outcome.Request!.Id, ct));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<RevocationRequestResponse>> Approve(Guid id, ApproveRevocationRequest request, CancellationToken ct)
    {
        var credential = await _db.RevocationRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.Credential).FirstOrDefaultAsync(ct);
        if (credential is null) return NotFound(new { error = "Request not found." });
        if (!CredentialsController.CodeMatches(request.ConfirmCode, credential))
            return BadRequest(new { error = CredentialsController.ConfirmCodeError });

        var outcome = await _revocations.ApproveAsync(id, CurrentUserId(), ct);
        return outcome.Error is { } error ? Problem(error) : Ok(await One(id, ct));
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<RevocationRequestResponse>> Reject(Guid id, RejectRevocationRequest request, CancellationToken ct)
    {
        var outcome = await _revocations.RejectAsync(id, CurrentUserId(), request.Note ?? "", ct);
        return outcome.Error is { } error ? Problem(error) : Ok(await One(id, ct));
    }

    [HttpPost("{id:guid}/withdraw")]
    public async Task<ActionResult<RevocationRequestResponse>> Withdraw(Guid id, CancellationToken ct)
    {
        var outcome = await _revocations.WithdrawAsync(id, CurrentUserId(), ct);
        return outcome.Error is { } error ? Problem(error) : Ok(await One(id, ct));
    }

    private async Task<RevocationRequestResponse> One(Guid id, CancellationToken ct) =>
        (await Load(_db.RevocationRequests.AsNoTracking().Where(r => r.Id == id), ct)).Single();

    private ObjectResult Problem(RevocationService.OutcomeError error)
    {
        var status = error.Kind switch
        {
            RevocationService.OutcomeErrorKind.NotFound => StatusCodes.Status404NotFound,
            RevocationService.OutcomeErrorKind.Conflict => StatusCodes.Status409Conflict,
            RevocationService.OutcomeErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(status, new { error = error.Message });
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
