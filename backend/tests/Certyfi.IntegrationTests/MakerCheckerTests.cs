using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves the maker-checker workflow (capability note 4.5.2) and public
// verification (4.5.4) end to end over real HTTP — a Registrar cannot
// approve their own draft, only Active/Suspended/Revoked credentials
// resolve publicly, and the public verify endpoint works with NO auth
// headers at all (proving the TenantResolutionMiddleware exemption wiring,
// not just the controller logic in isolation).
public class MakerCheckerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MakerCheckerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Registrar_CannotApprove_OwnDraftedCredential()
    {
        var (client, slug, tokenReg, _) = await SetUpTenantWithRegistrarAndApprover();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);

        var response = await PostAuthed(client, $"/api/v1/credentials/{credentialId}/approve", slug, tokenReg, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Approver_CanApprove_DifferentMakersDraft_AndVerifyReflectsActive()
    {
        var (client, slug, tokenReg, tokenApp) = await SetUpTenantWithRegistrarAndApprover();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);

        var approveResponse = await PostAuthed(client, $"/api/v1/credentials/{credentialId}/approve", slug, tokenApp, null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        // No Authorization header, no X-Tenant-Slug header — a genuinely
        // anonymous request, exactly as an external verifier would send.
        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var body = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Active", body!.Status);
    }

    [Fact]
    public async Task RejectedCredential_IsNotPubliclyVerifiable()
    {
        var (client, slug, tokenReg, tokenApp) = await SetUpTenantWithRegistrarAndApprover();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);

        var rejectResponse = await PostAuthed(client, $"/api/v1/credentials/{credentialId}/reject", slug, tokenApp,
            new RejectCredentialRequest("Duplicate request"));
        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        Assert.Equal(HttpStatusCode.NotFound, verifyResponse.StatusCode);
    }

    [Fact]
    public async Task PendingCredential_IsNotPubliclyVerifiable()
    {
        var (client, slug, tokenReg, _) = await SetUpTenantWithRegistrarAndApprover();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        Assert.Equal(HttpStatusCode.NotFound, verifyResponse.StatusCode);
    }

    [Fact]
    public async Task SuspendedCredential_ShowsAsSuspended_ThenReactivateRestoresActive()
    {
        var (client, slug, tokenReg, tokenApp) = await SetUpTenantWithRegistrarAndApprover();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);
        var approved = await (await PostAuthed(client, $"/api/v1/credentials/{credentialId}/approve", slug, tokenApp, null)).Content
            .ReadFromJsonAsync<CredentialResponse>();
        var code = approved!.VerificationCode;

        // Suspending needs the certificate's code typed to confirm.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAuthed(client, $"/api/v1/credentials/{credentialId}/suspend", slug, tokenApp,
            new SuspendCredentialRequest("Under review", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAuthed(client, $"/api/v1/credentials/{credentialId}/suspend", slug, tokenApp,
            new SuspendCredentialRequest("Under review", "2222-2222-2222"))).StatusCode);
        var suspendResponse = await PostAuthed(client, $"/api/v1/credentials/{credentialId}/suspend", slug, tokenApp,
            new SuspendCredentialRequest("Under review", code.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(HttpStatusCode.OK, suspendResponse.StatusCode);

        var suspendedVerify = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        var suspendedBody = await suspendedVerify.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Suspended", suspendedBody!.Status);

        var reactivateResponse = await PostAuthed(client, $"/api/v1/credentials/{credentialId}/reactivate", slug, tokenApp,
            new ReactivateCredentialRequest("Review finished", code));
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);

        var activeVerify = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        var activeBody = await activeVerify.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Active", activeBody!.Status);
    }

    [Fact]
    public async Task DirectRevoke_IsGone_RevokingNeedsARequest()
    {
        var (client, slug, tokenReg, tokenApp) = await SetUpTenantWithRegistrarAndApprover();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);
        await PostAuthed(client, $"/api/v1/credentials/{credentialId}/approve", slug, tokenApp, null);

        var revokeResponse = await PostAuthed(client, $"/api/v1/credentials/{credentialId}/revoke", slug, tokenApp, new { reason = "Issued in error" });
        Assert.True(revokeResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, revokeResponse.StatusCode.ToString());

        var verify = await (await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}")).Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Active", verify!.Status);
    }

    // Provisions a fresh tenant, then creates+logs-in a Registrar and an
    // Approver under it (the InstitutionAdmin created at provisioning is
    // used only to create those two — never to draft/approve anything
    // itself, keeping each test focused on the Registrar/Approver pair).
    private async Task<(HttpClient client, string slug, string tokenReg, string tokenApp)> SetUpTenantWithRegistrarAndApprover()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        const string regPassword = "Registrar123!";
        const string appPassword = "Approver123!";
        string slug;

        using (var setupScope = _factory.Services.CreateScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", adminPassword);
            slug = tenant.Slug;
        }

        var client = _factory.CreateClient();

        var adminLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"admin@{suffix}.test", adminPassword));
        var tokenAdmin = (await adminLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        await PostAuthed(client, "/api/v1/users", slug, tokenAdmin,
            new CreateUserRequest($"registrar@{suffix}.test", "Test Registrar", regPassword, "Registrar"));
        await PostAuthed(client, "/api/v1/users", slug, tokenAdmin,
            new CreateUserRequest($"approver@{suffix}.test", "Test Approver", appPassword, "Approver"));

        var regLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"registrar@{suffix}.test", regPassword));
        var appLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"approver@{suffix}.test", appPassword));

        var tokenReg = (await regLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var tokenApp = (await appLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        return (client, slug, tokenReg, tokenApp);
    }

    private static async Task<Guid> CreateAchievement(HttpClient client, string slug, string tokenReg)
    {
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Test Achievement", null, null, null, null, null));
        var body = await response.Content.ReadFromJsonAsync<AchievementResponse>();
        return body!.Id;
    }

    private static async Task<Guid> DraftCredential(HttpClient client, string slug, string tokenReg, Guid achievementId)
    {
        var response = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievementId, "Jane Doe", "jane@example.com", null));
        var body = await response.Content.ReadFromJsonAsync<CredentialResponse>();
        return body!.Id;
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, string tenantSlug, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostAuthed(HttpClient client, string url, string tenantSlug, string bearerToken, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
