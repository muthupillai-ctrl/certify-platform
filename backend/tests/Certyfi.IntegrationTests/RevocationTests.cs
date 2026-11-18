using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Two-person revocation (RevocationRequestsController / RevocationService):
// asking doesn't revoke; a different Approver/Admin must approve; both steps
// need the certificate's code typed; the requester can withdraw, others can
// reject; everything lands on the ledger.
public class RevocationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RevocationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_ThenADifferentPersonApproves_Revokes()
    {
        var t = await SetUp();
        var request = await Json<RevocationRequestResponse>(await Send(t, "/api/v1/revocation-requests", t.ApproverToken, HttpMethod.Post,
            new CreateRevocationRequest(t.CredentialId, "IssuedInError", "Wrong course", t.Code)));

        // Nothing changes yet; the certificate shows the waiting request.
        var detail = await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{t.CredentialId}", t.AdminToken));
        Assert.Equal("Active", detail.Status);
        Assert.Equal(("Issued in error", "Wrong course", "Approver"), (detail.PendingRevocation!.ReasonLabel, detail.PendingRevocation.Note, detail.PendingRevocation.RequestedByName));

        // The requester sees it as theirs; the admin can decide it.
        var mine = Assert.Single(await Json<List<RevocationRequestResponse>>(await Send(t, "/api/v1/revocation-requests", t.ApproverToken)));
        Assert.Equal((false, true), (mine.CanDecide, mine.CanWithdraw));
        var theirs = Assert.Single(await Json<List<RevocationRequestResponse>>(await Send(t, "/api/v1/revocation-requests", t.AdminToken)));
        Assert.Equal((true, false), (theirs.CanDecide, theirs.CanWithdraw));
        Assert.Equal(1, (await Json<CredentialSummaryCountsResponse>(await Send(t, "/api/v1/credentials/summary", t.AdminToken))).RevocationsForMe);
        Assert.Equal(0, (await Json<CredentialSummaryCountsResponse>(await Send(t, "/api/v1/credentials/summary", t.ApproverToken))).RevocationsForMe);

        // Maker–checker, and the code must be typed.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, $"/api/v1/revocation-requests/{request.Id}/approve", t.ApproverToken, HttpMethod.Post,
            new ApproveRevocationRequest(t.Code))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, $"/api/v1/revocation-requests/{request.Id}/approve", t.AdminToken, HttpMethod.Post,
            new ApproveRevocationRequest("2222-2222-2222"))).StatusCode);

        var approved = await Json<RevocationRequestResponse>(await Send(t, $"/api/v1/revocation-requests/{request.Id}/approve", t.AdminToken, HttpMethod.Post,
            new ApproveRevocationRequest(t.Code)));
        Assert.Equal(("Approved", "Revoked", "Institution Administrator"), (approved.Status, approved.CredentialStatus, approved.DecidedByName));

        detail = await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{t.CredentialId}", t.AdminToken));
        Assert.Equal("Revoked", detail.Status);
        Assert.Null(detail.PendingRevocation);
        Assert.Equal("Issued in error: Wrong course", detail.RevocationReason);
        var ledger = await Json<List<LedgerEntryResponse>>(await Send(t, $"/api/v1/credentials/{t.CredentialId}/ledger", t.AdminToken));
        Assert.Equal(["Requested", "Approved", "RevocationRequested", "Revoked"], ledger.Select(e => e.EventType));

        // Once decided it's history, not a second decision.
        Assert.Equal(HttpStatusCode.Conflict, (await Send(t, $"/api/v1/revocation-requests/{request.Id}/approve", t.AdminToken, HttpMethod.Post,
            new ApproveRevocationRequest(t.Code))).StatusCode);
        Assert.Equal(request.Id, Assert.Single(await Json<List<RevocationRequestResponse>>(await Send(t, "/api/v1/revocation-requests?state=decided", t.AdminToken))).Id);
    }

    [Fact]
    public async Task Reject_KeepsTheCertificate_AndAllowsANewRequest()
    {
        var t = await SetUp();
        var request = await Json<RevocationRequestResponse>(await Send(t, "/api/v1/revocation-requests", t.ApproverToken, HttpMethod.Post,
            new CreateRevocationRequest(t.CredentialId, "Misconduct", "Reported by examiner", t.Code)));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, $"/api/v1/revocation-requests/{request.Id}/reject", t.ApproverToken, HttpMethod.Post,
            new RejectRevocationRequest("changed my mind"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, $"/api/v1/revocation-requests/{request.Id}/reject", t.AdminToken, HttpMethod.Post,
            new RejectRevocationRequest(" "))).StatusCode);
        var rejected = await Json<RevocationRequestResponse>(await Send(t, $"/api/v1/revocation-requests/{request.Id}/reject", t.AdminToken, HttpMethod.Post,
            new RejectRevocationRequest("Investigation found no misconduct")));
        Assert.Equal(("Rejected", "Active", "Investigation found no misconduct"), (rejected.Status, rejected.CredentialStatus, rejected.DecisionNote));

        Assert.Equal(HttpStatusCode.OK, (await Send(t, "/api/v1/revocation-requests", t.AdminToken, HttpMethod.Post,
            new CreateRevocationRequest(t.CredentialId, "IssuedInError", "Second look", t.Code))).StatusCode);
    }

    [Fact]
    public async Task OnlyTheRequesterCanWithdraw()
    {
        var t = await SetUp();
        var request = await Json<RevocationRequestResponse>(await Send(t, "/api/v1/revocation-requests", t.ApproverToken, HttpMethod.Post,
            new CreateRevocationRequest(t.CredentialId, "HolderRequest", "Holder asked", t.Code)));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, $"/api/v1/revocation-requests/{request.Id}/withdraw", t.AdminToken, HttpMethod.Post)).StatusCode);
        var withdrawn = await Json<RevocationRequestResponse>(await Send(t, $"/api/v1/revocation-requests/{request.Id}/withdraw", t.ApproverToken, HttpMethod.Post));
        Assert.Equal(("Withdrawn", "Active"), (withdrawn.Status, withdrawn.CredentialStatus));
        Assert.Empty(await Json<List<RevocationRequestResponse>>(await Send(t, "/api/v1/revocation-requests", t.AdminToken)));
    }

    [Fact]
    public async Task Requests_AreValidated()
    {
        var t = await SetUp();
        async Task<HttpStatusCode> Ask(string token, Guid id, string category, string note, string? code) =>
            (await Send(t, "/api/v1/revocation-requests", token, HttpMethod.Post, new CreateRevocationRequest(id, category, note, code))).StatusCode;

        Assert.Equal(HttpStatusCode.Forbidden, await Ask(t.RegistrarToken, t.CredentialId, "IssuedInError", "x", t.Code));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(t.AdminToken, t.CredentialId, "IssuedInError", "x", null));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(t.AdminToken, t.CredentialId, "Bogus", "x", t.Code));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(t.AdminToken, t.CredentialId, "PartnerSystem", "x", t.Code));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(t.AdminToken, t.CredentialId, "IssuedInError", " ", t.Code));
        Assert.Equal(HttpStatusCode.NotFound, await Ask(t.AdminToken, Guid.NewGuid(), "IssuedInError", "x", t.Code));
        Assert.Equal(HttpStatusCode.Conflict, await Ask(t.AdminToken, t.PendingCredentialId, "IssuedInError", "x", t.PendingCode));

        Assert.Equal(HttpStatusCode.OK, await Ask(t.AdminToken, t.CredentialId, "IssuedInError", "x", t.Code));
        Assert.Equal(HttpStatusCode.Conflict, await Ask(t.ApproverToken, t.CredentialId, "IssuedInError", "again", t.Code));
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, string AdminToken, string ApproverToken, string RegistrarToken,
        Guid CredentialId, string Code, Guid PendingCredentialId, string PendingCode);

    // One active certificate (requested by the registrar, approved by the
    // admin) and one still pending approval.
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
        var admin = await Login(client, tenant.Slug, $"admin@{suffix}.test", "TenantAdmin123!");
        var t = new Setup(client, tenant.Slug, admin, "", "", Guid.Empty, "", Guid.Empty, "");
        await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"app@{suffix}.test", "Approver", "Password123!", "Approver"));
        await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"reg@{suffix}.test", "Registrar", "Password123!", "Registrar"));
        var approver = await Login(client, tenant.Slug, $"app@{suffix}.test", "Password123!");
        var registrar = await Login(client, tenant.Slug, $"reg@{suffix}.test", "Password123!");

        var achievement = (await Json<AchievementResponse>(await Send(t, "/api/v1/achievements", admin, HttpMethod.Post,
            new CreateAchievementRequest("Fire Safety", null, null, null, "[]", null)))).Id;
        var active = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", registrar, HttpMethod.Post,
            new CreateCredentialRequest(achievement, "Alice Smith", "alice@example.com", "{}")));
        await Json<CredentialResponse>(await Send(t, $"/api/v1/credentials/{active.Id}/approve", admin, HttpMethod.Post));
        var pending = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", registrar, HttpMethod.Post,
            new CreateCredentialRequest(achievement, "Bob Jones", "bob@example.com", "{}")));

        return t with
        {
            ApproverToken = approver, RegistrarToken = registrar,
            CredentialId = active.Id, Code = active.VerificationCode.ToLowerInvariant(),
            PendingCredentialId = pending.Id, PendingCode = pending.VerificationCode,
        };
    }

    private static async Task<string> Login(HttpClient client, string slug, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", slug);
        return (await Json<LoginResponse>(await client.SendAsync(request))).AccessToken;
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
