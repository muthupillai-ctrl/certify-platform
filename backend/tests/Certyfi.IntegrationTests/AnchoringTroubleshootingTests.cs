using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// The SuperAdmin Anchoring page's troubleshooting endpoints: SuperAdmin
// only, the "needs attention" rules, proof download, and that re-verify
// refuses anything but a confirmed checkpoint. Checkpoints are inserted
// directly (no calendar traffic), so this runs offline; the live
// calendar/diagnosis path is covered by AnchoringTests and
// OpenTimestampsUpgradeTests.
public class AnchoringTroubleshootingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AnchoringTroubleshootingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TroubleshootingEndpoints_RequireSuperAdmin()
    {
        var t = await SetUp();

        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Client.GetAsync("/api/v1/platform/anchoring/status")).StatusCode);

        var asTenantAdmin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/platform/anchoring/checkpoints");
        asTenantAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.TenantAdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Client.SendAsync(asTenantAdmin)).StatusCode);

        var proofAsTenantAdmin = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/platform/anchoring/checkpoints/{t.Slug}/{t.OverdueId}/proof");
        proofAsTenantAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.TenantAdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Client.SendAsync(proofAsTenantAdmin)).StatusCode);
    }

    [Fact]
    public async Task AttentionFilter_FlagsOverduePendingAndUnverifiedConfirmed_ButNotHealthyOnes()
    {
        var t = await SetUp();

        var rows = await GetPlatform<List<CheckpointRow>>(t, $"/api/v1/platform/anchoring/checkpoints?tenant={t.Slug}&status=attention");

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == t.OverdueId && r.AttentionReason!.StartsWith("Pending for"));
        Assert.Contains(rows, r => r.Id == t.UnverifiedId && r.AttentionReason!.Contains("block check did not pass"));

        var all = await GetPlatform<List<CheckpointRow>>(t, $"/api/v1/platform/anchoring/checkpoints?tenant={t.Slug}");
        Assert.Equal(4, all.Count);
        Assert.Null(all.Single(r => r.Id == t.FreshId).AttentionReason);
        Assert.Null(all.Single(r => r.Id == t.HealthyId).AttentionReason);

        var status = await GetPlatform<AnchoringStatusResponse>(t, "/api/v1/platform/anchoring/status");
        Assert.True(status.Counts.NeedsAttention >= 2);
    }

    [Fact]
    public async Task Proof_DownloadsStoredBytes()
    {
        var t = await SetUp();

        var response = await SendPlatform(t, HttpMethod.Get, $"/api/v1/platform/anchoring/checkpoints/{t.Slug}/{t.OverdueId}/proof");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ProofBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.EndsWith(".ots", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Fact]
    public async Task Reverify_RefusesPendingCheckpoint_AndUnknownIdsAre404()
    {
        var t = await SetUp();

        Assert.Equal(HttpStatusCode.Conflict,
            (await SendPlatform(t, HttpMethod.Post, $"/api/v1/platform/anchoring/checkpoints/{t.Slug}/{t.OverdueId}/reverify")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendPlatform(t, HttpMethod.Post, $"/api/v1/platform/anchoring/checkpoints/{t.Slug}/{Guid.NewGuid()}/reverify")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendPlatform(t, HttpMethod.Get, "/api/v1/platform/anchoring/checkpoints?status=bogus")).StatusCode);
    }

    // ---------- helpers ----------

    private static readonly byte[] ProofBytes = { 0x00, 0x4f, 0x70, 0x65, 0x6e, 0x54, 0x69, 0x6d, 0x65, 0x73 };

    private record Setup(HttpClient Client, string Slug, string PlatformToken, string TenantAdminToken,
        Guid OverdueId, Guid FreshId, Guid UnverifiedId, Guid HealthyId);

    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        string slug, platformToken;
        var overdue = Checkpoint(1, "Pending", DateTimeOffset.UtcNow.AddDays(-2), verified: false);
        var fresh = Checkpoint(2, "Pending", DateTimeOffset.UtcNow.AddMinutes(-10), verified: false);
        var unverified = Checkpoint(3, "Confirmed", DateTimeOffset.UtcNow.AddDays(-1), verified: false);
        var healthy = Checkpoint(4, "Confirmed", DateTimeOffset.UtcNow.AddDays(-1), verified: true);

        using (var scope = _factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", adminPassword);
            slug = tenant.Slug;

            await using var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>()
                .CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            tenantDb.LedgerCheckpoints.AddRange(overdue, fresh, unverified, healthy);
            await tenantDb.SaveChangesAsync();

            platformToken = TestPlatformAdmins.CreateWithToken(_factory.Services, $"troubleshoot-{suffix}@platform.test", "Troubleshooting Test Admin");
        }

        var client = _factory.CreateClient();
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new TenantLoginRequest($"admin@{suffix}.test", adminPassword)),
        };
        login.Headers.Add("X-Tenant-Slug", slug);
        var tenantAdminToken = (await (await client.SendAsync(login)).Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        return new Setup(client, slug, platformToken, tenantAdminToken, overdue.Id, fresh.Id, unverified.Id, healthy.Id);
    }

    private static LedgerCheckpoint Checkpoint(long upTo, string status, DateTimeOffset createdAt, bool verified) => new()
    {
        UpToSequenceNumber = upTo,
        CheckpointHash = new string('a', 64),
        Status = status,
        OtsProof = ProofBytes,
        CreatedAt = createdAt,
        ConfirmedAt = status == "Confirmed" ? createdAt.AddHours(3) : null,
        BitcoinBlockHeight = status == "Confirmed" ? 968300 : null,
        MerkleRootVerified = verified,
    };

    private static Task<HttpResponseMessage> SendPlatform(Setup t, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.PlatformToken);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> GetPlatform<T>(Setup t, string url)
    {
        var response = await SendPlatform(t, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
