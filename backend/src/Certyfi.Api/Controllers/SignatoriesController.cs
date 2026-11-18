using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// The institution's signatories (see Signatory). InstitutionAdmin manages
// them; Registrar can read them (templates show who signs). Editing a name
// or designation applies to certificates created from then on; ones
// already created keep what they were made with. A signatory a template
// still names can't be deleted.
[ApiController]
[Route("api/v1/signatories")]
public class SignatoriesController : ControllerBase
{
    private readonly TenantDbContext _db;

    public SignatoriesController(TenantDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<IEnumerable<SignatoryResponse>>> List(CancellationToken ct)
    {
        var signatories = await _db.Signatories.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var templates = await _db.CertificateTemplates.AsNoTracking().Select(t => new { t.Name, t.SignatoryIds }).ToListAsync(ct);
        return Ok(signatories.Select(s => new SignatoryResponse(s.Id, s.Name, s.Designation,
            templates.Where(t => t.SignatoryIds.Contains(s.Id)).Select(t => t.Name).OrderBy(n => n).ToList())));
    }

    [HttpPost]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<SignatoryResponse>> Create(SaveSignatoryRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return BadRequest(new { error });
        var signatory = new Signatory { Name = request.Name.Trim(), Designation = request.Designation.Trim() };
        _db.Signatories.Add(signatory);
        await _db.SaveChangesAsync(ct);
        return Ok(new SignatoryResponse(signatory.Id, signatory.Name, signatory.Designation, []));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<SignatoryResponse>> Update(Guid id, SaveSignatoryRequest request, CancellationToken ct)
    {
        var signatory = await _db.Signatories.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (signatory is null) return NotFound(new { error = "Signatory not found." });
        if (Validate(request) is { } error) return BadRequest(new { error });
        signatory.Name = request.Name.Trim();
        signatory.Designation = request.Designation.Trim();
        await _db.SaveChangesAsync(ct);
        var usedBy = await _db.CertificateTemplates.Where(t => t.SignatoryIds.Contains(id)).Select(t => t.Name).OrderBy(n => n).ToListAsync(ct);
        return Ok(new SignatoryResponse(signatory.Id, signatory.Name, signatory.Designation, usedBy));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var usedBy = await _db.CertificateTemplates.Where(t => t.SignatoryIds.Contains(id)).Select(t => t.Name).OrderBy(n => n).ToListAsync(ct);
        if (usedBy.Count > 0)
        {
            return Conflict(new { error = $"Used by {string.Join(", ", usedBy)}. Choose another signatory on {(usedBy.Count == 1 ? "that template" : "those templates")} first." });
        }
        var deleted = await _db.Signatories.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound(new { error = "Signatory not found." }) : NoContent();
    }

    private static string? Validate(SaveSignatoryRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        var designation = request.Designation?.Trim() ?? "";
        if (name.Length is 0 or > 120) return "Name is required (max 120 characters).";
        if (designation.Length is 0 or > 120) return "Designation is required (max 120 characters), e.g. Principal.";
        return null;
    }
}
