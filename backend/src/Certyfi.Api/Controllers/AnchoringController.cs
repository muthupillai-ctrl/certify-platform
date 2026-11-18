using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Certyfi.Api.Controllers;

// SuperAdmin only. Not tenant-scoped (exempted path prefix — matches
// TenantsController). Manual, immediate triggers for the same checkpoint/
// upgrade work LedgerAnchorBackgroundService otherwise runs hourly/every
// few hours — deliberately a manual, audited, SuperAdmin-only action rather
// than something with a tighter automatic schedule, same reasoning as
// TenantsController.MigrateSchemas. Also what makes the checkpoint/upgrade
// logic directly integration-testable without waiting on the background
// timers.
//
// The rest backs the Anchoring page's troubleshooting tools: job status,
// connectivity, the checkpoint list, a read-only diagnosis of one
// checkpoint, and per-checkpoint "check now" / "re-verify" / proof download.
// None of these can alter a proof except by the same upgrade/verify steps
// the background job runs.
[ApiController]
[Route("api/v1/platform/anchoring")]
[Authorize(Roles = SystemRole.SuperAdmin)]
public class AnchoringController : ControllerBase
{
    private const int MaxListLimit = 500;

    private readonly ILedgerCheckpointService _checkpointService;
    private readonly AnchoringDiagnosticsService _diagnostics;
    private readonly AnchoringRunStatus _runStatus;

    public AnchoringController(ILedgerCheckpointService checkpointService, AnchoringDiagnosticsService diagnostics, AnchoringRunStatus runStatus)
    {
        _checkpointService = checkpointService;
        _diagnostics = diagnostics;
        _runStatus = runStatus;
    }

    [HttpPost("checkpoint-now")]
    public async Task<IActionResult> CheckpointNow(CancellationToken ct)
    {
        var created = await _checkpointService.RunCheckpointStepAsync("Manual", ct);
        return Ok(new { checkpointsCreated = created });
    }

    [HttpPost("upgrade-now")]
    public async Task<IActionResult> UpgradeNow(CancellationToken ct)
    {
        var confirmed = await _checkpointService.RunUpgradeStepAsync("Manual", ct);
        return Ok(new { checkpointsConfirmed = confirmed });
    }

    // Cheap: no outbound calls. Connectivity is a separate, explicit check.
    [HttpGet("status")]
    public async Task<ActionResult<AnchoringStatusResponse>> Status(CancellationToken ct)
    {
        var counts = await _diagnostics.CountAsync(ct);
        return Ok(new AnchoringStatusResponse(
            _runStatus.ProcessStartedAt, _runStatus.BackgroundJobStartedAt,
            _runStatus.CheckpointInterval.TotalMinutes, _runStatus.UpgradeInterval.TotalMinutes,
            _runStatus.LastCheckpointRun, _runStatus.LastUpgradeRun, counts));
    }

    [HttpGet("connectivity")]
    public async Task<ActionResult<ConnectivityReport>> Connectivity(CancellationToken ct) =>
        Ok(await _diagnostics.ProbeConnectivityAsync(ct));

    [HttpGet("checkpoints")]
    public async Task<ActionResult<IEnumerable<CheckpointRow>>> ListCheckpoints(
        [FromQuery] string? status, [FromQuery] string? tenant, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not (null or "all" or "pending" or "confirmed" or "failed" or "attention"))
        {
            return BadRequest(new { error = "status must be one of: all, pending, confirmed, failed, attention." });
        }
        var tenantSlug = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim().ToLowerInvariant();
        return Ok(await _diagnostics.ListCheckpointsAsync(normalizedStatus, tenantSlug, Math.Clamp(limit, 1, MaxListLimit), ct));
    }

    [HttpGet("checkpoints/{tenantSlug}/{id:guid}/diagnose")]
    public async Task<ActionResult<CheckpointDiagnosis>> Diagnose(string tenantSlug, Guid id, CancellationToken ct)
    {
        var tenant = await _diagnostics.FindTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found." });
        var diagnosis = await _diagnostics.DiagnoseAsync(tenant, id, ct);
        return diagnosis is null ? NotFound(new { error = "Checkpoint not found." }) : Ok(diagnosis);
    }

    // Upgrades just this checkpoint (if Pending) and returns its new state.
    [HttpPost("checkpoints/{tenantSlug}/{id:guid}/check")]
    public async Task<ActionResult<CheckpointRow>> CheckOne(string tenantSlug, Guid id, CancellationToken ct)
    {
        var tenant = await _diagnostics.FindTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found." });
        var checkpoint = await _checkpointService.CheckOneAsync(tenant, id, ct);
        if (checkpoint is null) return NotFound(new { error = "Checkpoint not found." });
        return Ok(await RowAsync(tenantSlug, id, ct));
    }

    [HttpPost("checkpoints/{tenantSlug}/{id:guid}/reverify")]
    public async Task<ActionResult<CheckpointRow>> Reverify(string tenantSlug, Guid id, CancellationToken ct)
    {
        var tenant = await _diagnostics.FindTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found." });
        var checkpoint = await _checkpointService.ReverifyAsync(tenant, id, ct);
        if (checkpoint is null) return NotFound(new { error = "Checkpoint not found." });
        if (checkpoint.Status != "Confirmed")
        {
            return Conflict(new { error = $"Only confirmed checkpoints can be re-verified; this one is '{checkpoint.Status}'." });
        }
        return Ok(await RowAsync(tenantSlug, id, ct));
    }

    // The raw .ots proof, to check with the official `ots` client
    // (`ots verify`) independently of Certyfi.
    [HttpGet("checkpoints/{tenantSlug}/{id:guid}/proof")]
    public async Task<IActionResult> DownloadProof(string tenantSlug, Guid id, CancellationToken ct)
    {
        var tenant = await _diagnostics.FindTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Tenant not found." });
        var proof = await _diagnostics.GetProofAsync(tenant, id, ct);
        if (proof is null) return NotFound(new { error = "Checkpoint not found." });
        return File(proof, "application/octet-stream", $"{tenant.Slug}-checkpoint-{id}.ots");
    }

    private async Task<CheckpointRow> RowAsync(string tenantSlug, Guid id, CancellationToken ct) =>
        (await _diagnostics.ListCheckpointsAsync(null, tenantSlug.Trim().ToLowerInvariant(), MaxListLimit, ct)).First(r => r.Id == id);
}
