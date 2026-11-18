using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Certyfi.Api.Auth;

// Authenticates Partner API requests from the X-Api-Key header. Only
// endpoints that name this scheme explicitly use it (PartnerController) —
// JWT stays the default scheme, so an API key can never reach a staff
// endpoint, and a staff JWT can never reach a partner endpoint.
//
// Tenant isolation: TenantResolutionMiddleware has already resolved the
// tenant from X-Tenant-Slug, and the key is looked up ONLY in that tenant's
// schema. A key issued by tenant A presented with tenant B's slug simply
// isn't found. That's why this handler doesn't need TenantClaimGuardMiddleware
// (which only sees the default JWT scheme's user anyway).
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ScopeClaim = "scope";
    public const string ApiClientIdClaim = "api_client_id";

    // LastUsedAt is informational (shown in the admin UI) — writing it on
    // every request would turn every partner read into a database write.
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenantContext;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        TenantDbContext db, ITenantContext tenantContext)
        : base(options, logger, encoder)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValue) || string.IsNullOrWhiteSpace(headerValue))
        {
            return AuthenticateResult.NoResult();
        }
        if (!_tenantContext.IsResolved)
        {
            return AuthenticateResult.Fail("Tenant not resolved.");
        }

        var presentedKey = headerValue.ToString().Trim();
        if (!ApiKeyGenerator.TryParsePrefix(presentedKey, out var keyPrefix))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var client = await _db.ApiClients.AsNoTracking()
            .Include(c => c.ServiceUser)
            .FirstOrDefaultAsync(c => c.KeyPrefix == keyPrefix, Context.RequestAborted);

        if (client is null || client.RevokedAt is not null || !client.ServiceUser.IsActive
            || !ApiKeyGenerator.Matches(presentedKey, client.SecretHash))
        {
            // One message for every failure — don't reveal whether a prefix
            // exists or a key was revoked.
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var now = DateTimeOffset.UtcNow;
        if (client.LastUsedAt is null || now - client.LastUsedAt > LastUsedWriteInterval)
        {
            await _db.ApiClients.Where(c => c.Id == client.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now), Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, client.ServiceUserId.ToString()),
            new(ApiClientIdClaim, client.Id.ToString()),
            new("tenant_id", _tenantContext.TenantId.ToString()),
            new(ClaimTypes.Role, SystemRole.Developer),
        };
        claims.AddRange(client.Scopes.Select(s => new Claim(ScopeClaim, s)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new { error = "A valid API key is required in the X-Api-Key header." });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new { error = "This API key does not have the scope this endpoint requires." });
    }
}
