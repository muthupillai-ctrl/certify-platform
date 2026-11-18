using Certyfi.Infrastructure.Auth;

namespace Certyfi.Api.Middleware;

// A token issued for a temporary password (after an admin reset) carries
// JwtTokenService.PasswordChangeClaim. Until a new password is chosen, the
// only thing it can do is change the password — enforced here, for every
// endpoint, rather than trusting the frontend to redirect.
public class PasswordChangeRequiredMiddleware
{
    private const string ChangePasswordPath = "/api/v1/auth/change-password";
    // Platform accounts: their own change-password, plus reading the profile
    // page it lives on.
    private const string PlatformChangePasswordPath = "/api/v1/platform/account/change-password";
    private const string PlatformAccountPath = "/api/v1/platform/account";
    private readonly RequestDelegate _next;

    public PasswordChangeRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext httpContext)
    {
        if (httpContext.User.HasClaim(JwtTokenService.PasswordChangeClaim, "required")
            && !httpContext.Request.Path.Equals(ChangePasswordPath, StringComparison.OrdinalIgnoreCase)
            && !httpContext.Request.Path.Equals(PlatformChangePasswordPath, StringComparison.OrdinalIgnoreCase)
            && !(HttpMethods.IsGet(httpContext.Request.Method) && httpContext.Request.Path.Equals(PlatformAccountPath, StringComparison.OrdinalIgnoreCase)))
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "You must choose a new password before continuing.", code = "password_change_required" });
            return;
        }
        await _next(httpContext);
    }
}
