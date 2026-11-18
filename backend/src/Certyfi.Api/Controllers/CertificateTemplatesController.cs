using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped. A reusable starting point for creating Achievements — see
// CertificateTemplate's own comment for why Achievements COPY a template's
// schema/kind rather than referencing it live (which is exactly what makes
// Update safe here: editing a template's fields/kind never changes an
// already-created achievement, only future ones — same reasoning already
// proven for AchievementsController.Update). Registrar gets read access
// (needed to pick a template when creating an achievement) but only
// InstitutionAdmin manages the catalog itself — narrower than Achievement's
// own create permissions, matching the user's explicit scoping. Delete is
// permanent but only when unused (no achievement has ever referenced this
// template) — a template that HAS been used keeps its traceability and can
// only be Deactivated, never deleted; this mirrors the ledger/snapshot
// philosophy used everywhere else in this app: real history is never
// silently discarded.
[ApiController]
[Route("api/v1/certificate-templates")]
public class CertificateTemplatesController : ControllerBase
{
    public const int MaxSignatories = 3;

    private readonly TenantDbContext _db;
    // Certificates:EnforceMandatoryContent (default on): a template must
    // name a signatory and its design must show every core item
    // (LayoutValidator.CoreElements) before it can be saved.
    private readonly bool _enforceMandatory;

    private readonly ControlRequestService _controlRequests;

    public CertificateTemplatesController(TenantDbContext db, IConfiguration configuration, ControlRequestService controlRequests)
    {
        _controlRequests = controlRequests;
        _db = db;
        _enforceMandatory = configuration.GetValue("Certificates:EnforceMandatoryContent", true);
    }

    [HttpGet]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<IEnumerable<CertificateTemplateResponse>>> List()
    {
        var templates = await _db.CertificateTemplates
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync();

        var signatories = await _db.Signatories.AsNoTracking().ToDictionaryAsync(s => s.Id);
        var offPending = await _db.ControlRequests.AsNoTracking()
            .Where(r => r.Kind == ControlRequestKind.DisableFinalApproval && r.Status == ControlRequestStatus.Pending).Select(r => r.TargetId).ToListAsync();
        return Ok(templates.Select(t => ToResponse(t, t.SignatoryIds.Where(signatories.ContainsKey).Select(id => signatories[id]).ToList(), offPending.Contains(t.Id))));
    }

    [HttpPost]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<CertificateTemplateResponse>> Create(CreateCertificateTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required." });
        }

        // Enum.TryParse alone also accepts numeric strings (e.g. "99") for
        // an undefined underlying value — Enum.IsDefined closes that gap.
        if (!Enum.TryParse<TemplateKind>(request.Kind, out var kind) || !Enum.IsDefined(kind))
        {
            return BadRequest(new { error = $"Unknown template kind '{request.Kind}'." });
        }

        var fieldSchemaJson = request.FieldSchemaJson ?? "[]";
        if (!FieldSchemaValidator.TryValidateSchema(fieldSchemaJson, out var error))
        {
            return BadRequest(new { error });
        }

        if (!TryResolveVisualDesign(request.TitleText, request.Name, request.AccentColor, request.FontFamily,
                request.LayoutJson, request.BackgroundImageUrl, kind, fieldSchemaJson,
                out var titleText, out var accentColor, out var fontFamily, out var layoutJson, out var backgroundImageUrl, out error))
        {
            return BadRequest(new { error });
        }

        var (signatoryIds, signatoryError) = await CheckMandatoryAsync(request.SignatoryIds, layoutJson);
        if (signatoryError is not null) return BadRequest(new { error = signatoryError });

