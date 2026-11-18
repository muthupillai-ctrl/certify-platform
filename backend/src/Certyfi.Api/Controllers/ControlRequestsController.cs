using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Dual-control requests (ControlRequest) and the institution audit log.
// Institution admins only: one asks for a safeguard change elsewhere (a
// template, an API key); another confirms or refuses it here.
[ApiController]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class ControlRequestsController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly ControlRequestService _requests;

    public ControlRequestsController(TenantDbContext db, ControlRequestService requests)
    {
        _db = db;
        _requests = requests;
    }

    [HttpGet("api/v1/control-requests")]
    public async Task<ActionResult<IEnumerable<ControlRequestResponse>>> List([FromQuery] bool pendingOnly = false, CancellationToken ct = default)
    {
        var me = CurrentUserId();
        var query = _db.ControlRequests.AsNoTracking();
        if (pendingOnly) query = query.Where(r => r.Status == ControlRequestStatus.Pending);
        var list = await query.OrderBy(r => r.Status == ControlRequestStatus.Pending ? 0 : 1).ThenByDescending(r => r.RequestedAt).Take(100).ToListAsync(ct);
        return Ok(list.Select(r => ToResponse(r, me)));
    }

    [HttpPost("api/v1/control-requests/{id:guid}/confirm")]
    public async Task<ActionResult<ControlRequestResponse>> Confirm(Guid id, CancellationToken ct) =>
        ToAction(await _requests.ConfirmAsync(id, await ActorAsync(ct), ct));

    [HttpPost("api/v1/control-requests/{id:guid}/refuse")]
    public async Task<ActionResult<ControlRequestResponse>> Refuse(Guid id, RefuseControlRequest request, CancellationToken ct) =>
        ToAction(await _requests.RefuseAsync(id, await ActorAsync(ct), request.Note?.Trim(), ct));

    [HttpPost("api/v1/control-requests/{id:guid}/withdraw")]
    public async Task<ActionResult<ControlRequestResponse>> Withdraw(Guid id, CancellationToken ct) =>
        ToAction(await _requests.WithdrawAsync(id, await ActorAsync(ct), ct));

    // Newest first, a page at a time.
    [HttpGet("api/v1/audit-log")]
    public async Task<ActionResult<AuditLogPageResponse>> AuditLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await _db.InstitutionAuditLog.CountAsync(ct);
        var rows = await _db.InstitutionAuditLog.AsNoTracking().OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditLogEntryResponse(a.Id, a.At, a.ActorKind, a.ActorName, a.Action, a.Subject, a.Details))
            .ToListAsync(ct);
        return Ok(new AuditLogPageResponse(rows, total, page, pageSize));
    }

    // The whole log as a spreadsheet (oldest first).
    [HttpGet("api/v1/audit-log/export.csv")]
    public async Task<IActionResult> AuditLogCsv(CancellationToken ct)
    {
        var rows = await _db.InstitutionAuditLog.AsNoTracking().OrderBy(a => a.At).ThenBy(a => a.Id).ToListAsync(ct);
        var sb = new System.Text.StringBuilder("When (UTC),By,Who,Action,About,Details\r\n");
        foreach (var a in rows)
        {
            sb.Append(string.Join(",", new[] { a.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"), a.ActorKind, a.ActorName, a.Action, a.Subject, a.Details ?? "" }.Select(Csv))).Append("\r\n");
        }
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", "audit-log.csv");
    }

    // Quotes a cell; a leading = + - @ is neutralised so spreadsheets don't run it as a formula.
    private static string Csv(string v)
    {
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static ControlRequestResponse ToResponse(ControlRequest r, Guid me) => new(
        r.Id, r.Kind, r.TargetId, r.TargetName, r.RequestedByName, r.RequestedAt, r.Status, r.DecidedByName, r.DecidedAt, r.Note,
        CanDecide: r.Status == ControlRequestStatus.Pending && r.RequestedByUserId != me,
        IsMine: r.RequestedByUserId == me);

    private ActionResult<ControlRequestResponse> ToAction(ControlRequestService.Result result) =>
        result.Request is null ? StatusCode(result.Status, new { error = result.Error }) : Ok(ToResponse(result.Request, CurrentUserId()));

    private async Task<TenantUserService.Actor> ActorAsync(CancellationToken ct)
    {
        var id = CurrentUserId();
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "Unknown";
        return new TenantUserService.Actor(id, name);
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
