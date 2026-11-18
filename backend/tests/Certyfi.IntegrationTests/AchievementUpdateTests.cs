using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves AchievementsController.Update (amend) end to end: malformed
// schemas are rejected same as Create, only Registrar/InstitutionAdmin can
// amend, and — the actual reason this endpoint required a data-model change
// (Credential.FieldSchemaJson) before it could ship safely — amending an
// achievement's field schema must NEVER change how an already-issued
// credential renders. That's the single most important behavior this file
// exists to prove, not just a happy-path CRUD check.
public class AchievementUpdateTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementUpdateTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Update_RegistrarCanAmendTitleAndFieldSchema_AndListReflectsIt()
    {
        var (client, slug, tokenReg, _) = await SetUpTenant();
        var achievementId = await CreateAchievement(client, slug, tokenReg, "Original Title", "[]");

        var updateResponse = await PutAuthed(client, $"/api/v1/achievements/{achievementId}", slug, tokenReg,
            new UpdateAchievementRequest("Amended Title", "New description", null, null,
                """[{"key":"rollNumber","label":"Roll Number","type":"text"}]"""));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<AchievementResponse>();
        Assert.Equal("Amended Title", updated!.Title);
        Assert.Contains("rollNumber", updated.FieldSchemaJson);

        var listResponse = await GetAuthed(client, "/api/v1/achievements", slug, tokenReg);
        var achievements = await listResponse.Content.ReadFromJsonAsync<List<AchievementResponse>>();
        var found = achievements!.Single(a => a.Id == achievementId);
        Assert.Equal("Amended Title", found.Title);
        Assert.Equal("New description", found.Description);
    }

    [Fact]
    public async Task Update_RejectsMalformedFieldSchema()
    {
        var (client, slug, tokenReg, _) = await SetUpTenant();
        var achievementId = await CreateAchievement(client, slug, tokenReg, "T", "[]");

        var response = await PutAuthed(client, $"/api/v1/achievements/{achievementId}", slug, tokenReg,
            new UpdateAchievementRequest("T", null, null, null, """[{"key":"a","label":"A"}]"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_Learner_CannotAmend()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        const string learnerPassword = "Learner123!";
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
            new CreateUserRequest($"learner@{suffix}.test", "Test Learner", learnerPassword, "Learner"));
        var learnerLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"learner@{suffix}.test", learnerPassword));
        var tokenLearner = (await learnerLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var achievementId = await CreateAchievement(client, slug, tokenAdmin, "T", "[]");

        var response = await PutAuthed(client, $"/api/v1/achievements/{achievementId}", slug, tokenLearner,
            new UpdateAchievementRequest("Hacked Title", null, null, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_DoesNotChange_AlreadyIssuedCredentials_FieldSchemaSnapshot()
    {
        var (client, slug, tokenReg, tokenApp) = await SetUpTenant();
        const string schemaA = """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""";
        var achievementId = await CreateAchievement(client, slug, tokenReg, "Marks Card", schemaA);

        var createResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievementId, "Jane Doe", "jane@example.com",
                """{"rollNumber":"CS2024001"}"""));
        var credential = await createResponse.Content.ReadFromJsonAsync<CredentialResponse>();
        await PostAuthed(client, $"/api/v1/credentials/{credential!.Id}/approve", slug, tokenApp, null);

        // Amend the achievement to a completely different schema (B) —
        // drops rollNumber, adds an unrelated field.
        const string schemaB = """[{"key":"grade","label":"Grade","type":"text"}]""";
        var updateResponse = await PutAuthed(client, $"/api/v1/achievements/{achievementId}", slug, tokenReg,
            new UpdateAchievementRequest("Marks Card", null, null, null, schemaB));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        // The already-issued credential must still show schema A, not B.
        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credential.Id}");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Contains("rollNumber", verifyBody!.FieldSchemaJson);
        Assert.DoesNotContain("grade", verifyBody.FieldSchemaJson);

        // A NEW credential drafted after the amend must use the new schema.
        var newDraftResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievementId, "John Smith", "john@example.com", """{"grade":"A+"}"""));
        Assert.Equal(HttpStatusCode.Created, newDraftResponse.StatusCode);
        var newCredential = await newDraftResponse.Content.ReadFromJsonAsync<CredentialResponse>();
        Assert.Contains("grade", newCredential!.FieldSchemaJson);
        Assert.DoesNotContain("rollNumber", newCredential.FieldSchemaJson);
    }

    [Fact]
    public async Task Create_StoresFieldSchemaSnapshot_OnCredentialResponse()
    {
        var (client, slug, tokenReg, _) = await SetUpTenant();
        const string schema = """[{"key":"note","label":"Note","type":"text"}]""";
        var achievementId = await CreateAchievement(client, slug, tokenReg, "T", schema);

        var response = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievementId, "Jane Doe", "jane@example.com", """{"note":"hi"}"""));
        var credential = await response.Content.ReadFromJsonAsync<CredentialResponse>();
        Assert.Contains("note", credential!.FieldSchemaJson);
    }

    // Provisions a fresh tenant, then creates+logs-in a Registrar and an
    // Approver under it.
    private async Task<(HttpClient client, string slug, string tokenReg, string tokenApp)> SetUpTenant()
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

    private static async Task<Guid> CreateAchievement(HttpClient client, string slug, string tokenReg, string title, string fieldSchemaJson)
    {
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest(title, null, null, null, fieldSchemaJson, null));
        var body = await response.Content.ReadFromJsonAsync<AchievementResponse>();
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

    private static Task<HttpResponseMessage> PutAuthed(HttpClient client, string url, string tenantSlug, string bearerToken, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
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
