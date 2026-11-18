using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Platform admins' own profile (name, email with confirmation, password)
// and the Platform users page (add, reset, deactivate).
public class PlatformAccountTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Password = "Original-Pass-2026";
    private readonly CustomWebApplicationFactory _factory;

    public PlatformAccountTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Name_AndPassword_ChangePasswordEndsOtherSessions()
    {
        var (email, client) = await NewAdmin();
        var first = await Login(client, email, Password);
        var second = await Login(client, email, Password);

        var renamed = await Json<PlatformAccountResponse>(await Send(client, HttpMethod.Put, "/api/v1/platform/account/name", first, new UpdatePlatformNameRequest("Muthu R")));
        Assert.Equal("Muthu R", renamed.FullName);
        Assert.Contains(renamed.Changes, c => c.Field == "Name" && c.NewValue == "Muthu R");

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-password", first, new PlatformChangePasswordRequest("wrong-password-x", "A-New-Pass-2026!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-password", first, new PlatformChangePasswordRequest(Password, "Short-1x"))).StatusCode);
        var fresh = (await Json<LoginResponse>(await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-password", first, new PlatformChangePasswordRequest(Password, "A-New-Pass-2026!")))).AccessToken;

        // The other session (and the old token) end; the fresh one works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/platform/account", second)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/platform/account", first)).StatusCode);
        var me = await Json<PlatformAccountResponse>(await Send(client, HttpMethod.Get, "/api/v1/platform/account", fresh));
        Assert.NotNull(me.PasswordChangedAt);
        Assert.Contains(me.Changes, c => c.Field == "Password" && c.OldValue == null && c.NewValue == null);
        Assert.StartsWith("Your Certyfi platform password was changed", (await LastEmailTo(email)).Subject);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(email, Password))).StatusCode);
        await Login(client, email, "A-New-Pass-2026!");
    }

    [Fact]
    public async Task EmailChange_NeedsTheConfirmationLink_AndTellsTheOldAddress()
    {
        var (email, client) = await NewAdmin();
        var (otherEmail, _) = await NewAdmin();
        var token = await Login(client, email, Password);
        var newEmail = $"new-{Guid.NewGuid():N}@platform.test";

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-email", token, new PlatformChangeEmailRequest(newEmail, "wrong-password-x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-email", token, new PlatformChangeEmailRequest(otherEmail, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-email", token, new PlatformChangeEmailRequest(newEmail, Password))).StatusCode);

        // Nothing changes until the link is used.
        Assert.Equal(email, (await Json<PlatformAccountResponse>(await Send(client, HttpMethod.Get, "/api/v1/platform/account", token))).Email);
        var confirmation = await LastEmailTo(newEmail);
        var link = Regex.Match(confirmation.Text, @"https://certyfi\.test/platform/confirm-email/(\S+)").Groups[1].Value;
        Assert.NotEmpty(link);

        var anonymous = _factory.CreateClient();
        var confirmed = await Json<PlatformConfirmEmailResponse>(await anonymous.PostAsJsonAsync("/api/v1/platform/account/confirm-email", new PlatformConfirmEmailRequest(link)));
        Assert.Equal(newEmail, confirmed.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/v1/platform/account/confirm-email", new PlatformConfirmEmailRequest(link))).StatusCode);
        Assert.Contains("sign-in email was changed", (await LastEmailTo(email)).Subject);
        await Login(client, newEmail, Password);

        // Expired links are refused.
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<PlatformDbContext>().PlatformAdmins.Single(a => a.Email == newEmail);
        var old = scope.ServiceProvider.GetRequiredService<PlatformEmailChangeLink>().Issue(admin.Id, newEmail, $"x-{Guid.NewGuid():N}@platform.test", DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Equal(HttpStatusCode.Gone, (await anonymous.PostAsJsonAsync("/api/v1/platform/account/confirm-email", new PlatformConfirmEmailRequest(old))).StatusCode);
    }

    [Fact]
    public async Task PlatformUsers_AddResetDeactivate()
    {
        var (email, client) = await NewAdmin();
        var token = await Login(client, email, Password);
        var colleagueEmail = $"colleague-{Guid.NewGuid():N}@platform.test";

        var created = await Json<PlatformAdminWithPasswordResponse>(await Send(client, HttpMethod.Post, "/api/v1/platform/admins", token, new CreatePlatformAdminRequest(colleagueEmail, "Ada Colleague")));
        Assert.True(created.Admin.MustChangePassword);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, "/api/v1/platform/admins", token, new CreatePlatformAdminRequest(colleagueEmail, "Again"))).StatusCode);

        // First sign-in with the temporary password: only the profile and changing the password work.
        var login = await Json<LoginResponse>(await client.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(colleagueEmail, created.TemporaryPassword)));
        Assert.True(login.PasswordChangeRequired);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Get, "/api/v1/platform/tenants", login.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/platform/account", login.AccessToken)).StatusCode);
        var colleague = (await Json<LoginResponse>(await Send(client, HttpMethod.Post, "/api/v1/platform/account/change-password", login.AccessToken,
            new PlatformChangePasswordRequest(created.TemporaryPassword, "Colleague-Pass-2026!")))).AccessToken;
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/platform/tenants", colleague)).StatusCode);

        // You can't deactivate or reset yourself.
        var meId = (await Json<PlatformAccountResponse>(await Send(client, HttpMethod.Get, "/api/v1/platform/account", token))).Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, $"/api/v1/platform/admins/{meId}/deactivate", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, $"/api/v1/platform/admins/{meId}/reset-password", token)).StatusCode);

        // Reset ends their session; deactivate stops sign-in.
        var reset = await Json<PlatformAdminWithPasswordResponse>(await Send(client, HttpMethod.Post, $"/api/v1/platform/admins/{created.Admin.Id}/reset-password", token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/platform/tenants", colleague)).StatusCode);
        var afterReset = await Json<LoginResponse>(await client.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(colleagueEmail, reset.TemporaryPassword)));
        Assert.True(afterReset.PasswordChangeRequired);

        Assert.False((await Json<PlatformAdminResponse>(await Send(client, HttpMethod.Post, $"/api/v1/platform/admins/{created.Admin.Id}/deactivate", token))).IsActive);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/platform/account", afterReset.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(colleagueEmail, reset.TemporaryPassword))).StatusCode);

        var history = await Json<List<PlatformAccountChangeResponse>>(await Send(client, HttpMethod.Get, $"/api/v1/platform/admins/{created.Admin.Id}/history", token));
        Assert.Equal(["Deactivated", "Password reset", "Password", "Created"], history.Select(h => h.Field).ToArray());
        var list = await Json<List<PlatformAdminResponse>>(await Send(client, HttpMethod.Get, "/api/v1/platform/admins", token));
        Assert.True(list.Single(a => a.Email == email).IsYou);
    }

    [Fact]
    public async Task ForgotPassword_EmailsAOneTimeLink_ThatEndsOtherSessions()
    {
        var (email, client) = await NewAdmin();
        var oldSession = await Login(client, email, Password);
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/v1/platform/auth/forgot-password", new PlatformForgotPasswordRequest(email.ToUpperInvariant()))).StatusCode);
        var resetEmail = await LastEmailTo(email);
        Assert.StartsWith("Reset your Certyfi platform password", resetEmail.Subject);
        var token = Regex.Match(resetEmail.Text, @"https://certyfi\.test/platform/reset-password/(\S+)").Groups[1].Value;
        Assert.NotEmpty(token);

        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/platform/auth/reset-password", new PlatformResetPasswordRequest(token, "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/platform/auth/reset-password", new PlatformResetPasswordRequest(token, "Reset-By-Email-2026!"))).StatusCode);
        // Once only; the old session and password are gone; the new one works.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/v1/platform/auth/reset-password", new PlatformResetPasswordRequest(token, "Another-Pass-2026!"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/platform/account", oldSession)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(email, Password))).StatusCode);
        var fresh = await Login(client, email, "Reset-By-Email-2026!");
        Assert.Contains((await Json<PlatformAccountResponse>(await Send(client, HttpMethod.Get, "/api/v1/platform/account", fresh))).Changes, c => c.Field == "Password reset by email");
        Assert.StartsWith("Your Certyfi platform password was changed", (await LastEmailTo(email)).Subject);

        // Unknown emails get the same answer and no email.
        var unknown = $"nobody-{Guid.NewGuid():N}@platform.test";
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/v1/platform/auth/forgot-password", new PlatformForgotPasswordRequest(unknown))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().SendDueAsync(CancellationToken.None);
        }
        Assert.DoesNotContain(_factory.Services.GetRequiredService<MemoryEmailTransport>().Sent, e => e.To == unknown);
    }

    // ---------- helpers ----------

    private async Task<(string Email, HttpClient Client)> NewAdmin()
    {
        var email = $"admin-{Guid.NewGuid():N}@platform.test";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.PlatformAdmins.Add(new PlatformAdmin { Email = email, FullName = "Test Platform Admin", PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password) });
        await db.SaveChangesAsync();
        return (email, _factory.CreateClient());
    }

    private static async Task<string> Login(HttpClient client, string email, string password) =>
        (await Json<LoginResponse>(await client.PostAsJsonAsync("/api/v1/platform/auth/login", new PlatformLoginRequest(email, password)))).AccessToken;

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null || method == HttpMethod.Post) request.Content = JsonContent.Create(body ?? new { });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<OutgoingEmail> LastEmailTo(string address)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().SendDueAsync(CancellationToken.None);
        }
        return _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent.Last(e => e.To == address);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