        var template = new CertificateTemplate
        {
            RequiresFinalApproval = request.RequiresFinalApproval == true,
            SignatoryIds = signatoryIds,
            Name = request.Name,
            Description = request.Description,
            Kind = kind,
            FieldSchemaJson = fieldSchemaJson,
            TitleText = titleText,
            SubtitleText = request.SubtitleText,
            AccentColor = accentColor,
            FontFamily = fontFamily,
            LayoutJson = layoutJson,
            BackgroundImageUrl = backgroundImageUrl,
        };
        _db.CertificateTemplates.Add(template);
        if (template.RequiresFinalApproval) TenantUserService.Audit(_db, await ActorAsync(), "FinalApprovalTurnedOn", $"Template \"{template.Name}\"", "New template");
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), await ToResponseAsync(template));
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult> Deactivate(Guid id)
    {
        var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (template is null) return NotFound();

        template.IsActive = false;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<CertificateTemplateResponse>> Update(Guid id, UpdateCertificateTemplateRequest request)
    {
        var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (template is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required." });
        }

        if (!Enum.TryParse<TemplateKind>(request.Kind, out var kind) || !Enum.IsDefined(kind))
        {
            return BadRequest(new { error = $"Unknown template kind '{request.Kind}'." });
        }

        var fieldSchemaJson = request.FieldSchemaJson ?? "[]";
        if (!FieldSchemaValidator.TryValidateSchema(fieldSchemaJson, out var error))
        {
            return BadRequest(new { error });
        }

        if (!TryResolveVisualDesign(request.TitleText, request.Name, request.AccentColor, request.FontFamily,
                request.LayoutJson, request.BackgroundImageUrl, kind, fieldSchemaJson,
                out var titleText, out var accentColor, out var fontFamily, out var layoutJson, out var backgroundImageUrl, out error))
        {
            return BadRequest(new { error });
        }

        var (signatoryIds, signatoryError) = await CheckMandatoryAsync(request.SignatoryIds, layoutJson);
        if (signatoryError is not null) return BadRequest(new { error = signatoryError });

        // Final approval: on at once; off only when another admin confirms.
        var askToTurnOff = false;
        if (request.RequiresFinalApproval == true && !template.RequiresFinalApproval)
        {
            template.RequiresFinalApproval = true;
            TenantUserService.Audit(_db, await ActorAsync(), "FinalApprovalTurnedOn", $"Template \"{request.Name}\"");
        }
        else if (request.RequiresFinalApproval == false && template.RequiresFinalApproval)
        {
            askToTurnOff = true;
        }

        template.SignatoryIds = signatoryIds;
        template.Name = request.Name;
        template.Description = request.Description;
        template.Kind = kind;
        template.FieldSchemaJson = fieldSchemaJson;
        template.TitleText = titleText;
        template.SubtitleText = request.SubtitleText;
        template.AccentColor = accentColor;
        template.FontFamily = fontFamily;
        template.LayoutJson = layoutJson;
        template.BackgroundImageUrl = backgroundImageUrl;
        await _db.SaveChangesAsync();
        if (askToTurnOff)
        {
            await _controlRequests.RequestAsync(ControlRequestKind.DisableFinalApproval, template.Id, $"Template \"{template.Name}\": switch off final approval", await ActorAsync(), HttpContext.RequestAborted);
        }

        return Ok(await ToResponseAsync(template));
    }

    // Permanent — only when no Achievement has ever been created from this
    // template. See the class-level comment for why a USED template can
    // only be Deactivated: hard-deleting it would leave every achievement
    // (and the credentials issued from them) with a dangling, unexplained
    // provenance gap.
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult> Delete(Guid id)
    {
        var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (template is null) return NotFound();

        var isUsed = await _db.Achievements.AnyAsync(a => a.TemplateId == id);
        if (isUsed)
        {
            return Conflict(new { error = "This template has been used to create achievements and cannot be deleted — deactivate it instead." });
        }

        _db.CertificateTemplates.Remove(template);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static readonly System.Text.RegularExpressions.Regex HexColorPattern = new(@"^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$");
    private static readonly HashSet<string> FontFamilies = LayoutValidator.FontKeys;
    // Same http(s)-only convention as TenantSettingsController's LogoUrl —
    // this app has no file storage of its own, so an imported "professional"
    // background is always externally hosted, never uploaded here.
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(@"^https?://");

    // TitleText defaults to Name and LayoutJson to a Kind-appropriate
    // generated default (DefaultLayouts) whenever a request omits them —
    // this keeps every caller that only ever set Name/Description/Kind/
    // FieldSchemaJson (existing tests, or a future raw API caller) working
    // unchanged, while still letting the canvas editor (Phase 2) supply its
    // own explicit values for all five.
    private static bool TryResolveVisualDesign(
        string? requestTitleText, string name, string? requestAccentColor, string? requestFontFamily, string? requestLayoutJson, string? requestBackgroundImageUrl,
        TemplateKind kind, string fieldSchemaJson,
        out string titleText, out string? accentColor, out string? fontFamily, out string layoutJson, out string? backgroundImageUrl, out string? error)
    {
        error = null;
        titleText = string.IsNullOrWhiteSpace(requestTitleText) ? name : requestTitleText;
        accentColor = requestAccentColor;
        fontFamily = requestFontFamily;
        layoutJson = string.IsNullOrWhiteSpace(requestLayoutJson) ? DefaultLayouts.Generate(kind, fieldSchemaJson) : requestLayoutJson;
        backgroundImageUrl = string.IsNullOrWhiteSpace(requestBackgroundImageUrl) ? null : requestBackgroundImageUrl;

        if (!string.IsNullOrEmpty(accentColor) && !HexColorPattern.IsMatch(accentColor))
        {
            error = "accentColor must be a hex color.";
            return false;
        }

        if (!string.IsNullOrEmpty(fontFamily) && !FontFamilies.Contains(fontFamily))
        {
            error = $"fontFamily must be one of: {string.Join(", ", FontFamilies)}.";
            return false;
        }

        if (backgroundImageUrl is not null && !UrlPattern.IsMatch(backgroundImageUrl))
        {
            error = "backgroundImageUrl must be an http:// or https:// URL.";
            return false;
        }

        if (!LayoutValidator.TryValidateLayout(layoutJson, fieldSchemaJson, out error))
        {
            return false;
        }

        return true;
    }

    // The template's signatories (known, distinct, at most three; at least
    // one when enforced) and, when enforced, the core items its design must
    // show. Returns the ids to save, or the error.
    private async Task<(List<Guid> Ids, string? Error)> CheckMandatoryAsync(IReadOnlyList<Guid>? requested, string layoutJson)
    {
        var ids = (requested ?? []).Distinct().ToList();
        if (ids.Count > MaxSignatories) return (ids, $"Choose at most {MaxSignatories} signatories.");
        if (ids.Count > 0 && await _db.Signatories.CountAsync(s => ids.Contains(s.Id)) != ids.Count)
        {
            return (ids, "One of the chosen signatories doesn't exist any more. Reload and choose again.");
        }
        if (!_enforceMandatory) return (ids, null);
        if (ids.Count == 0) return (ids, "Choose at least one signatory: every certificate names who signs it.");
        var missing = LayoutValidator.MissingCoreElements(layoutJson);
        if (missing.Count > 0) return (ids, $"The design must show: {string.Join(", ", missing)}.");
        return (ids, null);
    }

    private async Task<CertificateTemplateResponse> ToResponseAsync(CertificateTemplate t) =>
        ToResponse(t, await SignatorySnapshot.ForTemplateAsync(_db, t.SignatoryIds, HttpContext.RequestAborted),
            await _controlRequests.IsPendingAsync(ControlRequestKind.DisableFinalApproval, t.Id, HttpContext.RequestAborted));

    private static CertificateTemplateResponse ToResponse(CertificateTemplate t, IReadOnlyList<Signatory> signatories, bool offPending) => new(
        t.Id, t.Name, t.Description, t.Kind.ToString(), t.FieldSchemaJson, t.IsActive,
        t.TitleText, t.SubtitleText, t.AccentColor, t.FontFamily, t.LayoutJson, t.BackgroundImageUrl,
        t.SignatoryIds, signatories.Select(s => new SignatoryName(s.Name, s.Designation)).ToList(),
        t.RequiresFinalApproval, offPending);

    private async Task<TenantUserService.Actor> ActorAsync()
    {
        var id = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync() ?? "Unknown";
        return new TenantUserService.Actor(id, name);
    }
}
