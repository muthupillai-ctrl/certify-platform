using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves the "real signature, not just another hash" claim end to end: an
// approved credential's Signature actually verifies cryptographically
// against its tenant's own public key using .NET's own ECDsa.VerifyData —
// not just "a string got set on the entity" — and that the verification
// path a real third party would use (fetch CanonicalHash + Signature +
// SignatureKeyId from the public verify response, fetch the public key
// from the public signing-keys endpoint, verify locally) works with zero
// authentication and zero trust in Certyfi's own say-so.
public class SigningTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SigningTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Approve_ProducesSignature_ThatVerifiesAgainstThePubliclyFetchedKey()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var credentialId = await DraftAndApproveCredential(client, slug, tokenReg, tokenAdmin);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credentialId}");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.NotNull(verifyBody!.CanonicalHash);
        Assert.NotNull(verifyBody.Signature);
        Assert.NotNull(verifyBody.SignatureKeyId);
        Assert.Equal("ES256", verifyBody.SignatureAlgorithm);

        // The public-key fetch itself needs NO auth and NO X-Tenant-Slug —
        // a plain client.GetAsync with no headers added, same as a real
        // third-party verifier (who has neither) would send.
        var keyResponse = await client.GetAsync($"/api/v1/verify/{slug}/signing-keys/{verifyBody.SignatureKeyId}");
        Assert.Equal(HttpStatusCode.OK, keyResponse.StatusCode);
        var keyBody = await keyResponse.Content.ReadFromJsonAsync<TenantSigningPublicKeyResponse>();

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(keyBody!.PublicKeyPem);
        // Rfc3279DerSequence explicitly, matching LocalTenantSigningKeyProvider's
        // own explicit signing format — NOT .NET's default (IeeeP1363).
        // Confirmed against Node.js's crypto module directly (not just
        // another .NET call) that DER is what's actually interoperable;
        // being explicit here on both ends means a format drift would fail
        // this test loudly instead of the two .NET calls quietly agreeing
        // with each other while every other library disagrees with both.
        var verified = ecdsa.VerifyData(
            Encoding.UTF8.GetBytes(verifyBody.CanonicalHash),
            Convert.FromBase64String(verifyBody.Signature),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        Assert.True(verified, "Signature must verify against the tenant's own publicly-fetched key.");

        // A signature that doesn't match the payload must NOT verify —
        // proves this is a real check, not a tautology that always passes.
        var tamperedVerified = ecdsa.VerifyData(
            Encoding.UTF8.GetBytes(verifyBody.CanonicalHash + "tampered"),
            Convert.FromBase64String(verifyBody.Signature),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(tamperedVerified);
    }

    [Fact]
    public async Task AfterAKeyIsRetired_NewCertificatesUseV2_AndOldOnesStillVerify()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var oldId = await DraftAndApproveCredential(client, slug, tokenReg, tokenAdmin);

        using (var scope = _factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<Certyfi.Infrastructure.Persistence.PlatformDbContext>();
            var tenantId = platform.Tenants.Single(t => t.Slug == slug).Id;
            foreach (var key in platform.TenantSigningKeys.Where(k => k.TenantId == tenantId)) key.IsActive = false;
            await platform.SaveChangesAsync();
        }

        var newId = await DraftAndApproveCredential(client, slug, tokenReg, tokenAdmin);
        var newer = (await (await client.GetAsync($"/api/v1/verify/{slug}/{newId}")).Content.ReadFromJsonAsync<VerifyCredentialResponse>())!;
        var older = (await (await client.GetAsync($"/api/v1/verify/{slug}/{oldId}")).Content.ReadFromJsonAsync<VerifyCredentialResponse>())!;
        Assert.Equal("v2", newer.SignatureKeyId);
        Assert.Equal("v1", older.SignatureKeyId);

        foreach (var c in new[] { older, newer })
        {
            var key = (await (await client.GetAsync($"/api/v1/verify/{slug}/signing-keys/{c.SignatureKeyId}")).Content.ReadFromJsonAsync<TenantSigningPublicKeyResponse>())!;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            Assert.True(ecdsa.VerifyData(Encoding.UTF8.GetBytes(c.CanonicalHash!), Convert.FromBase64String(c.Signature!),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), $"{c.SignatureKeyId} signature must verify");
        }
    }

    [Fact]
    public async Task DifferentTenants_GetDifferentSigningKeys()
    {
        var (clientA, slugA, tokenRegA, tokenAdminA) = await SetUpTenant();
        var (clientB, slugB, tokenRegB, tokenAdminB) = await SetUpTenant();

        var credentialIdA = await DraftAndApproveCredential(clientA, slugA, tokenRegA, tokenAdminA);
        var credentialIdB = await DraftAndApproveCredential(clientB, slugB, tokenRegB, tokenAdminB);

        var verifyA = await (await clientA.GetAsync($"/api/v1/verify/{slugA}/{credentialIdA}")).Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        var verifyB = await (await clientB.GetAsync($"/api/v1/verify/{slugB}/{credentialIdB}")).Content.ReadFromJsonAsync<VerifyCredentialResponse>();

        var keyA = await (await clientA.GetAsync($"/api/v1/verify/{slugA}/signing-keys/{verifyA!.SignatureKeyId}")).Content.ReadFromJsonAsync<TenantSigningPublicKeyResponse>();
        var keyB = await (await clientB.GetAsync($"/api/v1/verify/{slugB}/signing-keys/{verifyB!.SignatureKeyId}")).Content.ReadFromJsonAsync<TenantSigningPublicKeyResponse>();

        Assert.NotEqual(keyA!.PublicKeyPem, keyB!.PublicKeyPem);
    }

    [Fact]
    public async Task SigningKeysEndpoint_UnknownKeyId_Returns404()
    {
        var (client, slug, _, _) = await SetUpTenant();
        var response = await client.GetAsync($"/api/v1/verify/{slug}/signing-keys/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> DraftAndApproveCredential(HttpClient client, string slug, string tokenReg, string tokenAdmin)
    {
        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Signed Achievement", null, null, null, null, null));
        var achievement = await achievementResponse.Content.ReadFromJsonAsync<AchievementResponse>();

        var credentialResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievement!.Id, "Jane Doe", "jane@example.com", null));
        var credential = await credentialResponse.Content.ReadFromJsonAsync<CredentialResponse>();

        var approveResponse = await PostAuthed(client, $"/api/v1/credentials/{credential!.Id}/approve", slug, tokenAdmin, null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        return credential.Id;
    }

    private async Task<(HttpClient client, string slug, string tokenReg, string tokenAdmin)> SetUpTenant()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        const string regPassword = "Registrar123!";
        string slug;

        using (var setupScope = _factory.Services.CreateScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", adminPassword);
            slug = tenant.Slug;
        }

        var client = _factory.CreateClient();

        var adminLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest($"admin@{suffix}.test", adminPassword)) };
        adminLoginRequest.Headers.Add("X-Tenant-Slug", slug);
        var adminLogin = await client.SendAsync(adminLoginRequest);
        var tokenAdmin = (await adminLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var createUserRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users") { Content = JsonContent.Create(new CreateUserRequest($"registrar@{suffix}.test", "Test Registrar", regPassword, "Registrar")) };
        createUserRequest.Headers.Add("X-Tenant-Slug", slug);
        createUserRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        await client.SendAsync(createUserRequest);

        var regLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest($"registrar@{suffix}.test", regPassword)) };
        regLoginRequest.Headers.Add("X-Tenant-Slug", slug);
        var regLogin = await client.SendAsync(regLoginRequest);
        var tokenReg = (await regLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        return (client, slug, tokenReg, tokenAdmin);
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
