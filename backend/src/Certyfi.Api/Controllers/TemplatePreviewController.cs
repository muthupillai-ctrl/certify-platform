using Certyfi.Api.Auth;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Public, anonymous: what the template-preview page draws for a link a
// partner app got from POST /api/v1/partner/templates/{id}/preview-link.
// The signed link is the only credential; it names the institution, so this
// path is exempt from TenantResolutionMiddleware and resolves the tenant
// itself, like VerificationController. Returns only the template's design
// and the institution's public branding — the page fills in sample data.
[ApiController]
[Route("api/v1/template-preview")]
public class TemplatePreviewController : ControllerBase
{
    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbFactory;
    private readonly TemplatePreviewLink _previewLinks;

    public TemplatePreviewController(PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory, TemplatePreviewLink previewLinks)
    {
        _platformDb = platformDb;
        _tenantDbFactory = tenantDbFactory;
        _previewLinks = previewLinks;
    }

    [HttpGet("{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<TemplatePreviewResponse>> Get(string token, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";

        var target = _previewLinks.Read(token);
        if (target is null) return NotFound(new { error = "This preview link isn't valid." });
        if (target.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return StatusCode(StatusCodes.Status410Gone, new { error = "This preview link has expired. Open the preview again from the app you came from." });
        }

        var tenant = await _platformDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == target.TenantSlug && t.Status == TenantStatus.Active, ct);
        if (tenant is null) return NotFound(new { error = "This template is no longer available." });

        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var template = await tenantDb.CertificateTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == target.TemplateId && t.IsActive, ct);
        if (template is null) return NotFound(new { error = "This template is no longer available." });

        return Ok(new TemplatePreviewResponse(
            template.Name, template.Kind.ToString(), template.FieldSchemaJson,
            template.TitleText, template.SubtitleText, template.AccentColor, template.FontFamily, template.LayoutJson, template.BackgroundImageUrl,
            tenant.Name, tenant.BrandingLogoUrl, tenant.BrandingPrimaryColor, target.ExpiresAt,
            (await SignatorySnapshot.ForTemplateAsync(tenantDb, template.SignatoryIds, ct)).Select(s => new SignatoryName(s.Name, s.Designation)).ToList()));
    }
}
