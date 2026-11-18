using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.MultiTenancy;

// Resolves the tenant for every request except platform (super-admin) and
// health-check routes, BEFORE authentication runs — JWT validation for a
// tenant-scoped token still needs to know which tenant's signing context
// applies. Resolution order: X-Tenant-Slug header first (used by the
// Angular app and partner API clients), then subdomain, e.g. acme.certyfi.io.
//
// A request that needs a tenant but can't resolve one gets a 400 here,
// deliberately, rather than falling through to a default schema.
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    // /api/v1/verify is exempt because a public credential verifier is
    // anonymous and has no way to know to send X-Tenant-Slug — that route
    // takes the tenant slug from its own URL path instead and resolves the
    // tenant itself (see VerificationController). /api/v1/docs serves the
    // public Partner API documentation, which is the same for every tenant.
    // /api/v1/template-preview carries its tenant inside a signed link (see
    // TemplatePreviewController). /api/v1/student-sign-in finds the student's
    // institutions itself (StudentSignInController).
    private static readonly string[] ExemptPrefixes = { "/api/v1/platform", "/api/v1/verify", "/api/v1/access", "/api/v1/template-preview", "/api/v1/system-info", "/api/v1/student-sign-in", "/api/v1/docs", "/health", "/swagger" };

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext httpContext, PlatformDbContext platformDb, ITenantContext tenantContext)
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(httpContext);
            return;
        }

        var slug = ResolveSlug(httpContext);
        if (string.IsNullOrWhiteSpace(slug))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Tenant could not be resolved from request." });
            return;
        }

        var tenant = await platformDb.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug);

        if (tenant is { Status: Domain.Enums.TenantStatus.Suspended })
        {
            // Staff sign-in and the Partner API stop; public verification
            // (exempt from this middleware) keeps working.
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "This institution's Certyfi account is suspended. Contact Certyfi support.", code = "tenant_suspended" });
            return;
        }

        if (tenant is null || tenant.Status != Domain.Enums.TenantStatus.Active)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Tenant not found or not active." });
            return;
        }

        tenantContext.Set(tenant.Id, tenant.Slug, tenant.SchemaName);

        await _next(httpContext);
    }

    private static string? ResolveSlug(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("X-Tenant-Slug", out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString().Trim().ToLowerInvariant();
        }

        var host = httpContext.Request.Host.Host;
        var parts = host.Split('.');
        // e.g. acme.certyfi.io -> "acme". Adjust the depth check to match
        // the real deployed domain shape once that's decided.
        return parts.Length >= 3 ? parts[0].ToLowerInvariant() : null;
    }
}
