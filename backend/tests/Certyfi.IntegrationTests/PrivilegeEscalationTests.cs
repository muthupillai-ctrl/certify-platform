using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// A tenant-issued token must never be usable as a platform-admin credential,
// no matter what role claims it carries. Two layers, tested separately:
// (1) the API-level guard — an InstitutionAdmin cannot create a same-tenant
//     user with role "SuperAdmin" (SystemRole.TenantAssignable excludes it);
// (2) the belt-and-suspenders guard — even a token that somehow carries both
//     a tenant_id claim and a SuperAdmin role claim (simulating layer 1
//     being bypassed) is still rejected by TenantClaimGuardMiddleware
//     against a platform-only endpoint.
public class PrivilegeEscalationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrivilegeEscalationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InstitutionAdmin_CannotCreateUser_WithSuperAdminRole()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string password = "TenantAdmin123!";
        string slug;

        using (var setupScope = _factory.Services.CreateScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", "admin@t.test", password);
            slug = tenant.Slug;
        }

        var client = _factory.CreateClient();
        var loginResponse = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest("admin@t.test", password));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var token = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var createResponse = await PostJsonAuthed(client, "/api/v1/users", slug, token,
            new CreateUserRequest("escalate@t.test", "Escalate", "Password123!", SystemRole.SuperAdmin));

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
    }

    [Fact]
    public async Task TenantIssuedToken_WithSuperAdminRoleClaim_IsRejectedAgainstPlatformEndpoint()
    {
        using var scope = _factory.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", "admin@t.test", "Password123!");

        var forgedToken = jwt.IssueTenantUserToken(
            user: new ApplicationUser { Email = "admin@t.test", PasswordHash = "x", FullName = "x" },
            tenantId: tenant.Id,
            tenantSlug: tenant.Slug,
            tenantSchema: tenant.SchemaName,
            roles: new[] { SystemRole.SuperAdmin });

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/platform/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forgedToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, string tenantSlug, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostJsonAuthed(HttpClient client, string url, string tenantSlug, string bearerToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
