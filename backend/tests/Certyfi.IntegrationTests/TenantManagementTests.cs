using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Institution user management (Institution Admin and SuperAdmin), admin
// password resets with a forced change at next sign-in, the institution
// profile (and which parts are public), and suspension.
public class TenantManagementTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string AdminPassword = "TenantAdmin123!";
    private const string StaffPassword = "Password123!";

    private readonly CustomWebApplicationFactory _factory;

    public TenantManagementTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_ViewsAndEditsUser_IncludingEmailAndRoles()
    {
        var t = await SetUp();

        var detail = await Json<TenantUserDetail>(await Send(t, HttpMethod.Get, $"/api/v1/users/{t.RegistrarId}", t.AdminToken));
        Assert.Equal(new[] { "Registrar" }, detail.Roles);
        Assert.NotNull(detail.LastLoginAt);

        var updated = await Send(t, HttpMethod.Put, $"/api/v1/users/{t.RegistrarId}", t.AdminToken,
            new UpdateUserRequest("Renamed Registrar", $"renamed@{t.Suffix}.test", new[] { "Registrar", "Approver" }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await Json<TenantUserDetail>(updated);
        Assert.Equal("Renamed Registrar", body.FullName);
        Assert.Equal(new[] { "Approver", "Registrar" }, body.Roles);

        Assert.Equal(HttpStatusCode.OK, (await Login(t, $"renamed@{t.Suffix}.test", StaffPassword)).StatusCode);
    }

    [Fact]
    public async Task Edit_RejectsDuplicateEmail_AndUnknownRole()
    {
        var t = await SetUp();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(t, HttpMethod.Put, $"/api/v1/users/{t.RegistrarId}", t.AdminToken,
            new UpdateUserRequest("X", $"admin@{t.Suffix}.test", new[] { "Registrar" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Put, $"/api/v1/users/{t.RegistrarId}", t.AdminToken,
            new UpdateUserRequest("X", $"x@{t.Suffix}.test", new[] { "SuperAdmin" }))).StatusCode);
    }

    [Fact]
    public async Task InstitutionIsNeverLeftWithoutAnActiveAdmin()
    {
        var t = await SetUp();

        // An admin can't lock themselves out...
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Post, $"/api/v1/users/{t.AdminId}/deactivate", t.AdminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Put, $"/api/v1/users/{t.AdminId}", t.AdminToken,
            new UpdateUserRequest("Admin", $"admin@{t.Suffix}.test", new[] { "Registrar" }))).StatusCode);

        // ...and not even a SuperAdmin can remove the only active admin.
        Assert.Equal(HttpStatusCode.Conflict,
            (await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/users/{t.AdminId}/deactivate", t.PlatformToken)).StatusCode);
    }

    [Fact]
    public async Task DeactivatedUser_CannotSignIn_UntilReactivated()
    {
        var t = await SetUp();
        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Post, $"/api/v1/users/{t.RegistrarId}/deactivate", t.AdminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(t, $"reg@{t.Suffix}.test", StaffPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Post, $"/api/v1/users/{t.RegistrarId}/reactivate", t.AdminToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(t, $"reg@{t.Suffix}.test", StaffPassword)).StatusCode);
    }

    [Fact]
    public async Task PasswordReset_ForcesNewPasswordBeforeAnythingElse()
    {
        var t = await SetUp();

        var reset = await Json<ResetPasswordResponse>(await Send(t, HttpMethod.Post, $"/api/v1/users/{t.RegistrarId}/reset-password", t.AdminToken));
        Assert.True(reset.User.MustChangePassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(t, $"reg@{t.Suffix}.test", StaffPassword)).StatusCode);

        var tempLogin = await Json<LoginResponse>(await Login(t, $"reg@{t.Suffix}.test", reset.TemporaryPassword));
        Assert.True(tempLogin.PasswordChangeRequired);

        var blocked = await Send(t, HttpMethod.Get, "/api/v1/credentials", tempLogin.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("password_change_required", await blocked.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, "/api/v1/auth/change-password", tempLogin.AccessToken,
            new ChangePasswordRequest(reset.TemporaryPassword, "short"))).StatusCode);
        var changed = await Send(t, HttpMethod.Post, "/api/v1/auth/change-password", tempLogin.AccessToken,
            new ChangePasswordRequest(reset.TemporaryPassword, "A-brand-new-passphrase"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var fresh = await Json<LoginResponse>(changed);
        Assert.False(fresh.PasswordChangeRequired);

        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Get, "/api/v1/credentials", fresh.AccessToken)).StatusCode);
        var detail = await Json<TenantUserDetail>(await Send(t, HttpMethod.Get, $"/api/v1/users/{t.RegistrarId}", t.AdminToken));
        Assert.False(detail.MustChangePassword);
    }

    [Fact]
    public async Task ChangeOwnPassword_RequiresCurrentPassword()
    {
        var t = await SetUp();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, "/api/v1/auth/change-password", t.RegistrarToken,
            new ChangePasswordRequest("wrong-password", "Another-good-passphrase"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Post, "/api/v1/auth/change-password", t.RegistrarToken,
            new ChangePasswordRequest(StaffPassword, "Another-good-passphrase"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(t, $"reg@{t.Suffix}.test", StaffPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(t, $"reg@{t.Suffix}.test", "Another-good-passphrase")).StatusCode);
    }

    [Fact]
    public async Task CreateUser_EnforcesPasswordPolicy()
    {
        var t = await SetUp();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, "/api/v1/users", t.AdminToken,
            new CreateUserRequest($"weak@{t.Suffix}.test", "Weak", "short1!", "Registrar"))).StatusCode);
    }

    [Fact]
    public async Task Profile_IsEditable_AndOnlyPublicPartsReachVerification()
    {
        var t = await SetUp();
        var bad = await Send(t, HttpMethod.Put, "/api/v1/tenant-settings/profile", t.AdminToken, Profile(t, website: "not a url"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await Send(t, HttpMethod.Put, "/api/v1/tenant-settings/profile", t.AdminToken, Profile(t, website: "https://acme.example"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var profile = await Json<TenantProfileResponse>(await Send(t, HttpMethod.Get, "/api/v1/tenant-settings/profile", t.AdminToken));
        Assert.Equal("contact@acme.example", profile.ContactEmail);

        // Registrars can't edit the profile.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Put, "/api/v1/tenant-settings/profile", t.RegistrarToken, Profile(t, "https://x.example"))).StatusCode);

        var credentialId = await IssueCertificate(t);
        var verifyJson = await t.Client.GetStringAsync($"/api/v1/verify/{t.Slug}/{credentialId}");
        var institution = JsonDocument.Parse(verifyJson).RootElement.GetProperty("institutionProfile");
        Assert.Equal("ACC-42", institution.GetProperty("registrationNumber").GetString());
        Assert.Equal("https://acme.example", institution.GetProperty("websiteUrl").GetString());
        Assert.DoesNotContain("contact@acme.example", verifyJson);
        Assert.DoesNotContain("+1 555 0100", verifyJson);
        Assert.DoesNotContain("1 Main Street", verifyJson);

        var issuer = JsonDocument.Parse(await t.Client.GetStringAsync($"/api/v1/verify/{t.Slug}/issuer")).RootElement;
        Assert.Equal("https://acme.example", issuer.GetProperty("url").GetString());
    }

    [Fact]
    public async Task SuperAdmin_SeesInstitution_EditsProfile_AndSuspends_WhileCertificatesStayVerifiable()
    {
        var t = await SetUp();
        var credentialId = await IssueCertificate(t);

        var detail = await Json<TenantDetailResponse>(await Send(t, HttpMethod.Get, $"/api/v1/platform/tenants/{t.TenantId}", t.PlatformToken));
        Assert.Equal(3, detail.UserCount);
        Assert.Equal(1, detail.ActiveCertificateCount);

        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Put, $"/api/v1/platform/tenants/{t.TenantId}/profile", t.PlatformToken,
            Profile(t, "https://acme.example") with { Name = "Acme Renamed" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/suspend", t.PlatformToken, new SuspendTenantRequest(""))).StatusCode);
        var suspended = await Json<TenantDetailResponse>(await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/suspend", t.PlatformToken,
            new SuspendTenantRequest("Unpaid invoice")));
        Assert.Equal("Suspended", suspended.Status);

        var login = await Login(t, $"admin@{t.Suffix}.test", AdminPassword);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("tenant_suspended", await login.Content.ReadAsStringAsync());

        var verify = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{credentialId}");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal("Acme Renamed", (await Json<VerifyCredentialResponse>(verify)).InstitutionName);

        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Post, $"/api/v1/platform/tenants/{t.TenantId}/reactivate", t.PlatformToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(t, $"admin@{t.Suffix}.test", AdminPassword)).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_ManagesInstitutionUsers_AndTenantAdminCannotUsePlatformEndpoints()
    {
        var t = await SetUp();

        var users = await Json<List<TenantUserDetail>>(await Send(t, HttpMethod.Get, $"/api/v1/platform/tenants/{t.TenantId}/users", t.PlatformToken));
        Assert.Equal(3, users.Count);

        var reset = await Json<ResetPasswordResponse>(await Send(t, HttpMethod.Post,
            $"/api/v1/platform/tenants/{t.TenantId}/users/{t.AdminId}/reset-password", t.PlatformToken));
        var login = await Json<LoginResponse>(await Login(t, $"admin@{t.Suffix}.test", reset.TemporaryPassword));
        Assert.True(login.PasswordChangeRequired);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, HttpMethod.Get, $"/api/v1/platform/tenants/{t.TenantId}/users", t.RegistrarToken)).StatusCode);
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Suffix, string Slug, Guid TenantId, string PlatformToken,
        string AdminToken, Guid AdminId, string RegistrarToken, Guid RegistrarId, string ApproverToken);

    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Tenant tenant;
        string platformToken;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", AdminPassword);
            platformToken = TestPlatformAdmins.CreateWithToken(_factory.Services, $"tenant-mgmt-{suffix}@platform.test", "Tenant Management Test Admin");
        }
        var client = _factory.CreateClient();
        var partial = new Setup(client, suffix, tenant.Slug, tenant.Id, platformToken, "", Guid.Empty, "", Guid.Empty, "");

        var adminToken = (await Json<LoginResponse>(await Login(partial, $"admin@{suffix}.test", AdminPassword))).AccessToken;
        partial = partial with { AdminToken = adminToken };
        foreach (var (email, role) in new[] { ($"reg@{suffix}.test", "Registrar"), ($"app@{suffix}.test", "Approver") })
        {
            Assert.Equal(HttpStatusCode.Created, (await Send(partial, HttpMethod.Post, "/api/v1/users", adminToken,
                new CreateUserRequest(email, role, StaffPassword, role))).StatusCode);
        }
        var users = await Json<List<TenantUserDetail>>(await Send(partial, HttpMethod.Get, "/api/v1/users", adminToken));
        return partial with
        {
            AdminId = users.Single(u => u.Email == $"admin@{suffix}.test").Id,
            RegistrarId = users.Single(u => u.Email == $"reg@{suffix}.test").Id,
            RegistrarToken = (await Json<LoginResponse>(await Login(partial, $"reg@{suffix}.test", StaffPassword))).AccessToken,
            ApproverToken = (await Json<LoginResponse>(await Login(partial, $"app@{suffix}.test", StaffPassword))).AccessToken,
        };
    }

    private static TenantProfileInput Profile(Setup t, string website) => new(
        $"Tenant {t.Suffix}", "Acme University Trust", website, "ACC-42", "National Accreditation Board", "A test institution.",
        "1 Main Street", null, "Springfield", "State", "12345", "Country", "contact@acme.example", "+1 555 0100");

    private static async Task<Guid> IssueCertificate(Setup t)
    {
        var achievement = await Json<AchievementResponse>(await Send(t, HttpMethod.Post, "/api/v1/achievements", t.RegistrarToken,
            new CreateAchievementRequest("Course", null, null, null, null, null)));
        var draft = await Json<CredentialResponse>(await Send(t, HttpMethod.Post, "/api/v1/credentials", t.RegistrarToken,
            new CreateCredentialRequest(achievement.Id, "Jane Doe", "jane@example.com", null)));
        Assert.Equal(HttpStatusCode.OK, (await Send(t, HttpMethod.Post, $"/api/v1/credentials/{draft.Id}/approve", t.ApproverToken)).StatusCode);
        return draft.Id;
    }

    private static Task<HttpResponseMessage> Login(Setup t, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        return t.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Send(Setup t, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (!url.StartsWith("/api/v1/platform")) request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
