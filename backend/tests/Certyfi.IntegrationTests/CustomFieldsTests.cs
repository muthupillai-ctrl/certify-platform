using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves the custom-field-schema mechanism (Achievement.FieldSchemaJson /
// Credential.FieldValuesJson) end to end: malformed schemas are rejected at
// creation (not left to crash the public verify page later), well-formed
// flat and table field values round-trip through the public verify
// endpoint VERBATIM including an XSS payload — an explicit behavioral
// contract that sanitization is the frontend's job (Angular's {{ }}
// interpolation), not this API's — and institution branding updates are
// InstitutionAdmin-only.
public class CustomFieldsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CustomFieldsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AchievementCreate_RejectsSchema_MissingType()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("T", null, null, null, """[{"key":"a","label":"A"}]""", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AchievementCreate_RejectsSchema_UnknownType()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("T", null, null, null, """[{"key":"a","label":"A","type":"currency"}]""", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AchievementCreate_RejectsSchema_NestedTableInColumn()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("T", null, null, null,
                """[{"key":"subjects","label":"Subjects","type":"table","columns":[{"key":"inner","label":"Inner","type":"table","columns":[{"key":"x","label":"X","type":"text"}]}]}]""", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AchievementCreate_RejectsSchema_DuplicateKey()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("T", null, null, null,
                """[{"key":"a","label":"A","type":"text"},{"key":"a","label":"A again","type":"text"}]""", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AchievementCreate_RejectsSchema_ReservedKey()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("T", null, null, null,
                """[{"key":"__proto__","label":"Bad","type":"text"}]""", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AchievementCreate_AcceptsValidFlatAndTableSchema_AndRoundTripsOnList()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        const string schema = """
            [
              {"key":"rollNumber","label":"Roll Number","type":"text"},
              {"key":"subjects","label":"Subjects","type":"table","columns":[
                {"key":"subject","label":"Subject","type":"text"},
                {"key":"marks","label":"Marks","type":"number"}
              ]}
            ]
            """;

        var createResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Marks Card", null, null, null, schema, null));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await GetAuthed(client, "/api/v1/achievements", slug, tokenReg);
        var achievements = await listResponse.Content.ReadFromJsonAsync<List<AchievementResponse>>();
        var created = achievements!.Single(a => a.Title == "Marks Card");
        Assert.Contains("rollNumber", created.FieldSchemaJson);
        Assert.Contains("subjects", created.FieldSchemaJson);
    }

    [Fact]
    public async Task CredentialCreate_RejectsFieldValues_TableFieldGivenNonArray()
    {
        var (client, slug, tokenReg, _, _) = await SetUpTenant();
        var achievementId = await CreateAchievementWithSchema(client, slug, tokenReg,
            """[{"key":"subjects","label":"Subjects","type":"table","columns":[{"key":"subject","label":"Subject","type":"text"}]}]""");

        var response = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievementId, "Jane Doe", "jane@example.com", """{"subjects":"not an array"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CredentialCreate_AcceptsFieldValues_AndVerifyReturnsThemVerbatim_IncludingXssPayload()
    {
        var (client, slug, tokenReg, tokenApp, _) = await SetUpTenant();
        var achievementId = await CreateAchievementWithSchema(client, slug, tokenReg,
            """
            [
              {"key":"note","label":"Note","type":"text"},
              {"key":"subjects","label":"Subjects","type":"table","columns":[
                {"key":"subject","label":"Subject","type":"text"}
              ]}
            ]
            """);

        const string xssFlat = "<script>alert(document.domain)</script>";
        const string xssCell = "<img src=x onerror=alert(1)>";
        var fieldValues = $$"""
            {"note":"{{xssFlat}}","subjects":[{"subject":"{{xssCell}}"}]}
            """;

        var createResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievementId, "Jane Doe", "jane@example.com", fieldValues));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CredentialResponse>();

        await PostAuthed(client, $"/api/v1/credentials/{created!.Id}/approve", slug, tokenApp, null);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();

        // The API returns the payload verbatim, unescaped — sanitization is
        // correctly a render-time (frontend) responsibility, not a
        // storage-time one. This is an explicit behavioral contract.
        Assert.Contains(xssFlat, verifyBody!.FieldValuesJson);
        Assert.Contains(xssCell, verifyBody.FieldValuesJson);
    }

    [Fact]
    public async Task TenantSettings_InstitutionAdminCanUpdateBranding_AndGetReflectsIt()
    {
        var (client, slug, _, _, tokenAdmin) = await SetUpTenant();

        var updateResponse = await PutAuthed(client, "/api/v1/tenant-settings/branding", slug, tokenAdmin,
            new UpdateTenantBrandingRequest("https://example.com/logo.png", "#112233"));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var getResponse = await GetAuthed(client, "/api/v1/tenant-settings/branding", slug, tokenAdmin);
        var branding = await getResponse.Content.ReadFromJsonAsync<TenantBrandingResponse>();
        Assert.Equal("https://example.com/logo.png", branding!.LogoUrl);
        Assert.Equal("#112233", branding.PrimaryColor);
    }

    [Fact]
    public async Task TenantSettings_RejectsInvalidLogoUrl()
    {
        var (client, slug, _, _, tokenAdmin) = await SetUpTenant();
        var response = await PutAuthed(client, "/api/v1/tenant-settings/branding", slug, tokenAdmin,
            new UpdateTenantBrandingRequest("javascript:alert(1)", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TenantSettings_NonInstitutionAdmin_CannotUpdateBranding()
    {
        var (client, slug, tokenReg, tokenApp, _) = await SetUpTenant();
        var request = new UpdateTenantBrandingRequest("https://example.com/logo.png", "#112233");

        var regResponse = await PutAuthed(client, "/api/v1/tenant-settings/branding", slug, tokenReg, request);
        Assert.Equal(HttpStatusCode.Forbidden, regResponse.StatusCode);

        var appResponse = await PutAuthed(client, "/api/v1/tenant-settings/branding", slug, tokenApp, request);
        Assert.Equal(HttpStatusCode.Forbidden, appResponse.StatusCode);
    }

    // Provisions a fresh tenant, then creates+logs-in a Registrar and an
    // Approver under it, alongside the InstitutionAdmin's own token.
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

    private static async Task<Guid> CreateAchievementWithSchema(HttpClient client, string slug, string tokenReg, string fieldSchemaJson)
    {
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Test Achievement", null, null, null, fieldSchemaJson, null));
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

    private static Task<HttpResponseMessage> PutAuthed(HttpClient client, string url, string tenantSlug, string bearerToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
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
