using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// README "Known gaps" #6: tenant isolation is this system's single most
// important correctness property — a JWT issued for one institution must
// never be honoured against another institution's resolved tenant. This is
// enforced by TenantClaimGuardMiddleware, which cross-checks the token's
// tenant_id claim against the tenant TenantResolutionMiddleware resolved
// from the request. This test proves that end to end over real HTTP, rather
// than only asserting the middleware's logic in isolation.
public class TenantIsolationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TenantIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TenantAToken_IsRejected_WhenPresentedAgainstTenantBsResolvedTenant()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string passwordA = "TenantAAdmin123!";
        const string passwordB = "TenantBAdmin123!";

        // Provision directly through the service, not the HTTP API — that
        // sidesteps needing a SuperAdmin login just to set up test fixtures,
        // and exercises the exact same provisioning path used in production.
        using (var setupScope = _factory.Services.CreateScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            await provisioning.ProvisionAsync($"Tenant A {suffix}", $"tenant-a-{suffix}", "admin@a.test", passwordA);
            await provisioning.ProvisionAsync($"Tenant B {suffix}", $"tenant-b-{suffix}", "admin@b.test", passwordB);
        }

        var tenantASlug = $"tenant-a-{suffix}";
        var tenantBSlug = $"tenant-b-{suffix}";
        var client = _factory.CreateClient();

        var loginResponse = await PostJson(client, "/api/v1/auth/login", tenantASlug,
            new TenantLoginRequest("admin@a.test", passwordA));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        var tokenA = login!.AccessToken;

        // Sanity check: token A against ITS OWN tenant must succeed — this
        // guards against the isolation assertion below passing for the wrong
        // reason (e.g. the endpoint being broken or always 403ing).
        var ownTenantResponse = await Get(client, "/api/v1/users", tenantASlug, tokenA);
        Assert.Equal(HttpStatusCode.OK, ownTenantResponse.StatusCode);

        // The actual isolation guarantee under test: token A, presented
        // against tenant B's resolved tenant, must be rejected.
        var crossTenantResponse = await Get(client, "/api/v1/users", tenantBSlug, tokenA);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantResponse.StatusCode);
    }

    [Fact]
    public async Task TenantAToken_CannotUpdateTenantBsBranding_ViaTenantSettingsController()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string passwordA = "TenantAAdmin123!";
        const string passwordB = "TenantBAdmin123!";

        using (var setupScope = _factory.Services.CreateScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            await provisioning.ProvisionAsync($"Tenant A {suffix}", $"tenant-a-{suffix}", "admin@a.test", passwordA);
            await provisioning.ProvisionAsync($"Tenant B {suffix}", $"tenant-b-{suffix}", "admin@b.test", passwordB);
        }

        var tenantASlug = $"tenant-a-{suffix}";
        var tenantBSlug = $"tenant-b-{suffix}";
        var client = _factory.CreateClient();

        var loginResponse = await PostJson(client, "/api/v1/auth/login", tenantASlug,
            new TenantLoginRequest("admin@a.test", passwordA));
        var tokenA = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        // TenantSettingsController queries PlatformDbContext directly rather
        // than TenantDbContext (see its own comment on why that's safe) —
        // this is the test that proves it: tenant A's InstitutionAdmin token,
        // presented against tenant B's resolved tenant, must never be able to
        // overwrite tenant B's branding row.
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tenant-settings/branding")
        {
            Content = JsonContent.Create(new UpdateTenantBrandingRequest("https://evil.example.com/logo.png", "#ff0000"))
        };
        request.Headers.Add("X-Tenant-Slug", tenantBSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        var crossTenantResponse = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, crossTenantResponse.StatusCode);
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, string tenantSlug, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string tenantSlug, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
