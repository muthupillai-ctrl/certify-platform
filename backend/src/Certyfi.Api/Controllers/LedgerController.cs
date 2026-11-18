using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped, InstitutionAdmin only — an integrity-check/admin operation,
// not a general viewer surface (per-credential ledger reads live on
// CredentialsController.GetLedger instead). Deliberately named "integrity",
// not "verify" — /api/v1/verify/{slug}/{id} (public, anonymous, credential
// status lookup) and this (authenticated, chain-integrity check) are
// unrelated concepts; reusing "verify" one path segment apart would be a
// real naming trap for future maintainers and API consumers.
[ApiController]
[Route("api/v1/ledger")]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class LedgerController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly LedgerIntegrityService _integrity;

    public LedgerController(TenantDbContext db, LedgerIntegrityService integrity)
    {
        _db = db;
        _integrity = integrity;
    }

    // On failure, Problem explains which certificate and event, what it
    // means and what to do (see LedgerIntegrityService).
    [HttpGet("integrity")]
    public async Task<ActionResult<LedgerIntegrityReport>> Integrity(CancellationToken ct) =>
        Ok(await _integrity.CheckAsync(_db, ct));
}
