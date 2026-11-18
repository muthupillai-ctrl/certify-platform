using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves the hash-chained, append-only ledger end to end: entries are
// appended in order with a valid chain for every credential lifecycle
// transition, the integrity-check endpoint reports the chain as intact for
// clean activity, and — the test that actually exercises the concurrency
// design (see LedgerService's advisory-lock reasoning) — concurrent writes
// against a REAL Testcontainers Postgres produce one valid chain, not a
// fork. This is the test that would have caught a SELECT ... FOR UPDATE
// race if that had been used instead of the advisory lock.
public class LedgerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public LedgerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CredentialLifecycle_AppendsLedgerEntriesInOrder_WithValidChain()
    {
        var (client, slug, tokenReg, tokenApp, tokenAdmin) = await SetUpTenant();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);

        await PostAuthed(client, $"/api/v1/credentials/{credentialId}/approve", slug, tokenApp, null);
        var code = (await (await GetAuthed(client, $"/api/v1/credentials/{credentialId}", slug, tokenApp)).Content
            .ReadFromJsonAsync<CredentialResponse>())!.VerificationCode;
        Assert.Equal(HttpStatusCode.OK, (await PostAuthed(client, $"/api/v1/credentials/{credentialId}/suspend", slug, tokenApp, new SuspendCredentialRequest("r", code))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAuthed(client, $"/api/v1/credentials/{credentialId}/reactivate", slug, tokenApp, new ReactivateCredentialRequest("r", code))).StatusCode);
        // Revoking is two-person: the approver asks, the admin approves.
        var request = await (await PostAuthed(client, "/api/v1/revocation-requests", slug, tokenApp,
            new CreateRevocationRequest(credentialId, "IssuedInError", "r", code))).Content.ReadFromJsonAsync<RevocationRequestResponse>();
        Assert.Equal(HttpStatusCode.OK, (await PostAuthed(client, $"/api/v1/revocation-requests/{request!.Id}/approve", slug, tokenAdmin, new ApproveRevocationRequest(code))).StatusCode);

        var response = await GetAuthed(client, $"/api/v1/credentials/{credentialId}/ledger", slug, tokenApp);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = (await response.Content.ReadFromJsonAsync<List<LedgerEntryResponse>>())!;

        Assert.Equal(6, entries.Count);
        Assert.Equal(["Requested", "Approved", "Suspended", "Reactivated", "RevocationRequested", "Revoked"], entries.Select(e => e.EventType));
        Assert.Equal(LedgerService.GenesisHash, entries[0].PreviousEntryHash);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EntryHash, entries[i].PreviousEntryHash);
        }
    }

    [Fact]
    public async Task IntegrityEndpoint_ReportsIntact_ForCleanChain()
    {
        var (client, slug, tokenReg, tokenApp, tokenAdmin) = await SetUpTenant();
        var achievementId = await CreateAchievement(client, slug, tokenReg);
        var credentialId = await DraftCredential(client, slug, tokenReg, achievementId);
        await PostAuthed(client, $"/api/v1/credentials/{credentialId}/approve", slug, tokenApp, null);

        var response = await GetAuthed(client, "/api/v1/ledger/integrity", slug, tokenAdmin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Certyfi.Infrastructure.Services.LedgerIntegrityReport>();
        Assert.True(body!.Intact);
        Assert.Null(body.BrokenAtSequenceNumber);
    }

    [Fact]
    public async Task IntegrityEndpoint_Requires_InstitutionAdmin()
    {
        var (client, slug, tokenReg, tokenApp, _) = await SetUpTenant();

        var registrarResponse = await GetAuthed(client, "/api/v1/ledger/integrity", slug, tokenReg);
        Assert.Equal(HttpStatusCode.Forbidden, registrarResponse.StatusCode);

        var approverResponse = await GetAuthed(client, "/api/v1/ledger/integrity", slug, tokenApp);
        Assert.Equal(HttpStatusCode.Forbidden, approverResponse.StatusCode);
    }

    [Fact]
    public async Task ConcurrentApprovals_ProduceASingleValidChain_NotAFork()
    {
        const int count = 20;
        var (client, slug, tokenReg, tokenApp, tokenAdmin) = await SetUpTenant();
        var achievementId = await CreateAchievement(client, slug, tokenReg);

        var credentialIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            credentialIds.Add(await DraftCredential(client, slug, tokenReg, achievementId));
        }

        // Fire all approvals concurrently against the real Testcontainers
        // Postgres CustomWebApplicationFactory already uses — this genuinely
        // exercises pg_advisory_xact_lock under real concurrent connections,
        // not a mock.
        var approveTasks = credentialIds.Select(id =>
            PostAuthed(client, $"/api/v1/credentials/{id}/approve", slug, tokenApp, null));
        var results = await Task.WhenAll(approveTasks);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var integrityResponse = await GetAuthed(client, "/api/v1/ledger/integrity", slug, tokenAdmin);
        var integrity = await integrityResponse.Content.ReadFromJsonAsync<Certyfi.Infrastructure.Services.LedgerIntegrityReport>();
        Assert.True(integrity!.Intact, integrity.Reason);
        // One "Requested" + one "Approved" entry per credential.
        Assert.Equal(count * 2, integrity.TotalEntries);
    }

    // Provisions a fresh tenant, then creates+logs-in a Registrar and an
    // Approver under it, alongside the InstitutionAdmin's own token (needed
    // here, unlike MakerCheckerTests, for the InstitutionAdmin-only
    // integrity endpoint).
    private async Task<(HttpClient client, string slug, string tokenReg, string tokenApp, string tokenAdmin)> SetUpTenant()
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

        return (client, slug, tokenReg, tokenApp, tokenAdmin);
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
            new CreateCredentialRequest(achievementId, "Ledger Test", "ledger@test.com", null));
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

    private static Task<HttpResponseMessage> GetAuthed(HttpClient client, string url, string tenantSlug, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
