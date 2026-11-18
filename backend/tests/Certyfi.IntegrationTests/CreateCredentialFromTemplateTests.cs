using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves POST /api/v1/credentials/from-template end to end: the admin UI's
// New Certificate form now picks a CertificateTemplate + types a Title
// instead of picking an Achievement directly, and this endpoint resolves
// (or auto-creates) the Achievement that Credential still actually
// references underneath. The core behavior this file exists to prove is
// reuse: two calls with the identical (TemplateId, Title) must share the
// same Achievement — including one deliberately pre-built the fuller way
// via POST /api/v1/achievements, not just ones this endpoint created itself.
public class CreateCredentialFromTemplateTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CreateCredentialFromTemplateTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FirstCall_CreatesNewAchievement_AndCredential()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateId = await CreateTemplate(client, slug, tokenAdmin, "MarksCardTest1", """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""");

        var response = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 1 Marks Card", "Jane Doe", "jane@example.com", """{"rollNumber":"CS001"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var credential = await response.Content.ReadFromJsonAsync<CredentialResponse>();
        Assert.Equal("Semester 1 Marks Card", credential!.AchievementTitle);
        Assert.Contains("rollNumber", credential.FieldSchemaJson);

        var achievements = await GetAuthed(client, "/api/v1/achievements", slug, tokenReg);
        var list = await achievements.Content.ReadFromJsonAsync<List<AchievementResponse>>();
        Assert.Contains(list!, a => a.Id == credential.AchievementId && a.Title == "Semester 1 Marks Card");
    }

    [Fact]
    public async Task SecondCallSameTemplateAndTitle_ReusesSameAchievement()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateId = await CreateTemplate(client, slug, tokenAdmin, "MarksCardTest2", "[]");

        var first = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 1", "Jane Doe", "jane@example.com", null));
        var firstCredential = await first.Content.ReadFromJsonAsync<CredentialResponse>();

        var second = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 1", "John Smith", "john@example.com", null));
        var secondCredential = await second.Content.ReadFromJsonAsync<CredentialResponse>();

        Assert.Equal(firstCredential!.AchievementId, secondCredential!.AchievementId);

        var achievements = await GetAuthed(client, "/api/v1/achievements", slug, tokenReg);
        var list = await achievements.Content.ReadFromJsonAsync<List<AchievementResponse>>();
        Assert.Single(list!, a => a.Title == "Semester 1");
    }

    [Fact]
    public async Task DifferentTitle_CreatesDifferentAchievement()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateId = await CreateTemplate(client, slug, tokenAdmin, "MarksCardTest3", "[]");

        var first = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 1", "Jane Doe", "jane@example.com", null));
        var firstCredential = await first.Content.ReadFromJsonAsync<CredentialResponse>();

        var second = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 2", "Jane Doe", "jane@example.com", null));
        var secondCredential = await second.Content.ReadFromJsonAsync<CredentialResponse>();

        Assert.NotEqual(firstCredential!.AchievementId, secondCredential!.AchievementId);
    }

    [Fact]
    public async Task RejectsUnknownOrInactiveTemplate()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(Guid.NewGuid(), "Whatever", "Jane Doe", "jane@example.com", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectsEmptyTitle()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateId = await CreateTemplate(client, slug, tokenAdmin, "MarksCardTest4", "[]");
        var response = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "  ", "Jane Doe", "jane@example.com", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectsFieldValuesNotMatchingTemplateSchema()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateId = await CreateTemplate(client, slug, tokenAdmin, "MarksCardTest5",
            """[{"key":"subjects","label":"Subjects","type":"table","columns":[{"key":"subject","label":"Subject","type":"text"}]}]""");

        var response = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 1", "Jane Doe", "jane@example.com", """{"subjects":"not an array"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReusesAchievementDeliberatelyCreatedViaAchievementsEndpoint()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateId = await CreateTemplate(client, slug, tokenAdmin, "MarksCardTest6", "[]");

        // Built the fuller way — a real description and validity period,
        // which the from-template endpoint never sets itself.
        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Semester 1", "A rich, hand-built achievement", "Complete all coursework", 12, "[]", templateId));
        var prebuilt = await achievementResponse.Content.ReadFromJsonAsync<AchievementResponse>();

        var response = await PostAuthed(client, "/api/v1/credentials/from-template", slug, tokenReg,
            new CreateCredentialFromTemplateRequest(templateId, "Semester 1", "Jane Doe", "jane@example.com", null));
        var credential = await response.Content.ReadFromJsonAsync<CredentialResponse>();

        Assert.Equal(prebuilt!.Id, credential!.AchievementId);
        Assert.Equal("A rich, hand-built achievement", credential.AchievementDescription);
    }

    // Provisions a fresh tenant, then creates+logs-in a Registrar under it,
    // alongside the InstitutionAdmin's own token (needed for template
    // creation, which is InstitutionAdmin-only).
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

        var adminLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"admin@{suffix}.test", adminPassword));
        var tokenAdmin = (await adminLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        await PostAuthed(client, "/api/v1/users", slug, tokenAdmin,
            new CreateUserRequest($"registrar@{suffix}.test", "Test Registrar", regPassword, "Registrar"));

        var regLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"registrar@{suffix}.test", regPassword));
        var tokenReg = (await regLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        return (client, slug, tokenReg, tokenAdmin);
    }

    // Templates are InstitutionAdmin-only to create — callers pass the
    // admin token SetUpTenant already returned them.
    private static async Task<Guid> CreateTemplate(HttpClient client, string slug, string tokenAdmin, string name, string fieldSchemaJson)
    {
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest(name, null, "MarksCard", fieldSchemaJson));
        var body = await response.Content.ReadFromJsonAsync<CertificateTemplateResponse>();
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
