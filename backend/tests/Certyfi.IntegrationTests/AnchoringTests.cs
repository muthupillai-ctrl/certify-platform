using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Anchoring.OpenTimestamps;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Exercises the checkpoint/upgrade logic for real, against real public
// OpenTimestamps calendar servers — no mocking of the network calls, same
// "prove it against the genuine external thing, not a stand-in" standard
// SigningTests.cs applies to ECDSA. The submission path (checkpoint-now)
// always completes within a normal test timeout; full Bitcoin confirmation
// takes hours in reality, so only the *shape* of that later transition is
// covered here (OpenTimestampsSerializationTests.HandBuiltTree... exercises
// the Confirmed-detection logic itself with a synthetic Bitcoin
// attestation, since waiting for a real one isn't feasible in a test run).
public class AnchoringTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AnchoringTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CheckpointNow_CreatesCheckpoint_ForTenantWithLedgerActivity_AndAnchorProofBecomesDownloadable()
    {
        var (client, slug, tokenReg, tokenAdmin, platformToken) = await SetUpTenantAndPlatformAdmin();
        var credentialId = await DraftAndApproveCredential(client, slug, tokenReg, tokenAdmin);

        // Before any checkpoint has run, the credential's own verify
        // response must say so, and the proof download must 404.
        var verifyBefore = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        var verifyBeforeBody = await verifyBefore.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("NotYetCheckpointed", verifyBeforeBody!.AnchorStatus);

        var proofBefore = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}/anchor-proof");
        Assert.Equal(HttpStatusCode.NotFound, proofBefore.StatusCode);

        var checkpointResponse = await PostPlatform(client, "/api/v1/platform/anchoring/checkpoint-now", platformToken);
        Assert.Equal(HttpStatusCode.OK, checkpointResponse.StatusCode);
        var checkpointBody = await checkpointResponse.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        Assert.True(checkpointBody!["checkpointsCreated"] >= 1, "Expected at least our own tenant's checkpoint to be created.");

        var verifyAfter = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        var verifyAfterBody = await verifyAfter.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Pending", verifyAfterBody!.AnchorStatus);

        // The proof is a real, independently-parseable OpenTimestamps
        // DetachedTimestampFile — not just "some bytes got stored". Up to
        // one PendingAttestation per calendar server that accepted the
        // submission (SubmitToAllAsync merges all of them into one tree),
        // so at least one but not necessarily exactly one.
        var proofAfter = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}/anchor-proof");
        Assert.Equal(HttpStatusCode.OK, proofAfter.StatusCode);
        var proofBytes = await proofAfter.Content.ReadAsByteArrayAsync();
        var parsed = DetachedTimestampFile.Deserialize(proofBytes);
        var pendingAttestations = parsed.Timestamp.AllAttestations()
            .Select(a => a.Attestation).OfType<PendingAttestation>().ToList();
        Assert.NotEmpty(pendingAttestations);
        Assert.All(pendingAttestations, p => Assert.StartsWith("https://", p.Uri));
    }

    [Fact]
    public async Task CheckpointNow_IsIdempotent_WhenNoNewLedgerActivity()
    {
        var (client, slug, tokenReg, tokenAdmin, platformToken) = await SetUpTenantAndPlatformAdmin();
        await DraftAndApproveCredential(client, slug, tokenReg, tokenAdmin);

        var first = await PostPlatform(client, "/api/v1/platform/anchoring/checkpoint-now", platformToken);
        var firstBody = await first.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        Assert.True(firstBody!["checkpointsCreated"] >= 1);

        // No new ledger activity since — must not create a second checkpoint
        // for the same tenant.
        var second = await PostPlatform(client, "/api/v1/platform/anchoring/checkpoint-now", platformToken);
        var secondBody = await second.Content.ReadFromJsonAsync<Dictionary<string, int>>();
        Assert.Equal(0, secondBody!["checkpointsCreated"]);
    }

    [Fact]
    public async Task AnchoringEndpoints_RequireSuperAdmin()
    {
        var (client, slug, _, tokenAdmin, _) = await SetUpTenantAndPlatformAdmin();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/anchoring/checkpoint-now");
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Guid> DraftAndApproveCredential(HttpClient client, string slug, string tokenReg, string tokenAdmin)
    {
        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Anchored Achievement", null, null, null, null, null));
        var achievement = await achievementResponse.Content.ReadFromJsonAsync<AchievementResponse>();

        var credentialResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievement!.Id, "Anchor Test", "anchor@example.com", null));
        var credential = await credentialResponse.Content.ReadFromJsonAsync<CredentialResponse>();

        var approveResponse = await PostAuthed(client, $"/api/v1/credentials/{credential!.Id}/approve", slug, tokenAdmin, null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        return credential.Id;
    }

    private async Task<(HttpClient client, string slug, string tokenReg, string tokenAdmin, string platformToken)> SetUpTenantAndPlatformAdmin()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        const string regPassword = "Registrar123!";
        string slug;
        string platformToken;

        using (var setupScope = _factory.Services.CreateScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", adminPassword);
            slug = tenant.Slug;

            // Minted directly, same as PrivilegeEscalationTests — a genuine
            // platform-admin token needs no tenant_id claim and no seeded
            // PlatformAdmin row to validate (JWT bearer auth here checks the
            // signature/claims, not DB existence per request).
            platformToken = TestPlatformAdmins.CreateWithToken(_factory.Services, $"anchoring-admin-{suffix}@platform.test", "Anchoring Test Admin");
        }

        var client = _factory.CreateClient();

        var adminLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest($"admin@{suffix}.test", adminPassword)) };
        adminLoginRequest.Headers.Add("X-Tenant-Slug", slug);
        var adminLogin = await client.SendAsync(adminLoginRequest);
        var tokenAdmin = (await adminLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var createUserRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users") { Content = JsonContent.Create(new CreateUserRequest($"registrar-{suffix}@{suffix}.test", "Test Registrar", regPassword, "Registrar")) };
        createUserRequest.Headers.Add("X-Tenant-Slug", slug);
        createUserRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        await client.SendAsync(createUserRequest);

        var regLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest($"registrar-{suffix}@{suffix}.test", regPassword)) };
        regLoginRequest.Headers.Add("X-Tenant-Slug", slug);
        var regLogin = await client.SendAsync(regLoginRequest);
        var tokenReg = (await regLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        return (client, slug, tokenReg, tokenAdmin, platformToken);
    }

    private static Task<HttpResponseMessage> PostAuthed(HttpClient client, string url, string tenantSlug, string bearerToken, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostPlatform(HttpClient client, string url, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
