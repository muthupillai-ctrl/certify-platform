using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped. Achievement is a reusable template (course/qualification,
// criteria, validity period, custom fields) that Credentials reference —
// see the capability note 4.5.1. Learner can List (needed to pick an
// achievement when drafting a credential) but NOT Create/Update — those
// stay Registrar/InstitutionAdmin only, narrower than credential drafting.
// No deactivate endpoint yet — Update (amend) covers editing only.
[ApiController]
[Route("api/v1/achievements")]
[Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Approver},{SystemRole.InstitutionAdmin},{SystemRole.Learner}")]
public class AchievementsController : ControllerBase
{
    private readonly TenantDbContext _db;

    public AchievementsController(TenantDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AchievementResponse>>> List()
    {
        var achievements = await _db.Achievements
            .Where(a => a.IsActive)
            .OrderBy(a => a.Title)
            .Select(a => new AchievementResponse(a.Id, a.Title, a.Description, a.CriteriaNarrative, a.ValidityPeriodMonths, a.IsActive, a.FieldSchemaJson, a.TemplateKind.ToString()))
            .ToListAsync();

        return Ok(achievements);
    }

    [HttpPost]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<AchievementResponse>> Create(CreateAchievementRequest request)
    {
        var fieldSchemaJson = request.FieldSchemaJson ?? "[]";
        if (!FieldSchemaValidator.TryValidateSchema(fieldSchemaJson, out var error))
        {
            return BadRequest(new { error });
        }

        // The template only pre-fills the client-side form — whatever
        // fields the client actually submits (already validated above) is
        // what gets persisted. Kind and the template's visual design ARE
        // copied server-side, since they decide the verify-page layout and
        // must reflect a real, tenant-owned template, not a client-supplied
        // string — same snapshot as CredentialsController.CreateFromTemplate.
        var templateKind = TemplateKind.Certificate;
        var templateTitleText = request.Title;
        string? templateSubtitleText = null;
        string? templateAccentColor = null;
        string? templateFontFamily = null;
        var templateLayoutJson = DefaultLayouts.Generate(templateKind, fieldSchemaJson);
        string? templateBackgroundImageUrl = null;
        if (request.TemplateId is { } templateId)
        {
            var template = await _db.CertificateTemplates.FirstOrDefaultAsync(t => t.Id == templateId);
            if (template is null) return BadRequest(new { error = "Unknown certificate template." });
            templateKind = template.Kind;
            templateTitleText = template.TitleText;
            templateSubtitleText = template.SubtitleText;
            templateAccentColor = template.AccentColor;
            templateFontFamily = template.FontFamily;
            templateLayoutJson = template.LayoutJson;
            templateBackgroundImageUrl = template.BackgroundImageUrl;
        }

        var achievement = new Achievement
        {
            Title = request.Title,
            Description = request.Description,
            CriteriaNarrative = request.CriteriaNarrative,
            ValidityPeriodMonths = request.ValidityPeriodMonths,
            FieldSchemaJson = fieldSchemaJson,
            TemplateId = request.TemplateId,
            TemplateKind = templateKind,
            TemplateTitleText = templateTitleText,
            TemplateSubtitleText = templateSubtitleText,
            TemplateAccentColor = templateAccentColor,
            TemplateFontFamily = templateFontFamily,
            TemplateLayoutJson = templateLayoutJson,
            TemplateBackgroundImageUrl = templateBackgroundImageUrl,
        };
        _db.Achievements.Add(achievement);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), new AchievementResponse(
            achievement.Id, achievement.Title, achievement.Description, achievement.CriteriaNarrative,
            achievement.ValidityPeriodMonths, achievement.IsActive, achievement.FieldSchemaJson, achievement.TemplateKind.ToString()));
    }

    // Amend — everything but TemplateId/TemplateKind is editable, including
    // the field schema. Safe to change the schema at any time because
    // Credential.FieldSchemaJson snapshots it at draft time; an
    // already-issued credential keeps rendering with whatever schema was in
    // effect when IT was created, never this achievement's current one.
    // Re-templating an achievement (changing which CertificateTemplate it
    // was derived from) is deliberately out of scope — that's a riskier,
    // separate operation.
    [HttpPut("{id:guid}")]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.InstitutionAdmin}")]
    public async Task<ActionResult<AchievementResponse>> Update(Guid id, UpdateAchievementRequest request)
    {
        var achievement = await _db.Achievements.FirstOrDefaultAsync(a => a.Id == id);
        if (achievement is null) return NotFound();

        var fieldSchemaJson = request.FieldSchemaJson ?? "[]";
        if (!FieldSchemaValidator.TryValidateSchema(fieldSchemaJson, out var error))
        {
            return BadRequest(new { error });
        }

        achievement.Title = request.Title;
        achievement.Description = request.Description;
        achievement.CriteriaNarrative = request.CriteriaNarrative;
        achievement.ValidityPeriodMonths = request.ValidityPeriodMonths;
        achievement.FieldSchemaJson = fieldSchemaJson;
        await _db.SaveChangesAsync();

        return Ok(new AchievementResponse(
            achievement.Id, achievement.Title, achievement.Description, achievement.CriteriaNarrative,
            achievement.ValidityPeriodMonths, achievement.IsActive, achievement.FieldSchemaJson, achievement.TemplateKind.ToString()));
    }
}
