using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Mandatory certificate content, with the rule switched on as in
// production (Certificates:EnforceMandatoryContent): templates name one to
// three signatories and their design shows every core item; certificates
// keep their signatories as they were when made; fields marked required
// must be filled, wherever the certificate comes from.
public class SignatoryTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string FieldSchemaJson = """[{"key":"rollNumber","label":"Roll Number","type":"text","required":true},{"key":"grade","label":"Grade","type":"text"}]""";

    private readonly WebApplicationFactory<Program> _factory;

    public SignatoryTests(CustomWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Certificates:EnforceMandatoryContent"] = "true" })));
    }

    [Fact]
    public async Task Signatories_AreManagedByAdmins_AndCantBeDeletedWhileATemplateNamesThem()
    {
        var t = await SetUp();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, "/api/v1/signatories", t.Admin, new SaveSignatoryRequest("Dr. A. Kumar", " "))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, "/api/v1/signatories", t.Approver, new SaveSignatoryRequest("X", "Y"))).StatusCode);

        var list = await Json<List<SignatoryResponse>>(await Send(t, HttpMethod.Get, "/api/v1/signatories", t.Admin));
        var principal = list.Single(s => s.Id == t.PrincipalId);
        Assert.Equal(["Course Certificate"], principal.UsedByTemplates);

        var refused = await Send(t, HttpMethod.Delete, $"/api/v1/signatories/{t.PrincipalId}", t.Admin);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Course Certificate", await refused.Content.ReadAsStringAsync());

        var spare = await Json<SignatoryResponse>(await Send(t, HttpMethod.Post, "/api/v1/signatories", t.Admin, new SaveSignatoryRequest("Spare Person", "Registrar")));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(t, HttpMethod.Delete, $"/api/v1/signatories/{spare.Id}", t.Admin)).StatusCode);
    }

    [Fact]
    public async Task Templates_NeedASignatory_AtMostThree_AndEveryCoreItemInTheirDesign()
    {
        var t = await SetUp();
        var none = await Send(t, HttpMethod.Post, "/api/v1/certificate-templates", t.Admin,
            new CreateCertificateTemplateRequest("No Signatory", null, "Certificate", "[]"));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("at least one signatory", await none.Content.ReadAsStringAsync());

        var ids = new List<Guid> { t.PrincipalId };
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await Json<SignatoryResponse>(await Send(t, HttpMethod.Post, "/api/v1/signatories", t.Admin, new SaveSignatoryRequest($"Person {i}", "Dean")))).Id);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, "/api/v1/certificate-templates", t.Admin,
            new CreateCertificateTemplateRequest("Four", null, "Certificate", "[]", SignatoryIds: ids))).StatusCode);

        // A design without the recipient's name or the signatories.
        const string layout = """{"canvasWidth":700,"canvasHeight":500,"elements":[{"id":"i","type":"institutionName","x":0,"y":0,"width":300},{"id":"a","type":"achievementTitle","x":0,"y":40,"width":300},{"id":"d","type":"issuedDate","x":0,"y":80,"width":300},{"id":"q","type":"qrCode","x":0,"y":120,"width":140,"height":140}]}""";
        var incomplete = await Send(t, HttpMethod.Post, "/api/v1/certificate-templates", t.Admin,
            new CreateCertificateTemplateRequest("Incomplete", null, "Certificate", "[]", LayoutJson: layout, SignatoryIds: [t.PrincipalId]));
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        var message = await incomplete.Content.ReadAsStringAsync();
        Assert.Contains("Recipient name", message);
        Assert.Contains("Signatories", message);

        // The default design has everything, signatories included.
        var made = await Json<CertificateTemplateResponse>(await Send(t, HttpMethod.Post, "/api/v1/certificate-templates", t.Admin,
            new CreateCertificateTemplateRequest("Complete", null, "Certificate", "[]", SignatoryIds: [ids[1], t.PrincipalId])));
        Assert.Contains("\"signatories\"", made.LayoutJson);
        Assert.Equal(["Person 0", "Dr. A. Kumar"], made.Signatories.Select(s => s.Name));
    }

    [Fact]
    public async Task Certificates_KeepTheirSignatories_AsTheyWereWhenMade()
    {
        var t = await SetUp();
        var first = await Create(t, "Asha Rao", new { rollNumber = "R-1" });
        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Post, $"/api/v1/credentials/{first.Id}/approve", t.Approver)).StatusCode);
        var verified = await Json<VerifyCredentialResponse>(await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{first.Id}"));
        Assert.Equal([new SignatoryName("Dr. A. Kumar", "Principal")], verified.Signatories);

        // A new principal: later certificates carry the new name, issued ones don't change.
        await Json<SignatoryResponse>(await Send(t, HttpMethod.Put, $"/api/v1/signatories/{t.PrincipalId}", t.Admin, new SaveSignatoryRequest("Dr. B. Menon", "Principal")));
        var second = await Create(t, "Ben Lee", new { rollNumber = "R-2" });
        Assert.Equal("Dr. B. Menon", Assert.Single(second.Signatories!).Name);
        Assert.Equal("Dr. A. Kumar", Assert.Single((await Json<VerifyCredentialResponse>(await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{first.Id}"))).Signatories!).Name);
    }

    [Fact]
    public async Task ATemplateLeftWithoutSignatories_CantMakeCertificates_FromStaffOrPartner()
    {
        var t = await SetUp();
        await using (var db = _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema))
        {
            // As templates made before signatories existed are.
            await db.CertificateTemplates.Where(x => x.Id == t.TemplateId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SignatoryIds, new List<Guid>()));
        }

        var staff = await Send(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.Admin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Course Completion", "No One", "no.one@example.com", """{"rollNumber":"R-9"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, staff.StatusCode);
        Assert.Contains("no signatory", await staff.Content.ReadAsStringAsync());

        var key = (await Json<CreateApiClientResponse>(await Send(t, HttpMethod.Post, "/api/v1/api-clients", t.Admin,
            new CreateApiClientRequest("LMS", PartnerScope.All, false, false)))).ApiKey;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/credentials")
        {
            Content = JsonContent.Create(new PartnerIssueCredentialRequest(t.TemplateId, "Course Completion", "Pat Partner", "pat@example.com",
                JsonSerializer.SerializeToElement(new { rollNumber = "R-8" }), "ref-1", "S-1")),
        };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Add("X-Api-Key", key);
        var partner = await t.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, partner.StatusCode);
        Assert.Contains("no signatory", await partner.Content.ReadAsStringAsync());
        // Refused before the student was created.
        Assert.Equal(0, (await Json<StudentListResponse>(await Send(t, HttpMethod.Get, "/api/v1/students?q=pat%40example.com", t.Admin))).Total);
    }

    [Fact]
    public async Task RequiredFields_MustBeFilled_EvenOnAReusedAchievement()
    {
        var t = await SetUp();
        foreach (var values in new object[] { new { grade = "A" }, new { rollNumber = "   ", grade = "A" } })
        {
            var refused = await Send(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.Admin,
                new CreateCredentialFromTemplateRequest(t.TemplateId, "Course Completion", "Cara Diaz", "cara@example.com", JsonSerializer.Serialize(values)));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("'Roll Number' is required", await refused.Content.ReadAsStringAsync());
        }
        // Grade is optional.
        await Create(t, "Cara Diaz", new { rollNumber = "R-3" });

        // Made required on the template later: the existing achievement
        // ("Course Completion") still enforces it.
        var template = (await Json<List<CertificateTemplateResponse>>(await Send(t, HttpMethod.Get, "/api/v1/certificate-templates", t.Admin))).Single(x => x.Id == t.TemplateId);
        await Json<CertificateTemplateResponse>(await Send(t, HttpMethod.Put, $"/api/v1/certificate-templates/{t.TemplateId}", t.Admin,
            new UpdateCertificateTemplateRequest(template.Name, null, template.Kind, template.FieldSchemaJson.Replace("""{"key":"grade","label":"Grade","type":"text"}""", """{"key":"grade","label":"Grade","type":"text","required":true}"""),
                LayoutJson: template.LayoutJson, SignatoryIds: template.SignatoryIds)));
        var noGrade = await Send(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.Admin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Course Completion", "Dev Shah", "dev@example.com", """{"rollNumber":"R-4"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, noGrade.StatusCode);
        Assert.Contains("'Grade' is required", await noGrade.Content.ReadAsStringAsync());
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema, string Admin, string Approver, Guid PrincipalId, Guid TemplateId);

    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Certyfi.Domain.Entities.Tenant tenant;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<Certyfi.Infrastructure.Services.ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!");
        }
        var t = new Setup(_factory.CreateClient(), tenant.Slug, tenant.Id, tenant.SchemaName, "", "", Guid.Empty, Guid.Empty);
        var admin = await Login(t, $"admin@{suffix}.test", "TenantAdmin123!");
        t = t with { Admin = admin };
        await Send(t, HttpMethod.Post, "/api/v1/users", admin, new CreateUserRequest($"approver@{suffix}.test", "Approver", "Approver123!", SystemRole.Approver));
        t = t with { Approver = await Login(t, $"approver@{suffix}.test", "Approver123!") };

        var principal = await Json<SignatoryResponse>(await Send(t, HttpMethod.Post, "/api/v1/signatories", admin, new SaveSignatoryRequest(" Dr. A. Kumar ", "Principal")));
        Assert.Equal("Dr. A. Kumar", principal.Name);
        var template = await Json<CertificateTemplateResponse>(await Send(t, HttpMethod.Post, "/api/v1/certificate-templates", admin,
            new CreateCertificateTemplateRequest("Course Certificate", null, "Certificate", FieldSchemaJson, SignatoryIds: [principal.Id])));
        return t with { PrincipalId = principal.Id, TemplateId = template.Id };
    }

    private static async Task<CredentialResponse> Create(Setup t, string name, object values) =>
        await Json<CredentialResponse>(await Send(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.Admin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Course Completion", name, $"{name.Split(' ')[0].ToLowerInvariant()}@example.com", JsonSerializer.Serialize(values))));

    private static async Task<string> Login(Setup t, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        return (await Json<LoginResponse>(await t.Client.SendAsync(request))).AccessToken;
    }

    private static Task<HttpResponseMessage> Send(Setup t, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
