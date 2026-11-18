using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves CertificateTemplate end to end: malformed schemas/unknown kinds are
// rejected at creation, Registrar gets read-only access (needed to pick a
// template when creating an achievement) but only InstitutionAdmin manages
// the catalog, deactivating a template removes it from the list, and —
// the actual point of the feature — an achievement created FROM a template
// copies that template's Kind, which the public verify page then reflects
// (proving the "snapshot, not live reference" contract described on
// CertificateTemplate/Achievement.TemplateKind).
public class CertificateTemplatesTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CertificateTemplatesTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TemplateCreate_RejectsMalformedFieldSchema()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad", null, "Certificate", """[{"key":"a","label":"A"}]"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Designs_TextLinesImagesFramesAndFonts_AreAccepted_AndCheckedOnSave()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        const string elements = """
            {"id":"t","type":"text","text":"This is to certify that","x":60,"y":200,"width":580,"fontSize":14,"fontFamily":"cormorant","italic":true,"uppercase":false,"letterSpacing":2,"lineHeight":1.4},
            {"id":"n","type":"recipientName","x":60,"y":240,"width":580,"fontFamily":"great-vibes","bold":true},
            {"id":"l","type":"line","x":200,"y":290,"width":300,"height":2,"color":"#b8892c","lineStyle":"double"},
            {"id":"s","type":"image","src":"https://example.com/seal.png","x":560,"y":420,"width":100,"height":100,"opacity":0.8,"locked":true}
            """;
        string Layout(string extraElement = "", string frame = """{"style":"ornate","color":"#b8892c","width":4,"inset":24}""") =>
            $$"""{"canvasWidth":1100,"canvasHeight":850,"backgroundColor":"#fffdf7","frame":{{frame}},"elements":[{{elements}}{{extraElement}}]}""";

        var ok = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Designed", null, "Certificate", "[]", FontFamily: "playfair", LayoutJson: Layout()));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var saved = (await ok.Content.ReadFromJsonAsync<CertificateTemplateResponse>())!;
        Assert.Equal("playfair", saved.FontFamily);
        Assert.Contains("\"ornate\"", saved.LayoutJson);

        foreach (var (layout, expected) in new[]
        {
            (Layout(""",{"id":"x","type":"image","src":"javascript:alert(1)","x":0,"y":0,"width":10,"height":10}"""), "image address"),
            (Layout(""",{"id":"x","type":"text","text":" ","x":0,"y":0,"width":10}"""), "needs its text"),
            (Layout(""",{"id":"x","type":"text","text":"hi","fontFamily":"comic","x":0,"y":0,"width":10}"""), "fontFamily"),
            (Layout(""",{"id":"x","type":"line","lineStyle":"wavy","x":0,"y":0,"width":10,"height":2}"""), "lineStyle"),
            (Layout(""",{"id":"x","type":"image","src":"https://e.com/a.png","opacity":3,"x":0,"y":0,"width":10,"height":10}"""), "opacity"),
            (Layout(frame: """{"style":"sparkly"}"""), "frame.style"),
            (Layout(frame: """{"style":"line","color":"red"}"""), "frame.color"),
        })
        {
            var refused = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
                new CreateCertificateTemplateRequest("Bad", null, "Certificate", "[]", LayoutJson: layout));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(expected, await refused.Content.ReadAsStringAsync());
        }

        var badFont = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad font", null, "Certificate", "[]", FontFamily: "wingdings"));
        Assert.Equal(HttpStatusCode.BadRequest, badFont.StatusCode);
    }

    [Fact]
    public async Task TemplateCreate_RejectsBlankName()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("   ", null, "Certificate", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateUpdate_RejectsBlankName()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Original Name", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var response = await PutAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenAdmin,
            new UpdateCertificateTemplateRequest("", null, "Certificate", "[]"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateCreate_RejectsUnknownKind()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad", null, "Diploma", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateCreate_AcceptsValid_AndRoundTripsOnList()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        const string schema = """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""";

        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Diploma", "A diploma template", "MarksCard", schema));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await GetAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin);
        var templates = await listResponse.Content.ReadFromJsonAsync<List<CertificateTemplateResponse>>();
        var created = templates!.Single(t => t.Name == "Diploma");
        Assert.Equal("MarksCard", created.Kind);
        Assert.Contains("rollNumber", created.FieldSchemaJson);
    }

    [Fact]
    public async Task TemplateList_RegistrarCanView_ButCannotCreate()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Diploma", null, "Certificate", null));

        var listResponse = await GetAuthed(client, "/api/v1/certificate-templates", slug, tokenReg);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenReg,
            new CreateCertificateTemplateRequest("Another", null, "Certificate", null));
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
    }

    [Fact]
    public async Task TemplateDeactivate_RemovesFromList()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Temporary", null, "Certificate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var deactivateResponse = await PostAuthed(client, $"/api/v1/certificate-templates/{created!.Id}/deactivate", slug, tokenAdmin, null);
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await GetAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin);
        var templates = await listResponse.Content.ReadFromJsonAsync<List<CertificateTemplateResponse>>();
        Assert.DoesNotContain(templates!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task TemplateUpdate_InstitutionAdminCanAmendNameDescriptionKindAndFieldSchema_AndListReflectsIt()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Original", "Original description", "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var updateResponse = await PutAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenAdmin,
            new UpdateCertificateTemplateRequest("Amended", "Amended description", "MarksCard",
                """[{"key":"rollNumber","label":"Roll Number","type":"text"}]"""));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();
        Assert.Equal("Amended", updated!.Name);
        Assert.Equal("MarksCard", updated.Kind);
        Assert.Contains("rollNumber", updated.FieldSchemaJson);

        var listResponse = await GetAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin);
        var templates = await listResponse.Content.ReadFromJsonAsync<List<CertificateTemplateResponse>>();
        var found = templates!.Single(t => t.Id == created.Id);
        Assert.Equal("Amended", found.Name);
        Assert.Equal("Amended description", found.Description);
    }

    [Fact]
    public async Task TemplateUpdate_RejectsMalformedFieldSchema()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("T", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var response = await PutAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenAdmin,
            new UpdateCertificateTemplateRequest("T", null, "Certificate", """[{"key":"a","label":"A"}]"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateUpdate_RejectsUnknownKind()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("T", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var response = await PutAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenAdmin,
            new UpdateCertificateTemplateRequest("T", null, "Diploma", null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateUpdate_Registrar_CannotAmend()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("T", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var response = await PutAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenReg,
            new UpdateCertificateTemplateRequest("Hacked", null, "Certificate", null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TemplateDelete_UnusedTemplate_RemovesItPermanently()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Unused", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var deleteResponse = await DeleteAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenAdmin);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Proves REAL removal, not just deactivation: AchievementsController.Create's
        // template lookup has no IsActive filter, so a merely-deactivated
        // template would still succeed here — only a truly-gone row 400s.
        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenAdmin,
            new CreateAchievementRequest("Should Fail", null, null, null, null, created.Id));
        Assert.Equal(HttpStatusCode.BadRequest, achievementResponse.StatusCode);
    }

    [Fact]
    public async Task TemplateDelete_TemplateWithAchievements_IsBlocked()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Used", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Depends On Template", null, null, null, null, created!.Id));

        var deleteResponse = await DeleteAuthed(client, $"/api/v1/certificate-templates/{created.Id}", slug, tokenAdmin);
        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);

        var listResponse = await GetAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin);
        var templates = await listResponse.Content.ReadFromJsonAsync<List<CertificateTemplateResponse>>();
        Assert.Contains(templates!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task TemplateDelete_Registrar_CannotDelete()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("T", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var response = await DeleteAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenReg);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AchievementCreate_WithTemplateId_CopiesTemplateKind_AndVerifyReflectsIt()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        const string schema = """
            [
              {"key":"rollNumber","label":"Roll Number","type":"text"},
              {"key":"subjects","label":"Subjects","type":"table","columns":[
                {"key":"subject","label":"Subject","type":"text"}
              ]}
            ]
            """;
        var templateResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Marks Card", null, "MarksCard", schema));
        var template = await templateResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Semester 1 Marks Card", null, null, null, schema, template!.Id));
        Assert.Equal(HttpStatusCode.Created, achievementResponse.StatusCode);
        var achievement = await achievementResponse.Content.ReadFromJsonAsync<AchievementResponse>();
        Assert.Equal("MarksCard", achievement!.TemplateKind);

        var credentialResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievement.Id, "Jane Doe", "jane@example.com", null));
        var credential = await credentialResponse.Content.ReadFromJsonAsync<CredentialResponse>();

        // A different actor must approve — self-approval is blocked (see
        // MakerCheckerTests) — reuse the InstitutionAdmin token, distinct
        // from the Registrar who drafted it.
        await PostAuthed(client, $"/api/v1/credentials/{credential!.Id}/approve", slug, tokenAdmin, null);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credential.Id}");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("MarksCard", verifyBody!.TemplateKind);
    }

    [Fact]
    public async Task TemplateCreate_WithoutVisualDesign_DefaultsTitleTextToName_AndGeneratesLayout()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Diploma Of Honor", null, "Certificate", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CertificateTemplateResponse>();
        Assert.Equal("Diploma Of Honor", created!.TitleText);
        Assert.Null(created.SubtitleText);
        Assert.Contains("\"canvasWidth\"", created.LayoutJson);
        Assert.Contains("\"type\":\"title\"", created.LayoutJson);
    }

    [Fact]
    public async Task TemplateCreate_WithExplicitVisualDesign_RoundTrips()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        const string schema = """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""";
        const string layout = """
            {"canvasWidth":700,"canvasHeight":500,"elements":[
              {"id":"title","type":"title","x":10,"y":10,"width":300,"fontSize":30},
              {"id":"field_rollNumber","type":"field","fieldKey":"rollNumber","x":10,"y":60,"width":200}
            ]}
            """;

        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Custom", null, "Certificate", schema,
                TitleText: "My Custom Title", SubtitleText: "A subtitle", AccentColor: "#ff00aa", FontFamily: "sans", LayoutJson: layout));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CertificateTemplateResponse>();
        Assert.Equal("My Custom Title", created!.TitleText);
        Assert.Equal("A subtitle", created.SubtitleText);
        Assert.Equal("#ff00aa", created.AccentColor);
        Assert.Equal("sans", created.FontFamily);
        Assert.Contains("field_rollNumber", created.LayoutJson);
    }

    [Fact]
    public async Task TemplateCreate_RejectsLayoutReferencingUnknownFieldKey()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        const string layout = """{"canvasWidth":700,"canvasHeight":500,"elements":[{"id":"a","type":"field","fieldKey":"doesNotExist","x":0,"y":0,"width":100}]}""";

        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad Layout", null, "Certificate", "[]", LayoutJson: layout));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateCreate_RejectsMalformedLayoutJson()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad Layout", null, "Certificate", "[]", LayoutJson: "not json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateCreate_RejectsInvalidAccentColor()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad Color", null, "Certificate", "[]", AccentColor: "not-a-color"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemplateCreate_AcceptsHttpsBackgroundImageUrl_AndRoundTrips()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Imported Design", null, "Certificate", "[]", BackgroundImageUrl: "https://example.com/certificate-bg.png"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CertificateTemplateResponse>();
        Assert.Equal("https://example.com/certificate-bg.png", created!.BackgroundImageUrl);
    }

    [Fact]
    public async Task TemplateCreate_RejectsNonHttpBackgroundImageUrl()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Bad Background", null, "Certificate", "[]", BackgroundImageUrl: "javascript:alert(1)"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AchievementAndCredential_SnapshotTemplateBackgroundImageUrl_AndVerifyReflectsIt()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Imported", null, "Certificate", "[]", BackgroundImageUrl: "https://example.com/bg.jpg"));
        var template = await templateResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Imported Award", null, null, null, null, template!.Id));
        var achievement = await achievementResponse.Content.ReadFromJsonAsync<AchievementResponse>();

        var credentialResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievement!.Id, "Jane Doe", "jane@example.com", null));
        var credential = await credentialResponse.Content.ReadFromJsonAsync<CredentialResponse>();
        Assert.Equal("https://example.com/bg.jpg", credential!.BackgroundImageUrl);

        await PostAuthed(client, $"/api/v1/credentials/{credential.Id}/approve", slug, tokenAdmin, null);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credential.Id}");
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("https://example.com/bg.jpg", verifyBody!.BackgroundImageUrl);
    }

    [Fact]
    public async Task TemplateUpdate_CanAmendVisualDesign()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var createResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("T", null, "Certificate", "[]"));
        var created = await createResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var response = await PutAuthed(client, $"/api/v1/certificate-templates/{created!.Id}", slug, tokenAdmin,
            new UpdateCertificateTemplateRequest("T", null, "Certificate", "[]", TitleText: "Amended Title", AccentColor: "#123456"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<CertificateTemplateResponse>();
        Assert.Equal("Amended Title", updated!.TitleText);
        Assert.Equal("#123456", updated.AccentColor);
    }

    [Fact]
    public async Task AchievementAndCredential_SnapshotTemplateVisualDesign_AndVerifyReflectsIt()
    {
        var (client, slug, tokenReg, tokenAdmin) = await SetUpTenant();
        var templateResponse = await PostAuthed(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Honors", null, "Certificate", "[]",
                TitleText: "Certificate of Honors", SubtitleText: "Awarded for excellence", AccentColor: "#7a2331"));
        var template = await templateResponse.Content.ReadFromJsonAsync<CertificateTemplateResponse>();

        var achievementResponse = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Dean's List", null, null, null, null, template!.Id));
        var achievement = await achievementResponse.Content.ReadFromJsonAsync<AchievementResponse>();

        var credentialResponse = await PostAuthed(client, "/api/v1/credentials", slug, tokenReg,
            new CreateCredentialRequest(achievement!.Id, "Jane Doe", "jane@example.com", null));
        var credential = await credentialResponse.Content.ReadFromJsonAsync<CredentialResponse>();
        Assert.Equal("Certificate of Honors", credential!.TitleText);
        Assert.Equal("Awarded for excellence", credential.SubtitleText);
        Assert.Equal("#7a2331", credential.AccentColor);

        await PostAuthed(client, $"/api/v1/credentials/{credential.Id}/approve", slug, tokenAdmin, null);

        var verifyResponse = await client.GetAsync($"/api/v1/verify/{slug}/{credential.Id}");
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Certificate of Honors", verifyBody!.TitleText);
        Assert.Equal("Awarded for excellence", verifyBody.SubtitleText);
        Assert.Equal("#7a2331", verifyBody.AccentColor);

        // Amending the template afterward must NOT change the already-issued
        // credential's rendering — same snapshot contract as TemplateKind.
        await PutAuthed(client, $"/api/v1/certificate-templates/{template.Id}", slug, tokenAdmin,
            new UpdateCertificateTemplateRequest("Honors", null, "Certificate", "[]", TitleText: "Renamed Later"));

        var verifyAfterAmend = await client.GetAsync($"/api/v1/verify/{slug}/{credential.Id}");
        var verifyAfterAmendBody = await verifyAfterAmend.Content.ReadFromJsonAsync<VerifyCredentialResponse>();
        Assert.Equal("Certificate of Honors", verifyAfterAmendBody!.TitleText);
    }

    [Fact]
    public async Task AchievementCreate_WithoutTemplateId_DefaultsToCertificateKind()
    {
        var (client, slug, tokenReg, _) = await SetUpTenant();
        var response = await PostAuthed(client, "/api/v1/achievements", slug, tokenReg,
            new CreateAchievementRequest("Freeform Achievement", null, null, null, null, null));
        var achievement = await response.Content.ReadFromJsonAsync<AchievementResponse>();
        Assert.Equal("Certificate", achievement!.TemplateKind);
    }

    // Provisions a fresh tenant, then creates+logs-in a Registrar under it,
    // alongside the InstitutionAdmin's own token (needed here for
    // InstitutionAdmin-only template management and as a second, distinct
    // approver actor).
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

    private static Task<HttpResponseMessage> PutAuthed(HttpClient client, string url, string tenantSlug, string bearerToken, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteAuthed(HttpClient client, string url, string tenantSlug, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("X-Tenant-Slug", tenantSlug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client.SendAsync(request);
    }
}
