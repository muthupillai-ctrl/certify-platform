using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;

namespace Certyfi.Api.Middleware;

// Runs AFTER authentication. Two separate checks, both load-bearing:
//
// 1. A tenant-issued token (any token carrying a tenant_id claim — see
//    JwtTokenService.IssueTenantUserToken) must never be honoured as a
//    platform-admin credential, no matter what role claims it also carries.
//    This is checked UNCONDITIONALLY, including on routes
//    TenantResolutionMiddleware exempts from tenant resolution (e.g.
//    /api/v1/platform/*, where ITenantContext.IsResolved is always false).
//    That exemption is exactly where a tenant-assigned SuperAdmin role claim
//    would otherwise slip past plain [Authorize(Roles=SuperAdmin)]
//    unnoticed — SystemRole.TenantAssignable now keeps SuperAdmin from ever
//    being assignable within a tenant in the first place, but this check is
//    the actual enforcement point and must not depend on that alone.
//
// 2. The per-request tenant-isolation check: a tenant token's tenant_id
//    claim must match whichever tenant was resolved for THIS request
//    (header/subdomain). Without this, a valid token for Tenant A presented
//    against Tenant B's subdomain would still pass standard [Authorize]
//    checks, since JWT validation alone doesn't know about tenancy.
public class TenantClaimGuardMiddleware
{
    private readonly RequestDelegate _next;

    public TenantClaimGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext httpContext, ITenantContext tenantContext)
    {
        var user = httpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await _next(httpContext);
            return;
        }

        var tenantIdClaim = user.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
        var isTenantIssuedToken = tenantIdClaim is not null;

        if (isTenantIssuedToken && user.IsInRole(SystemRole.SuperAdmin))
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Tenant-issued tokens cannot hold platform-admin privileges." });
            return;
        }

        if (isTenantIssuedToken && tenantContext.IsResolved && tenantIdClaim != tenantContext.TenantId.ToString())
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Token tenant does not match request tenant." });
            return;
        }

        await _next(httpContext);
    }
}
