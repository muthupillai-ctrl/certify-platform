using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// The public ways to check a certificate beyond opening its link:
// look-up by printed code, the signed offline summary in the QR code, the
// Open Badges 3.0 credential file, the institution's public keys, and the
// verification history staff see. Signatures are checked here the way an
// outside verifier would: raw ES256 against the published JWK, not through
// any Certyfi code.
public class VerificationOptionsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public VerificationOptionsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CodeLookup_FindsApprovedCertificate_InAnyTypedForm_AndNothingElse()
    {
        var t = await SetUp();
        var approved = await IssueAndApprove(t);
        var pending = await Draft(t);

        Assert.Matches("^[2-9A-Z]{4}-[2-9A-Z]{4}-[2-9A-Z]{4}$", approved.VerificationCode);

        var typed = approved.VerificationCode.Replace("-", " ").ToLowerInvariant();
        var found = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup/{Uri.EscapeDataString(typed)}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(approved.Id, (await found.Content.ReadFromJsonAsync<VerificationLookupResponse>())!.CredentialId);

        // Pending certificates have codes too, but can't be looked up.
        Assert.Equal(HttpStatusCode.NotFound, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup/{pending.VerificationCode}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup/AAAA-AAAA-AAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup/not-a-code")).StatusCode);
    }

    [Fact]
    public async Task OfflineProof_IsSignedByInstitutionKey_AndBoundToThisCertificate()
    {
        var t = await SetUp();
        var approved = await IssueAndApprove(t);

        var verify = await VerifyAsPage(t, approved.Id);
        Assert.NotNull(verify.OfflineProof);
        var parts = verify.OfflineProof!.Split('.');
        Assert.Equal(2, parts.Length);

        var summary = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
        Assert.Equal(approved.VerificationCode.Replace("-", ""), summary.GetProperty("c").GetString());
        Assert.Equal("Jane Doe", summary.GetProperty("n").GetString());
        Assert.Equal("Course Completion", summary.GetProperty("a").GetString());
        Assert.False(summary.TryGetProperty("email", out _));

        using var key = await PublicKey(t, summary.GetProperty("k").GetString()!);
        var signature = Base64UrlDecode(parts[1]);
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{t.Slug}/{approved.Id}#{parts[0]}"), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // Moved to another certificate's link, the same proof no longer verifies.
        Assert.False(key.VerifyData(Encoding.ASCII.GetBytes($"{t.Slug}/{Guid.NewGuid()}#{parts[0]}"), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task OpenBadgeCredential_IsAValidEs256Jwt_WithHashedEmail()
    {
        var t = await SetUp();
        var approved = await IssueAndApprove(t);

        var response = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{approved.Id}/credential");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith(".jwt", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var jwt = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("jane@example.com", jwt);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        var kid = header.GetProperty("kid").GetString()!;
        Assert.EndsWith($"/api/v1/verify/{t.Slug}/jwks/v1", kid);

        using var key = await PublicKey(t, "v1");
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64UrlDecode(parts[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        var claims = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
        var vc = claims.GetProperty("vc");
        Assert.Contains("OpenBadgeCredential", vc.GetProperty("type").EnumerateArray().Select(e => e.GetString()));
        Assert.EndsWith($"/verify/{t.Slug}/{approved.Id}", vc.GetProperty("id").GetString());
        Assert.Equal(vc.GetProperty("id").GetString(), claims.GetProperty("jti").GetString());
        var identifiers = vc.GetProperty("credentialSubject").GetProperty("identifier").EnumerateArray().ToList();
        var email = identifiers.Single(i => i.GetProperty("identityType").GetString() == "emailAddress");
        Assert.True(email.GetProperty("hashed").GetBoolean());
        var salt = email.GetProperty("salt").GetString()!;
        var expected = "sha256$" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jane@example.com" + salt))).ToLowerInvariant();
        Assert.Equal(expected, email.GetProperty("identityHash").GetString());
        Assert.Equal("Course Completion", vc.GetProperty("credentialSubject").GetProperty("achievement").GetProperty("name").GetString());
    }

    [Fact]
    public async Task OpenBadgeCredential_IsRefused_OnceSuspended()
    {
        var t = await SetUp();
        var approved = await IssueAndApprove(t);
        await StaffPost(t, $"/api/v1/credentials/{approved.Id}/suspend", new SuspendCredentialRequest("Under review", approved.VerificationCode));

        Assert.Equal(HttpStatusCode.Conflict, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{approved.Id}/credential")).StatusCode);
    }

    [Fact]
    public async Task VerificationHistory_RecordsEachChannel_WithoutVerifierDetails()
    {
        var t = await SetUp();
        var approved = await IssueAndApprove(t);

        await VerifyAsPage(t, approved.Id);
        await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{approved.Id}");
        await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup/{approved.VerificationCode}");
        await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{approved.Id}/credential");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/credentials/{approved.Id}/verifications");
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.TokenApprover);
        var response = await t.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = (await response.Content.ReadFromJsonAsync<CredentialVerificationHistoryResponse>())!;

        Assert.Equal(4, history.Total);
        Assert.Equal(1, history.VerifyPage);
        Assert.Equal(1, history.Api);
        Assert.Equal(1, history.CodeLookup);
        Assert.Equal(1, history.CredentialDownload);
        Assert.All(history.Recent, e => Assert.Equal(e.Channel == "CodeLookup" ? "Found" : "Active", e.Result));
    }

    [Fact]
    public async Task Jwks_PublishesInstitutionKey_AndName()
    {
        var t = await SetUp();
        await IssueAndApprove(t);

        var jwks = JsonDocument.Parse(await t.Client.GetStringAsync($"/api/v1/verify/{t.Slug}/jwks")).RootElement;
        Assert.Equal(t.Slug, jwks.GetProperty("institution").GetProperty("slug").GetString());
        var key = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        Assert.Equal("P-256", key.GetProperty("crv").GetString());
        Assert.False(key.TryGetProperty("d", out _)); // never the private key
    }

    [Fact]
    public async Task CodeLookup_IsRateLimited()
    {
        var t = await SetUp();
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 25 && last != HttpStatusCode.TooManyRequests; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/verify/{t.Slug}/lookup/AAAA-AAAA-AAAA");
            request.Headers.Add("CF-Connecting-IP", "203.0.113.7");
            last = (await t.Client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, string TokenRegistrar, string TokenApprover);

    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string slug;
        using (var scope = _factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            slug = (await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!")).Slug;
        }
        var client = _factory.CreateClient();
        var admin = await Login(client, slug, $"admin@{suffix}.test", "TenantAdmin123!");
        foreach (var (email, role) in new[] { ($"reg@{suffix}.test", "Registrar"), ($"app@{suffix}.test", "Approver") })
        {
            var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users") { Content = JsonContent.Create(new CreateUserRequest(email, role, "Password123!", role)) };
            create.Headers.Add("X-Tenant-Slug", slug);
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            await client.SendAsync(create);
        }
        return new Setup(client, slug,
            await Login(client, slug, $"reg@{suffix}.test", "Password123!"),
            await Login(client, slug, $"app@{suffix}.test", "Password123!"));
    }

    private static async Task<string> Login(HttpClient client, string slug, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", slug);
        return (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private static async Task<CredentialResponse> Draft(Setup t)
    {
        var achievement = await StaffPost(t, "/api/v1/achievements", new CreateAchievementRequest("Course Completion", null, null, null, null, null), t.TokenRegistrar);
        var achievementId = (await achievement.Content.ReadFromJsonAsync<AchievementResponse>())!.Id;
        var draft = await StaffPost(t, "/api/v1/credentials", new CreateCredentialRequest(achievementId, "Jane Doe", "jane@example.com", null), t.TokenRegistrar);
        return (await draft.Content.ReadFromJsonAsync<CredentialResponse>())!;
    }

    private static async Task<CredentialResponse> IssueAndApprove(Setup t)
    {
        var draft = await Draft(t);
        var approve = await StaffPost(t, $"/api/v1/credentials/{draft.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        return (await approve.Content.ReadFromJsonAsync<CredentialResponse>())!;
    }

    private static Task<HttpResponseMessage> StaffPost(Setup t, string url, object? body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? t.TokenApprover);
        return t.Client.SendAsync(request);
    }

    private static async Task<VerifyCredentialResponse> VerifyAsPage(Setup t, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/verify/{t.Slug}/{id}");
        request.Headers.Add("X-Certyfi-Client", "verify-page");
        var response = await t.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VerifyCredentialResponse>())!;
    }

    // Built from the published JWK only, as an outside verifier would.
    private static async Task<ECDsa> PublicKey(Setup t, string keyId)
    {
        var jwk = JsonDocument.Parse(await t.Client.GetStringAsync($"/api/v1/verify/{t.Slug}/jwks/{keyId}")).RootElement;
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64UrlDecode(jwk.GetProperty("x").GetString()!), Y = Base64UrlDecode(jwk.GetProperty("y").GetString()!) },
        });
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
