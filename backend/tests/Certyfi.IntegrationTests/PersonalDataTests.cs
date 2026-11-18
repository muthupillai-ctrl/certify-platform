using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Personal data and the audit trail: names, emails and free-text reasons are
// kept off the hash chain (LedgerEntryDetail) behind salted fingerprints, so
// they can be erased on request without breaking the chain, and changing
// them directly in the database is still detected.
public class PersonalDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PersonalDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TheChainHoldsNoPersonalData_OnlyFingerprints_AndReportsStillShowTheReasons()
    {
        var t = await SetUp();
        await SuspendReactivateAndRevoke(t);

        var ledger = await Json<List<LedgerEntryResponse>>(await Send(t, $"/api/v1/credentials/{t.CredentialId}/ledger", t.AdminToken));
        Assert.Equal(["Requested", "Approved", "Suspended", "Reactivated", "RevocationRequested", "Revoked"], ledger.Select(e => e.EventType));
        foreach (var entry in ledger)
        {
            Assert.DoesNotContain("Adaeze", entry.EventDataJson);
            Assert.DoesNotContain("adaeze@example.com", entry.EventDataJson);
            Assert.DoesNotContain("Checking records", entry.EventDataJson);
            Assert.DoesNotContain("Wrong course", entry.EventDataJson);
        }
        Assert.All(ledger.Where(e => e.EventType != "Approved"), e => Assert.Contains("\"personalData\":\"hmac-sha256:", e.EventDataJson));

        await using (var db = Db(t))
            Assert.Equal(5, await db.LedgerEntryDetails.CountAsync(d => d.CredentialId == t.CredentialId));

        Assert.True((await Integrity(t)).GetProperty("intact").GetBoolean());

        var report = await Json<StatusChangesReportResponse>(await Send(t, $"/api/v1/reports/status-changes?{Period()}", t.AdminToken));
        Assert.Equal("Checking records", report.Rows.Single(r => r.Change == "Suspended").Reason);
        Assert.Equal("Issued in error: Wrong course", report.Rows.Single(r => r.Change == "Revoked").Reason);
    }

    [Fact]
    public async Task ChangingOrDeletingTheDetails_IsDetected()
    {
        var t = await SetUp();
        await using (var db = Db(t))
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{t.Schema}\".ledger_entry_details SET \"DataJson\" = replace(\"DataJson\", 'Adaeze Okonkwo', 'Someone Else')");
        var report = await Integrity(t);
        Assert.False(report.GetProperty("intact").GetBoolean());
        Assert.Equal("DetailsChanged", report.GetProperty("problem").GetProperty("kind").GetString());

        var other = await SetUp();
        await using (var db = Db(other))
            await db.LedgerEntryDetails.Where(d => d.CredentialId == other.CredentialId).ExecuteDeleteAsync();
        Assert.Equal("DetailsChanged", (await Integrity(other)).GetProperty("problem").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Erasure_WipesThePerson_KeepsTheChainIntact_AndHidesThePublicPage()
    {
        var t = await SetUp();

        // Only once the certificate is no longer valid.
        Assert.Equal(HttpStatusCode.Conflict, (await Erase(t, t.AdminToken, t.Code)).StatusCode);
        await SuspendReactivateAndRevoke(t);
        Assert.Equal(HttpStatusCode.Forbidden, (await Erase(t, t.ApproverToken, t.Code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Erase(t, t.AdminToken, "2222-2222-2222")).StatusCode);

        var erased = await Json<CredentialResponse>(await Erase(t, t.AdminToken, t.Code));
        Assert.Equal(("[erased]", "", "{}"), (erased.RecipientName, erased.RecipientEmail, erased.FieldValuesJson));
        Assert.Null(erased.RevocationReason);
        Assert.Null(erased.CanonicalHash);
        Assert.NotNull(erased.PersonalDataErasedAt);

        await using (var db = Db(t))
        {
            Assert.Equal(0, await db.LedgerEntryDetails.CountAsync(d => d.CredentialId == t.CredentialId));
            var credential = await db.Credentials.SingleAsync(c => c.Id == t.CredentialId);
            Assert.Null(credential.PersonalDataSalt);
            Assert.Equal("[erased]", (await db.RevocationRequests.SingleAsync(r => r.CredentialId == t.CredentialId)).Note);
        }

        // The chain is untouched and still verifies; the erasure is on it.
        Assert.True((await Integrity(t)).GetProperty("intact").GetBoolean());
        var ledger = await Json<List<LedgerEntryResponse>>(await Send(t, $"/api/v1/credentials/{t.CredentialId}/ledger", t.AdminToken));
        Assert.Equal("PersonalDataErased", ledger[^1].EventType);

        // Gone from public verification and look-up.
        Assert.Equal(HttpStatusCode.NotFound, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{t.CredentialId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup/{Uri.EscapeDataString(t.Code)}")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await Erase(t, t.AdminToken, t.Code)).StatusCode);
    }

    // ---------- helpers ----------

    private async Task SuspendReactivateAndRevoke(Setup t)
    {
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{t.CredentialId}/suspend", t.ApproverToken, HttpMethod.Post,
            new SuspendCredentialRequest("Checking records", t.Code)));
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{t.CredentialId}/reactivate", t.ApproverToken, HttpMethod.Post,
            new ReactivateCredentialRequest("Records fine", t.Code)));
        var request = await Json<RevocationRequestResponse>(await Send(t, "/api/v1/revocation-requests", t.ApproverToken, HttpMethod.Post,
            new CreateRevocationRequest(t.CredentialId, "IssuedInError", "Wrong course", t.Code)));
        await Json<RevocationRequestResponse>(await Send(t, $"/api/v1/revocation-requests/{request.Id}/approve", t.AdminToken, HttpMethod.Post,
            new ApproveRevocationRequest(t.Code)));
    }

    private static Task<HttpResponseMessage> Erase(Setup t, string token, string code) =>
        Send(t, $"/api/v1/credentials/{t.CredentialId}/erase-personal-data", token, HttpMethod.Post, new ErasePersonalDataRequest(code));

    private static async Task<JsonElement> Integrity(Setup t) =>
        await Json<JsonElement>(await Send(t, "/api/v1/ledger/integrity", t.AdminToken));

    private static string Period() =>
        $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&before={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema, string AdminToken, string ApproverToken, Guid CredentialId, string Code);

    // One active certificate for Adaeze Okonkwo: requested by the registrar,
    // approved by the approver.
    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Tenant tenant;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!");
        }
        var t = new Setup(_factory.CreateClient(), tenant.Slug, tenant.Id, tenant.SchemaName, "", "", Guid.Empty, "");
        var admin = await Login(t, $"admin@{suffix}.test", "TenantAdmin123!");
        t = t with { AdminToken = admin };
        foreach (var role in new[] { "Approver", "Registrar" })
            await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"{role.ToLowerInvariant()}@{suffix}.test", role, "Password123!", role));
        var approver = await Login(t, $"approver@{suffix}.test", "Password123!");
        var registrar = await Login(t, $"registrar@{suffix}.test", "Password123!");

        const string schema = """[{"key":"roll","label":"Roll number","type":"text"}]""";
        var achievement = (await Json<AchievementResponse>(await Send(t, "/api/v1/achievements", admin, HttpMethod.Post,
            new CreateAchievementRequest("Diploma", null, null, null, schema, null)))).Id;
        var draft = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", registrar, HttpMethod.Post,
            new CreateCredentialRequest(achievement, "Adaeze Okonkwo", "adaeze@example.com", """{"roll":"R-17"}""")));
        var approved = await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{draft.Id}/approve", approver, HttpMethod.Post));
        return t with { ApproverToken = approver, CredentialId = approved.Id, Code = approved.VerificationCode };
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
