using System.Text.RegularExpressions;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped (goes through the normal TenantResolutionMiddleware +
// TenantClaimGuardMiddleware pipeline — NOT exempt, unlike
// VerificationController). Unlike every other tenant-scoped controller in
// this codebase, this one queries PlatformDbContext directly rather than
// TenantDbContext — Tenant rows (including BrandingLogoUrl/
// BrandingPrimaryColor, both present in platform.tenants since the very
// first migration but unused until now) live in the platform schema, not a
// tenant schema. Safe: TenantClaimGuardMiddleware has already rejected any
// request where the JWT's tenant_id claim doesn't match the resolved
// tenant before this action runs, so filtering
// WHERE Id == tenantContext.TenantId can never touch another tenant's row
// for a legitimately-issued token.
[ApiController]
[Route("api/v1/tenant-settings")]
public class TenantSettingsController : ControllerBase
{
    private readonly PlatformDbContext _platformDb;
    private readonly ITenantContext _tenantContext;

    public TenantSettingsController(PlatformDbContext platformDb, ITenantContext tenantContext)
    {
        _platformDb = platformDb;
        _tenantContext = tenantContext;
    }

    [HttpGet("branding")]
    [Authorize(Roles = $"{SystemRole.Registrar},{SystemRole.Approver},{SystemRole.InstitutionAdmin},{SystemRole.Learner}")]
    public async Task<ActionResult<TenantBrandingResponse>> GetBranding(CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        if (tenant is null) return NotFound();

        return Ok(new TenantBrandingResponse(tenant.BrandingLogoUrl, tenant.BrandingPrimaryColor, tenant.Name));
    }

    // The institution's own profile (all fields, including private contact
    // details). Public fields also appear on verification pages.
    [HttpGet("profile")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<TenantProfileResponse>> GetProfile(CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        return tenant is null ? NotFound() : Ok(TenantProfileResponse.From(tenant));
    }

    [HttpPut("profile")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<TenantProfileResponse>> UpdateProfile(TenantProfileInput request, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        if (tenant is null) return NotFound();
        if (TenantProfileValidator.Apply(tenant, request) is { } error) return BadRequest(new { error });
        await _platformDb.SaveChangesAsync(ct);
        return Ok(TenantProfileResponse.From(tenant));
    }

    public const int MaxPaymentDays = 365;

    [HttpGet("payments")]
    [Authorize(Roles = $"{SystemRole.InstitutionAdmin},{SystemRole.Registrar},{SystemRole.Approver}")]
    public async Task<ActionResult<TenantPaymentSettings>> GetPayments(CancellationToken ct)
    {
        var days = await _platformDb.Tenants.AsNoTracking().Where(t => t.Id == _tenantContext.TenantId).Select(t => (int?)t.PaymentDays).FirstOrDefaultAsync(ct);
        return days is null ? NotFound() : Ok(new TenantPaymentSettings(days.Value));
    }

    // Applies to certificates requested from now on; ones already waiting
    // keep the due date they were given.
    [HttpPut("payments")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<TenantPaymentSettings>> UpdatePayments(TenantPaymentSettings request, CancellationToken ct)
    {
        if (request.PaymentDays is < 0 or > MaxPaymentDays)
        {
            return BadRequest(new { error = $"Days to pay must be between 0 (never cancel) and {MaxPaymentDays}." });
        }
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        if (tenant is null) return NotFound();
        tenant.PaymentDays = request.PaymentDays;
        await _platformDb.SaveChangesAsync(ct);
        return Ok(new TenantPaymentSettings(tenant.PaymentDays));
    }

    [HttpPut("branding")]
    [Authorize(Roles = SystemRole.InstitutionAdmin)]
    public async Task<ActionResult<TenantBrandingResponse>> UpdateBranding(UpdateTenantBrandingRequest request, CancellationToken ct)
    {
        // Server-side format validation — defense-in-depth beyond Angular's
        // client-side URL sanitizer on the [src] binding that renders this,
        // since this URL could later be consumed by something other than
        // the Angular verify page (partner API, PDF export, email
        // template) that wouldn't get that protection.
        if (!string.IsNullOrEmpty(request.LogoUrl) && !Regex.IsMatch(request.LogoUrl, @"^https?://"))
        {
            return BadRequest(new { error = "Logo URL must start with http:// or https://." });
        }
        if (!string.IsNullOrEmpty(request.PrimaryColor) && !Regex.IsMatch(request.PrimaryColor, @"^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$"))
        {
            return BadRequest(new { error = "Primary color must be a hex color, e.g. #1a2b3c." });
        }

        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        if (tenant is null) return NotFound();

        tenant.BrandingLogoUrl = string.IsNullOrEmpty(request.LogoUrl) ? null : request.LogoUrl;
        tenant.BrandingPrimaryColor = string.IsNullOrEmpty(request.PrimaryColor) ? null : request.PrimaryColor;
        await _platformDb.SaveChangesAsync(ct);

        return Ok(new TenantBrandingResponse(tenant.BrandingLogoUrl, tenant.BrandingPrimaryColor, tenant.Name));
    }
}
