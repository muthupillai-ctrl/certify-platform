using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Certyfi.IntegrationTests;

// The three-person rule as production runs it: institutions have two
// admins (added only by Ahamsys) who run the institution and give final
// approval; a Registrar requests and an Approver gives the first approval.
// Safeguards are switched off only with a second admin's confirmation, and
// every change is in the append-only institution audit log.
public class FinalApprovalTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Password = "Strong-Pass-2026!";
    private readonly WebApplicationFactory<Program> _factory;

    public FinalApprovalTests(CustomWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:AutoIssueNeedsSecondAdmin"] = "true" })));
    }

    [Fact]
    public async Task ThreeDifferentPeople_RequestApproveAndFinallyApprove_AllInTheLedger()
    {
        var t = await SetUp();
        var id = await Request(t, "Ama Mensah");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", t.AdminA)).StatusCode); // admins don't first-approve

        var first = await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", t.Approver));
        Assert.Equal("PendingFinalApproval", first.Status);
        // The approver can't also give the final approval.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", t.Approver)).StatusCode);
        // It's in admin A's queue.
        var queue = await Json<CredentialPageResponse>(await Send(t, HttpMethod.Get, "/api/v1/credentials?awaitingMe=true", t.AdminA));
        Assert.Contains(queue.Items, i => i.Id == id);

        var issued = await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", t.AdminA));
        Assert.Equal("Active", issued.Status);
        var ledger = await Json<List<LedgerEntryResponse>>(await Send(t, HttpMethod.Get, $"/api/v1/credentials/{id}/ledger", t.AdminA));
        Assert.Equal(["Requested", "FirstApproved", "Approved"], ledger.Select(e => e.EventType));
        Assert.Contains("\"stage\":\"Final\"", ledger[2].EventDataJson);

        // Rejected at the final step.
        var second = await Request(t, "Kofi Boateng");
        await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{second}/approve", t.Approver));
        var rejected = await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{second}/reject", t.AdminB, new RejectCredentialRequest("Wrong course")));
        Assert.Equal("Rejected", rejected.Status);

        // A template without the rule: one approval issues it.
        await SetFinalApproval(t, t.AdminA, false);
        await Json<ControlRequestResponse>(await Send(t, HttpMethod.Post, $"/api/v1/control-requests/{(await PendingRequests(t, t.AdminB)).Single().Id}/confirm", t.AdminB));
        var third = await Request(t, "Esi Owusu");
        Assert.Equal("Active", (await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{third}/approve", t.Approver))).Status);
    }

    [Fact]
    public async Task AnAdminWhoAlsoRequests_CantFinallyApproveTheirOwn()
    {
        var t = await SetUp();
        // Only Ahamsys changes an admin's roles.
        var platform = PlatformToken();
        var adminB = (await Json<List<TenantUserDetail>>(await Send(t, HttpMethod.Get, $"/api/v1/platform/tenants/{t.TenantId}/users", platform, slug: false))).Single(u => u.Email == t.AdminBEmail);
        await Json<TenantUserDetail>(await Send(t, HttpMethod.Put, $"/api/v1/platform/tenants/{t.TenantId}/users/{adminB.Id}", platform,
            new UpdateUserRequest(adminB.FullName, adminB.Email, [SystemRole.InstitutionAdmin, SystemRole.Registrar]), slug: false));
        var tokenB = await Login(t, t.AdminBEmail);

        var id = await Request(t, "Yaw Asante", tokenB);
        await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", t.Approver));
        var own = await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", tokenB);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Contains("You requested it", await own.Content.ReadAsStringAsync());
        Assert.Equal("Active", (await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{id}/approve", t.AdminA))).Status);
    }

    [Fact]
    public async Task SwitchingFinalApprovalOff_NeedsTheOtherAdmin_AndEveryStepIsAudited()
    {
        var t = await SetUp();
        var asked = await SetFinalApproval(t, t.AdminA, false);
        Assert.True(asked.RequiresFinalApproval);
        Assert.True(asked.FinalApprovalOffPending);

        var request = (await PendingRequests(t, t.AdminA)).Single();
        Assert.False(request.CanDecide);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/control-requests/{request.Id}/confirm", t.AdminA)).StatusCode);

        // Refused: stays on. Asked again, withdrawn. Asked again, confirmed.
        await Json<ControlRequestResponse>(await Send(t, HttpMethod.Post, $"/api/v1/control-requests/{request.Id}/refuse", t.AdminB, new RefuseControlRequest("Degrees need it")));
        Assert.True((await Template(t)).RequiresFinalApproval);
        await SetFinalApproval(t, t.AdminA, false);
        var again = (await PendingRequests(t, t.AdminA)).Single();
        await Json<ControlRequestResponse>(await Send(t, HttpMethod.Post, $"/api/v1/control-requests/{again.Id}/withdraw", t.AdminA));
        await SetFinalApproval(t, t.AdminA, false);
        var third = (await PendingRequests(t, t.AdminB)).Single();
        Assert.True(third.CanDecide);
        await Json<ControlRequestResponse>(await Send(t, HttpMethod.Post, $"/api/v1/control-requests/{third.Id}/confirm", t.AdminB));
        Assert.False((await Template(t)).RequiresFinalApproval);
        // Back on: immediate.
        Assert.True((await SetFinalApproval(t, t.AdminB, true)).RequiresFinalApproval);

        var log = await Json<AuditLogPageResponse>(await Send(t, HttpMethod.Get, "/api/v1/audit-log?pageSize=100", t.AdminA));
        var actions = log.Items.Select(i => i.Action).Reverse().ToList();
        Assert.Equal(
            ["FinalApprovalTurnedOn", "DisableFinalApprovalRequested", "DisableFinalApprovalRefused", "DisableFinalApprovalRequested",
             "DisableFinalApprovalWithdrawn", "DisableFinalApprovalRequested", "DisableFinalApprovalConfirmed", "FinalApprovalTurnedOn"],
            actions.Where(a => a.Contains("FinalApproval")).ToList());
        Assert.Contains(log.Items, i => i.Action == "DisableFinalApprovalRefused" && i.Details!.Contains("Degrees need it"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Get, "/api/v1/audit-log", t.Approver)).StatusCode);

        // The log can't be changed, even directly in the database.
        await using var db = Db(t);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.InstitutionAuditLog.ExecuteUpdateAsync(u => u.SetProperty(a => a.ActorName, "someone else")));
        Assert.Contains("append-only", (ex.InnerException ?? ex).Message);
    }

    [Fact]
    public async Task Admins_DontIssue_AndOnlyAhamsysManagesAdmins_KeepingTwo()
    {
        var t = await SetUp();
        var template = await Template(t);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.AdminA,
            new CreateCredentialFromTemplateRequest(template.Id, "Course", "No One", "no.one@example.com", "{}"))).StatusCode);

        // Institution admins can't add, promote, reset or deactivate admins.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, "/api/v1/users", t.AdminA,
            new CreateUserRequest("third@example.com", "Third", Password, SystemRole.InstitutionAdmin))).StatusCode);
        var users = await Json<List<TenantUserDetail>>(await Send(t, HttpMethod.Get, "/api/v1/users", t.AdminA));
        var adminB = users.Single(u => u.Email == t.AdminBEmail);
        var registrar = users.Single(u => u.Roles.Contains(SystemRole.Registrar));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/users/{adminB.Id}/reset-password", t.AdminA)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/users/{adminB.Id}/deactivate", t.AdminA)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Put, $"/api/v1/users/{registrar.Id}", t.AdminA,
            new UpdateUserRequest(registrar.FullName, registrar.Email, [SystemRole.Registrar, SystemRole.InstitutionAdmin]))).StatusCode);

        // Ahamsys: an institution needs two admins from the start; removing
        // one needs a third first.
        var platform = PlatformToken();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var oneAdmin = await Send(t, HttpMethod.Post, "/api/v1/platform/tenants", platform,
            new CreateTenantRequest($"Solo {suffix}", $"solo-{suffix}", $"a@{suffix}.test", Password, "Ann A"), slug: false);
        Assert.Equal(HttpStatusCode.BadRequest, oneAdmin.StatusCode);
        Assert.Contains("two admins", await oneAdmin.Content.ReadAsStringAsync());

        var keepTwo = await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/users/{adminB.Id}/deactivate", platform, slug: false);
        Assert.Equal(HttpStatusCode.Conflict, keepTwo.StatusCode);
        await Json<TenantUserDetail>(await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/admins", platform,
            new AddInstitutionAdminRequest($"c@{t.Slug}.test", "Chidi C", Password), slug: false));
        await Json<TenantUserDetail>(await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/users/{adminB.Id}/deactivate", platform, slug: false));

        var log = await Json<AuditLogPageResponse>(await Send(t, HttpMethod.Get, "/api/v1/audit-log?pageSize=100", t.AdminA));
        Assert.Contains(log.Items, i => i.Action == "AdminAdded" && i.ActorKind == "Platform" && i.Subject.Contains("Chidi C"));
        Assert.Contains(log.Items, i => i.Action == "UserDeactivated" && i.ActorKind == "Platform" && i.Subject.Contains(t.AdminBEmail));
        Assert.Contains(log.Items, i => i.Action == "UserAdded" && i.ActorKind == "Staff");
    }

    [Fact]
    public async Task IssuingWithoutApproval_NeedsASecondAdmin_AndStillGoesToFinalApproval()
    {
        var t = await SetUp();
        var created = await Json<CreateApiClientResponse>(await Send(t, HttpMethod.Post, "/api/v1/api-clients", t.AdminA,
            new CreateApiClientRequest("LMS", PartnerScope.All, true, false)));
        Assert.False(created.Client.AutoIssue);
        Assert.True(created.Client.AutoIssuePending);

        var template = await Template(t);
        var before = await PartnerIssue(t, created.ApiKey, template.Id, "p-1");
        Assert.Equal("PendingApproval", before.Status);

        var request = (await PendingRequests(t, t.AdminB)).Single(r => r.Kind == ControlRequestKind.EnableAutoIssue);
        await Json<ControlRequestResponse>(await Send(t, HttpMethod.Post, $"/api/v1/control-requests/{request.Id}/confirm", t.AdminB));

        // Auto-issue gives the first approval only: the template needs final approval.
        var after = await PartnerIssue(t, created.ApiKey, template.Id, "p-2");
        Assert.Equal("PendingApproval", after.Status); // partners don't see the internal final step
        Assert.Equal("awaiting_approval", after.NextStep!.Code);
        await using (var db = Db(t))
        {
            var stored = await db.Credentials.AsNoTracking().FirstAsync(c => c.Id == after.Id);
            Assert.Equal(CredentialStatus.PendingFinalApproval, stored.Status);
        }
        // Admin B confirmed the policy, so admin A gives the final approval.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/credentials/{after.Id}/approve", t.AdminB)).StatusCode);
        Assert.Equal("Active", (await Json<CredentialResponse>(await Send(t, HttpMethod.Post, $"/api/v1/credentials/{after.Id}/approve", t.AdminA))).Status);
    }

    [Fact]
    public async Task PaperImports_NeedFinalApprovalOfTheWholeBatch()
    {
        var t = await SetUp();
        var template = await Template(t);
        var rows = new[]
        {
            new ImportRow("Adjoa Darko", "adjoa@example.com", null, "12/03/2015", "P-1", null, null),
            new ImportRow("Kwame Ofori", "kwame@example.com", null, "12/03/2015", "P-2", null, null),
        };
        var batch = await Json<ImportBatchResponse>(await Send(t, HttpMethod.Post, "/api/v1/imports", t.Registrar, new CreateImportBatchRequest(template.Id, "old.xlsx", 2)));
        await Json<ImportRowsResponse>(await Send(t, HttpMethod.Post, $"/api/v1/imports/{batch.Id}/rows", t.Registrar, new ImportRowsRequest("Diploma", "DMY", false, rows, 0)));
        await Json<ImportBatchResponse>(await Send(t, HttpMethod.Post, $"/api/v1/imports/{batch.Id}/finish", t.Registrar));

        var first = await Json<ImportProgressResponse>(await Send(t, HttpMethod.Post, $"/api/v1/imports/{batch.Id}/approve", t.Approver, new ApproveImportRequest(2)));
        Assert.Equal(ImportBatchStatus.PendingFinalApproval, first.Status);
        Assert.True((await Json<ImportBatchDetailResponse>(await Send(t, HttpMethod.Get, $"/api/v1/imports/{batch.Id}", t.AdminA))).Batch.CanFinalApprove);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/imports/{batch.Id}/final-approve", t.Approver, new ApproveImportRequest(2))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, $"/api/v1/imports/{batch.Id}/final-approve", t.AdminA, new ApproveImportRequest(3))).StatusCode);

        var done = await Json<ImportProgressResponse>(await Send(t, HttpMethod.Post, $"/api/v1/imports/{batch.Id}/final-approve", t.AdminA, new ApproveImportRequest(2)));
        Assert.Equal((ImportBatchStatus.Approved, 0), (done.Status, done.Remaining));
        await using var db = Db(t);
        Assert.Equal(2, await db.Credentials.CountAsync(c => c.ImportBatchId == batch.Id && c.Status == CredentialStatus.Active));
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema, string AdminA, string AdminB, string AdminBEmail, string Registrar, string Approver);

    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var slug = $"fa-{suffix}";
        Tenant tenant;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>().ProvisionAsync($"Final {suffix}", slug,
                [new NewInstitutionAdmin($"a@{suffix}.test", "Admin Alpha", Password), new NewInstitutionAdmin($"b@{suffix}.test", "Admin Beta", Password)]);
        }
        var t = new Setup(_factory.CreateClient(), slug, tenant.Id, tenant.SchemaName, "", "", $"b@{suffix}.test", "", "");
        // They'd choose new passwords at first sign-in; skipped here.
        await using (var db = Db(t))
        {
            await db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.MustChangePassword, false));
        }
        var adminA = await Login(t, $"a@{suffix}.test");
        t = t with { AdminA = adminA, AdminB = await Login(t, $"b@{suffix}.test") };
        await Json<UserResponse>(await Send(t, HttpMethod.Post, "/api/v1/users", adminA, new CreateUserRequest($"reg@{suffix}.test", "Rita Registrar", Password, SystemRole.Registrar)));
        await Json<UserResponse>(await Send(t, HttpMethod.Post, "/api/v1/users", adminA, new CreateUserRequest($"app@{suffix}.test", "Abel Approver", Password, SystemRole.Approver)));
        t = t with { Registrar = await Login(t, $"reg@{suffix}.test"), Approver = await Login(t, $"app@{suffix}.test") };
        await Json<CertificateTemplateResponse>(await Send(t, HttpMethod.Post, "/api/v1/certificate-templates", adminA,
            new CreateCertificateTemplateRequest("Degree", null, "Certificate", "[]", RequiresFinalApproval: true)));
        return t;
    }

    private async Task<Guid> Request(Setup t, string name, string? token = null) =>
        (await Json<CredentialResponse>(await Send(t, HttpMethod.Post, "/api/v1/credentials/from-template", token ?? t.Registrar,
            new CreateCredentialFromTemplateRequest((await Template(t)).Id, "Bachelor of Science", name, $"{name.Split(' ')[0].ToLowerInvariant()}.{Guid.NewGuid():N}@example.com", "{}")))).Id;

    private async Task<CertificateTemplateResponse> Template(Setup t) =>
        (await Json<List<CertificateTemplateResponse>>(await Send(t, HttpMethod.Get, "/api/v1/certificate-templates", t.AdminA))).Single(x => x.Name == "Degree");

    private async Task<CertificateTemplateResponse> SetFinalApproval(Setup t, string token, bool on)
    {
        var x = await Template(t);
        return await Json<CertificateTemplateResponse>(await Send(t, HttpMethod.Put, $"/api/v1/certificate-templates/{x.Id}", token,
            new UpdateCertificateTemplateRequest(x.Name, x.Description, x.Kind, x.FieldSchemaJson, LayoutJson: x.LayoutJson, SignatoryIds: x.SignatoryIds, RequiresFinalApproval: on)));
    }

    private async Task<List<ControlRequestResponse>> PendingRequests(Setup t, string token) =>
        await Json<List<ControlRequestResponse>>(await Send(t, HttpMethod.Get, "/api/v1/control-requests?pendingOnly=true", token));

    private static async Task<PartnerCredentialResponse> PartnerIssue(Setup t, string key, Guid templateId, string reference)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/credentials")
        {
            Content = JsonContent.Create(new PartnerIssueCredentialRequest(templateId, "Short Course", "Lia Partner", $"lia.{Guid.NewGuid():N}@example.com",
                JsonSerializer.SerializeToElement(new { }), reference, null)),
        };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Add("X-Api-Key", key);
        return await Json<PartnerCredentialResponse>(await t.Client.SendAsync(request));
    }

    private string PlatformToken() =>
        TestPlatformAdmins.CreateWithToken(_factory.Services, $"ops.{Guid.NewGuid():N}@ahamsys.test", "Ops Person");

    private TenantDbContext Db(Setup t) =>
        _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema);

    private static async Task<string> Login(Setup t, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, Password)) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        return (await Json<LoginResponse>(await t.Client.SendAsync(request))).AccessToken;
    }

    private static Task<HttpResponseMessage> Send(Setup t, HttpMethod method, string url, string token, object? body = null, bool slug = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null || method == HttpMethod.Post) request.Content = JsonContent.Create(body ?? new { });
        if (slug) request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
