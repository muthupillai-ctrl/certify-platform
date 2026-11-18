using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// The Verification page's activity log (VerificationActivityController) and
// the Reports page (ReportsController): issuance, status changes, and the
// staff activity / two-person audit.
public class ReportsAndActivityTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ReportsAndActivityTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---------- Verification activity ----------

    [Fact]
    public async Task ActivityLog_FiltersAndCounts_AndFlagsWhatNeedsAttention()
    {
        var t = await SetUp();
        var all = await Json<VerificationActivityPageResponse>(await Send(t, "/api/v1/verification-activity", t.RegistrarToken));
        Assert.Equal(5, all.Total);
        Assert.Equal((2, 2, 1, 1), (all.ResultCounts["Valid"], all.ResultCounts["Attention"], all.ResultCounts["NotFound"], all.ResultCounts["Revoked"]));
        Assert.True(all.Items[0].OccurredAt >= all.Items[^1].OccurredAt); // newest first

        var attention = await Json<VerificationActivityPageResponse>(await Send(t, "/api/v1/verification-activity?result=Attention", t.AdminToken));
        Assert.Equal(2, attention.Total);
        Assert.Equal(5, attention.AllResults); // counts ignore the result filter
        var unmatched = attention.Items.Single(i => i.Result == "NotFound");
        Assert.Null(unmatched.RecipientName);

        var lookups = await Json<VerificationActivityPageResponse>(await Send(t, "/api/v1/verification-activity?channel=CodeLookup", t.AdminToken));
        Assert.Equal(1, lookups.Total);

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        var recent = await Json<VerificationActivityPageResponse>(await Send(t, $"/api/v1/verification-activity?from={from}", t.AdminToken));
        Assert.Equal(4, recent.Total); // one check was 40 days ago

        var csv = await Send(t, "/api/v1/verification-activity/export.csv?result=Attention", t.AdminToken);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("No such certificate", text);
        Assert.Contains("Revoked", text);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "/api/v1/verification-activity", t.LearnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, "/api/v1/verification-activity?result=Bogus", t.AdminToken)).StatusCode);
    }

    // ---------- Reports ----------

    [Fact]
    public async Task IssuanceReport_SummarisesThePeriod()
    {
        var t = await SetUp();
        var r = await Json<IssuanceReportResponse>(await Send(t, $"/api/v1/reports/issuance?{Period()}", t.AdminToken));

        Assert.Equal(2, r.Total);
        Assert.Equal(("Fire Safety", 2), (r.ByAchievement.Single().Name, r.ByAchievement.Single().Count));
        Assert.Equal(("Registrar", 2), (r.ByRequester.Single().Name, r.ByRequester.Single().Count));
        Assert.Equal(("Approver", 2), (r.ByApprover.Single().Name, r.ByApprover.Single().Count));
        Assert.Equal(("Staff", 2), (r.BySource.Single().Name, r.BySource.Single().Count));
        Assert.Equal(["Active", "Revoked"], r.Rows.Select(x => x.Status).Order());

        var csv = await (await Send(t, $"/api/v1/reports/issuance.csv?{Period()}", t.AdminToken)).Content.ReadAsStringAsync();
        Assert.Contains("Issued (UTC),Recipient,Email", csv);
        Assert.Equal(3, csv.Trim().Split("\r\n").Length);

        // Admin only, and a period is required.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, $"/api/v1/reports/issuance?{Period()}", t.ApproverToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, "/api/v1/reports/issuance", t.AdminToken)).StatusCode);
        // A period before anything happened is empty.
        var old = await Json<IssuanceReportResponse>(await Send(t, $"/api/v1/reports/issuance?{Period(-400, -300)}", t.AdminToken));
        Assert.Equal(0, old.Total);
    }

    [Fact]
    public async Task StatusChangesReport_ComesFromTheLedger_WithWhoAskedAndWhy()
    {
        var t = await SetUp();
        var r = await Json<StatusChangesReportResponse>(await Send(t, $"/api/v1/reports/status-changes?{Period()}", t.AdminToken));

        Assert.Equal(["Suspended", "Reactivated", "Revocation asked for", "Revoked"], r.Rows.Select(x => x.Change));
        Assert.Equal("Checking records", r.Rows[0].Reason);
        var revoked = r.Rows[^1];
        Assert.Equal(("Institution Administrator", "Approver", "Issued in error: Wrong course"), (revoked.By, revoked.RequestedBy, revoked.Reason));
        Assert.Equal(1, r.ByChange.Single(c => c.Name == "Revoked").Count);

        var csv = await (await Send(t, $"/api/v1/reports/status-changes.csv?{Period()}", t.AdminToken)).Content.ReadAsStringAsync();
        Assert.Contains("Issued in error: Wrong course", csv);
    }

    [Fact]
    public async Task StaffActivityReport_CountsPerPerson_AndChecksTheTwoPersonRule()
    {
        var t = await SetUp();
        var r = await Json<StaffActivityReportResponse>(await Send(t, $"/api/v1/reports/staff-activity?{Period()}", t.AdminToken));

        var registrar = r.People.Single(p => p.Name == "Registrar");
        Assert.Equal(3, registrar.Counts["Requested"]);
        var approver = r.People.Single(p => p.Name == "Approver");
        Assert.Equal((2, 1, 1, 1), (approver.Counts["Approved"], approver.Counts["Suspended"], approver.Counts["Reactivated"], approver.Counts["RevocationRequested"]));
        var admin = r.People.Single(p => p.Name == "Institution Administrator");
        Assert.Equal((1, 1), (admin.Counts["Rejected"], admin.Counts["Revoked"]));

        Assert.Equal((2, 1), (r.DecisionsChecked, r.RevocationsChecked)); // approvals only
        Assert.Empty(r.DecisionViolations);
        Assert.Empty(r.RevocationViolations);

        // If a record ever showed the same person on both sides, it's listed.
        await using (var db = Db(t))
            await db.Credentials.Where(c => c.RecipientName == "Bob Jones")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.DecidedByUserId, c => c.RequestedByUserId));
        r = await Json<StaffActivityReportResponse>(await Send(t, $"/api/v1/reports/staff-activity?{Period()}", t.AdminToken));
        Assert.Equal("Bob Jones", Assert.Single(r.DecisionViolations).RecipientName);

        var csv = await (await Send(t, $"/api/v1/reports/staff-activity.csv?{Period()}", t.AdminToken)).Content.ReadAsStringAsync();
        Assert.Contains("Revocation asked for", csv);
    }

    // ---------- helpers ----------

    private static string Period(int fromDays = -1, int beforeDays = 1) =>
        $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(fromDays).ToString("O"))}&before={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(beforeDays).ToString("O"))}";

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema,
        string AdminToken, string ApproverToken, string RegistrarToken, string LearnerToken);

    // Registrar requests Alice, Bob, Carol. Approver approves Alice and Bob;
    // admin rejects Carol. Approver suspends and reactivates Alice, then asks
    // to revoke Bob; admin approves. Five public checks: Alice x2 (valid, one
    // 40 days ago), Bob (revoked), a code look-up that matched, one that didn't.
    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Tenant tenant;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!");
        }
        var client = _factory.CreateClient();
        var t = new Setup(client, tenant.Slug, tenant.Id, tenant.SchemaName, "", "", "", "");
        var admin = await Login(t, $"admin@{suffix}.test", "TenantAdmin123!");
        foreach (var role in new[] { "Approver", "Registrar", "Learner" })
            await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"{role.ToLowerInvariant()}@{suffix}.test", role, "Password123!", role));
        t = t with
        {
            AdminToken = admin,
            ApproverToken = await Login(t, $"approver@{suffix}.test", "Password123!"),
            RegistrarToken = await Login(t, $"registrar@{suffix}.test", "Password123!"),
            LearnerToken = await Login(t, $"learner@{suffix}.test", "Password123!"),
        };

        var fire = (await Json<AchievementResponse>(await Send(t, "/api/v1/achievements", admin, HttpMethod.Post,
            new CreateAchievementRequest("Fire Safety", null, null, null, "[]", null)))).Id;
        var made = new Dictionary<string, CredentialResponse>();
        foreach (var name in new[] { "Alice Smith", "Bob Jones", "Carol White" })
            made[name] = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", t.RegistrarToken, HttpMethod.Post,
                new CreateCredentialRequest(fire, name, $"{name.Split(' ')[0].ToLowerInvariant()}@example.com", "{}")));
        var alice = made["Alice Smith"];
        var bob = made["Bob Jones"];
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{alice.Id}/approve", t.ApproverToken, HttpMethod.Post));
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{bob.Id}/approve", t.ApproverToken, HttpMethod.Post));
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{made["Carol White"].Id}/reject", admin, HttpMethod.Post, new RejectCredentialRequest("Duplicate")));
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{alice.Id}/suspend", t.ApproverToken, HttpMethod.Post,
            new SuspendCredentialRequest("Checking records", alice.VerificationCode)));
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{alice.Id}/reactivate", t.ApproverToken, HttpMethod.Post,
            new ReactivateCredentialRequest("Records fine", alice.VerificationCode)));
        var request = await Json<RevocationRequestResponse>(await Send(t, "/api/v1/revocation-requests", t.ApproverToken, HttpMethod.Post,
            new CreateRevocationRequest(bob.Id, "IssuedInError", "Wrong course", bob.VerificationCode)));
        await Json<RevocationRequestResponse>(await Send(t, $"/api/v1/revocation-requests/{request.Id}/approve", admin, HttpMethod.Post,
            new ApproveRevocationRequest(bob.VerificationCode)));

        await using var db = Db(t);
        db.VerificationEvents.AddRange(
            new VerificationEvent { CredentialId = alice.Id, Channel = VerificationChannel.VerifyPage, Result = "Active" },
            new VerificationEvent { CredentialId = alice.Id, Channel = VerificationChannel.VerifyPage, Result = "Active", OccurredAt = DateTimeOffset.UtcNow.AddDays(-40) },
            new VerificationEvent { CredentialId = bob.Id, Channel = VerificationChannel.VerifyPage, Result = "Revoked" },
            new VerificationEvent { CredentialId = alice.Id, Channel = VerificationChannel.Api, Result = "Found" },
            new VerificationEvent { CredentialId = null, Channel = VerificationChannel.CodeLookup, Result = "NotFound" });
        await db.SaveChangesAsync();
        return t;
    }

    private TenantDbContext Db(Setup t) =>
        _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema);

    private static async Task<string> Login(Setup t, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        return (await Json<LoginResponse>(await t.Client.SendAsync(request))).AccessToken;
    }

    private static Task<HttpResponseMessage> Send(Setup t, string url, string token, HttpMethod? method = null, object? body = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
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
