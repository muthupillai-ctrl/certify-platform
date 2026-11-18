using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves the Partner API end to end over real HTTP: API keys issued by an
// InstitutionAdmin, scope checks, maker-checker (pending by default,
// auto-issue only when the admin allowed it), idempotent retries by
// externalReference, per-key and per-tenant isolation, revocation, and that
// keys and staff JWTs can't be used on each other's endpoints.
public class PartnerApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string FieldSchemaJson = """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""";

    private readonly CustomWebApplicationFactory _factory;

    public PartnerApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task IssuedCredential_IsPendingByDefault_AndBecomesActiveAfterHumanApproval()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: false);

        var issue = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, key, IssueRequest(t.TemplateId, "ref-1"));
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var created = (await issue.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;
        Assert.Equal("PendingApproval", created.Status);
        Assert.Null(created.VerificationUrl);
        Assert.Equal("ref-1", created.ExternalReference);

        // Pending credentials are never publicly verifiable.
        Assert.Equal(HttpStatusCode.NotFound, (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{created.Id}")).StatusCode);

        var approve = await StaffPost(t.Client, $"/api/v1/credentials/{created.Id}/approve", t.Slug, t.TokenApprover, null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var fetched = await PartnerGet<PartnerCredentialResponse>(t.Client, $"/api/v1/partner/credentials/{created.Id}", t.Slug, key);
        Assert.Equal("Active", fetched.Status);
        Assert.NotNull(fetched.IssuedAt);
    }

    [Fact]
    public async Task AutoIssueKey_ActivatesAndSignsImmediately_WithAdminRecordedAsApprover()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: true);

        var issue = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, key, IssueRequest(t.TemplateId, "ref-auto"));
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var created = (await issue.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;
        Assert.Equal("Active", created.Status);

        var verify = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verifyBody = (await verify.Content.ReadFromJsonAsync<VerifyCredentialResponse>())!;
        Assert.NotNull(verifyBody.Signature);

        var staffView = await StaffGet<CredentialResponse>(t.Client, $"/api/v1/credentials/{created.Id}", t.Slug, t.TokenAdmin);
        // The admin who created the auto-issue key is the recorded checker.
        Assert.Equal("Institution Administrator", staffView.DecidedByName);
        Assert.StartsWith("API: ", staffView.RequestedByName);

        var ledger = await StaffGet<List<LedgerEntryResponse>>(t.Client, $"/api/v1/credentials/{created.Id}/ledger", t.Slug, t.TokenAdmin);
        var approved = Assert.Single(ledger, e => e.EventType == "Approved");
        Assert.Contains("AutoIssue", approved.EventDataJson);
    }

    [Fact]
    public async Task RetryWithSameExternalReference_ReturnsExistingCredential_WithoutIssuingTwice()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: false);

        var first = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, key, IssueRequest(t.TemplateId, "enrolment-42"));
        var second = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, key, IssueRequest(t.TemplateId, "enrolment-42"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = (await first.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;
        var secondBody = (await second.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;
        Assert.Equal(firstBody.Id, secondBody.Id);

        var list = await PartnerGet<List<PartnerCredentialResponse>>(t.Client, "/api/v1/partner/credentials?externalReference=enrolment-42", t.Slug, key);
        Assert.Single(list);
    }

    [Fact]
    public async Task InvalidFieldValues_AreRejected()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: false);

        var request = new PartnerIssueCredentialRequest(t.TemplateId, "Course Completion", "Jane Doe", "jane@example.com",
            // Same rules as the staff UI (FieldSchemaValidator): values must be strings.
            JsonSerializer.SerializeToElement(new { rollNumber = 42 }), null);
        var response = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, key, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task KeyWithoutScope_IsForbidden()
    {
        var t = await SetUpTenant();
        var readOnlyKey = await CreateKey(t, new[] { PartnerScope.TemplatesRead }, autoIssue: false);

        var templates = await PartnerGetRaw(t.Client, "/api/v1/partner/templates", t.Slug, readOnlyKey);
        Assert.Equal(HttpStatusCode.OK, templates.StatusCode);

        var issue = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, readOnlyKey, IssueRequest(t.TemplateId, null));
        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
    }

    [Fact]
    public async Task MissingMalformedOrRevokedKey_IsUnauthorized()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: false);

        var noKey = new HttpRequestMessage(HttpMethod.Get, "/api/v1/partner/templates");
        noKey.Headers.Add("X-Tenant-Slug", t.Slug);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Client.SendAsync(noKey)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PartnerGetRaw(t.Client, "/api/v1/partner/templates", t.Slug, "not-a-key")).StatusCode);
        // Right prefix, wrong secret.
        var tampered = key[..^4] + "AAAA";
        Assert.Equal(HttpStatusCode.Unauthorized, (await PartnerGetRaw(t.Client, "/api/v1/partner/templates", t.Slug, tampered)).StatusCode);

        var clients = await StaffGet<List<ApiClientResponse>>(t.Client, "/api/v1/api-clients", t.Slug, t.TokenAdmin);
        var revoke = await StaffPost(t.Client, $"/api/v1/api-clients/{clients.Single().Id}/revoke", t.Slug, t.TokenAdmin, null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PartnerGetRaw(t.Client, "/api/v1/partner/templates", t.Slug, key)).StatusCode);
    }

    [Fact]
    public async Task KeyFromOneTenant_IsRejectedByAnotherTenant()
    {
        var a = await SetUpTenant();
        var b = await SetUpTenant();
        var keyA = await CreateKey(a, PartnerScope.All, autoIssue: false);

        var response = await PartnerGetRaw(b.Client, "/api/v1/partner/templates", b.Slug, keyA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Key_CannotSeeOrRevoke_CredentialsItDidNotCreate()
    {
        var t = await SetUpTenant();
        var keyOne = await CreateKey(t, PartnerScope.All, autoIssue: true);
        var keyTwo = await CreateKey(t, PartnerScope.All, autoIssue: true);

        var issued = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, keyOne, IssueRequest(t.TemplateId, "shared-ref"));
        var credential = (await issued.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await PartnerGetRaw(t.Client, $"/api/v1/partner/credentials/{credential.Id}", t.Slug, keyTwo)).StatusCode);
        var revoke = await PartnerPost(t.Client, $"/api/v1/partner/credentials/{credential.Id}/revoke", t.Slug, keyTwo,
            new PartnerRevokeCredentialRequest("not mine"));
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        // The same externalReference under a different key is a different credential.
        var otherKeysIssue = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, keyTwo, IssueRequest(t.TemplateId, "shared-ref"));
        Assert.Equal(HttpStatusCode.Created, otherKeysIssue.StatusCode);
    }

    [Fact]
    public async Task PartnerRevoke_IsARequest_ThatStaffMustApprove()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: true);
        var issued = await PartnerPost(t.Client, "/api/v1/partner/credentials", t.Slug, key, IssueRequest(t.TemplateId, null));
        var credential = (await issued.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;

        var revoke = await PartnerPost(t.Client, $"/api/v1/partner/credentials/{credential.Id}/revoke", t.Slug, key,
            new PartnerRevokeCredentialRequest("Issued in error"));
        Assert.Equal(HttpStatusCode.Accepted, revoke.StatusCode);
        var accepted = (await revoke.Content.ReadFromJsonAsync<PartnerCredentialResponse>())!;
        Assert.True(accepted.RevocationRequested);
        Assert.Equal("Active", accepted.Status);

        // Asking again while it waits is not an error.
        var again = await PartnerPost(t.Client, $"/api/v1/partner/credentials/{credential.Id}/revoke", t.Slug, key,
            new PartnerRevokeCredentialRequest("Issued in error"));
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var verify = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{credential.Id}");
        Assert.Equal("Active", (await verify.Content.ReadFromJsonAsync<VerifyCredentialResponse>())!.Status);

        // A person approves it (the key is the requester, so the admin may).
        var queue = await StaffGet<List<RevocationRequestResponse>>(t.Client, "/api/v1/revocation-requests", t.Slug, t.TokenAdmin);
        var request = Assert.Single(queue);
        Assert.True(request.RequestedByPartnerApi);
        Assert.Equal("PartnerSystem", request.ReasonCategory);
        var approve = await StaffPost(t.Client, $"/api/v1/revocation-requests/{request.Id}/approve", t.Slug, t.TokenAdmin,
            new ApproveRevocationRequest(credential.VerificationCode));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        verify = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{credential.Id}");
        Assert.Equal("Revoked", (await verify.Content.ReadFromJsonAsync<VerifyCredentialResponse>())!.Status);
        var fetched = await PartnerGet<PartnerCredentialResponse>(t.Client, $"/api/v1/partner/credentials/{credential.Id}", t.Slug, key);
        Assert.Equal("Revoked", fetched.Status);
        Assert.False(fetched.RevocationRequested);
    }

    [Fact]
    public async Task ApiKey_CannotCallStaffEndpoints_AndStaffJwt_CannotCallPartnerEndpoints()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, PartnerScope.All, autoIssue: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PartnerGetRaw(t.Client, "/api/v1/credentials", t.Slug, key)).StatusCode);

        var staffOnPartner = new HttpRequestMessage(HttpMethod.Get, "/api/v1/partner/templates");
        staffOnPartner.Headers.Add("X-Tenant-Slug", t.Slug);
        staffOnPartner.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.TokenAdmin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Client.SendAsync(staffOnPartner)).StatusCode);
    }

    [Fact]
    public async Task ServiceAccounts_CannotLogIn_AndAreHiddenFromUsersList()
    {
        var t = await SetUpTenant();
        await CreateKey(t, PartnerScope.All, autoIssue: false);

        var users = await StaffGet<List<UserResponse>>(t.Client, "/api/v1/users", t.Slug, t.TokenAdmin);
        Assert.DoesNotContain(users, u => u.Email.EndsWith("@service.certyfi.invalid"));

        var clients = await StaffGet<List<ApiClientResponse>>(t.Client, "/api/v1/api-clients", t.Slug, t.TokenAdmin);
        var serviceEmail = $"apikey-{clients.Single().KeyPrefix}@service.certyfi.invalid";
        var login = await PostJson(t.Client, "/api/v1/auth/login", t.Slug, new TenantLoginRequest(serviceEmail, "anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task OnlyInstitutionAdmin_CanCreateKeys_AndAutoIssueNeedsWriteScope()
    {
        var t = await SetUpTenant();

        var byApprover = await StaffPost(t.Client, "/api/v1/api-clients", t.Slug, t.TokenApprover,
            new CreateApiClientRequest("LMS", PartnerScope.All, false));
        Assert.Equal(HttpStatusCode.Forbidden, byApprover.StatusCode);

        var autoIssueWithoutWrite = await StaffPost(t.Client, "/api/v1/api-clients", t.Slug, t.TokenAdmin,
            new CreateApiClientRequest("LMS", new[] { PartnerScope.CredentialsRead }, true));
        Assert.Equal(HttpStatusCode.BadRequest, autoIssueWithoutWrite.StatusCode);

        var unknownScope = await StaffPost(t.Client, "/api/v1/api-clients", t.Slug, t.TokenAdmin,
            new CreateApiClientRequest("LMS", new[] { "credentials:delete" }, false));
        Assert.Equal(HttpStatusCode.BadRequest, unknownScope.StatusCode);
    }

    [Fact]
    public async Task RateLimit_Returns429_OncePerKeyAllowanceIsUsed()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, new[] { PartnerScope.TemplatesRead }, autoIssue: false);

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 121 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = (await PartnerGetRaw(t.Client, "/api/v1/partner/templates", t.Slug, key)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task PartnerDocs_AreServedAnonymously_AndContainOnlyPartnerEndpoints()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/docs/partner/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains("/api/v1/partner/credentials", json);
        Assert.DoesNotContain("/api/v1/users", json);
        Assert.Contains("X-Api-Key", json);
    }

    [Fact]
    public async Task TemplatePreviewLink_OpensThePublicPreview_UntilItExpires()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, new[] { PartnerScope.TemplatesRead }, autoIssue: false);

        var response = await PartnerPost(t.Client, $"/api/v1/partner/templates/{t.TemplateId}/preview-link", t.Slug, key, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = (await response.Content.ReadFromJsonAsync<PartnerTemplatePreviewLinkResponse>())!;
        Assert.Contains("/template-preview/", link.Url);
        Assert.InRange(link.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        // Anonymous: no key, no tenant header — the link is the only credential.
        var token = link.Url[(link.Url.LastIndexOf('/') + 1)..];
        var anonymous = _factory.CreateClient();
        var page = await anonymous.GetAsync($"/api/v1/template-preview/{token}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        var preview = (await page.Content.ReadFromJsonAsync<TemplatePreviewResponse>())!;
        Assert.Equal("Course Certificate", preview.TemplateName);
        Assert.Equal(FieldSchemaJson, preview.FieldSchemaJson);
        Assert.StartsWith("Tenant ", preview.InstitutionName);
        Assert.False(string.IsNullOrEmpty(preview.LayoutJson));

        // Tampered: another template id under the same signature.
        var parts = token.Split('.');
        var forged = $"{parts[0]}.{Guid.NewGuid():N}.{parts[2]}.{parts[3]}";
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/template-preview/{forged}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/template-preview/not-a-token")).StatusCode);

        // Expired.
        using (var scope = _factory.Services.CreateScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<Certyfi.Api.Auth.TemplatePreviewLink>();
            var (old, _) = links.Issue(t.Slug, t.TemplateId, DateTimeOffset.UtcNow.AddHours(-1));
            Assert.Equal(HttpStatusCode.Gone, (await anonymous.GetAsync($"/api/v1/template-preview/{old}")).StatusCode);
        }

        // A template deactivated after the link was made stops showing.
        await StaffPost(t.Client, $"/api/v1/certificate-templates/{t.TemplateId}/deactivate", t.Slug, t.TokenAdmin, null);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/template-preview/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PartnerPost(t.Client, $"/api/v1/partner/templates/{t.TemplateId}/preview-link", t.Slug, key, new { })).StatusCode);
    }

    [Fact]
    public async Task TemplatePreviewLink_NeedsTemplatesRead_AndOnlyTheKeysOwnInstitution()
    {
        var t = await SetUpTenant();
        var writeOnly = await CreateKey(t, new[] { PartnerScope.CredentialsWrite }, autoIssue: false);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PartnerPost(t.Client, $"/api/v1/partner/templates/{t.TemplateId}/preview-link", t.Slug, writeOnly, new { })).StatusCode);

        var other = await SetUpTenant();
        var otherKey = await CreateKey(other, new[] { PartnerScope.TemplatesRead }, autoIssue: false);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PartnerPost(other.Client, $"/api/v1/partner/templates/{t.TemplateId}/preview-link", other.Slug, otherKey, new { })).StatusCode);
    }

    // ---------- helpers ----------

    private record TenantSetup(HttpClient Client, string Slug, string TokenAdmin, string TokenApprover, Guid TemplateId);

    private async Task<TenantSetup> SetUpTenant()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        const string approverPassword = "Approver123!";
        string slug;

        using (var scope = _factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var tenant = await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", adminPassword);
            slug = tenant.Slug;
        }

        var client = _factory.CreateClient();
        var adminLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"admin@{suffix}.test", adminPassword));
        var tokenAdmin = (await adminLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        await StaffPost(client, "/api/v1/users", slug, tokenAdmin,
            new CreateUserRequest($"approver@{suffix}.test", "Test Approver", approverPassword, SystemRole.Approver));
        var approverLogin = await PostJson(client, "/api/v1/auth/login", slug, new TenantLoginRequest($"approver@{suffix}.test", approverPassword));
        var tokenApprover = (await approverLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var template = await StaffPost(client, "/api/v1/certificate-templates", slug, tokenAdmin,
            new CreateCertificateTemplateRequest("Course Certificate", null, "Certificate", FieldSchemaJson));
        var templateId = (await template.Content.ReadFromJsonAsync<CertificateTemplateResponse>())!.Id;

        return new TenantSetup(client, slug, tokenAdmin, tokenApprover, templateId);
    }

    private static async Task<string> CreateKey(TenantSetup t, IReadOnlyList<string> scopes, bool autoIssue)
    {
        var response = await StaffPost(t.Client, "/api/v1/api-clients", t.Slug, t.TokenAdmin,
            new CreateApiClientRequest($"Partner {Guid.NewGuid():N}"[..20], scopes, autoIssue));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CreateApiClientResponse>())!;
        Assert.StartsWith("cfy_", body.ApiKey);
        return body.ApiKey;
    }

    private static PartnerIssueCredentialRequest IssueRequest(Guid templateId, string? externalReference) =>
        new(templateId, "Course Completion", "Jane Doe", "jane@example.com",
            JsonSerializer.SerializeToElement(new { rollNumber = "R-001" }), externalReference);

    private static Task<HttpResponseMessage> PartnerPost(HttpClient client, string url, string slug, string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Add("X-Api-Key", apiKey);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PartnerGetRaw(HttpClient client, string url, string slug, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Add("X-Api-Key", apiKey);
        return client.SendAsync(request);
    }

    private static async Task<T> PartnerGet<T>(HttpClient client, string url, string slug, string apiKey)
    {
        var response = await PartnerGetRaw(client, url, slug, apiKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string url, string slug, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", slug);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> StaffPost(HttpClient client, string url, string slug, string token, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<T> StaffGet<T>(HttpClient client, string url, string slug, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
