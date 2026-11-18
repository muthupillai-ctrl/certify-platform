using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Proves the local-disk upload path end to end: an authenticated
// InstitutionAdmin can upload an image and get back a URL that's actually
// servable WITHOUT any tenant/auth context (a plain <img src> can't send
// X-Tenant-Slug — see Program.cs's UseStaticFiles placement), a Registrar
// cannot upload, and non-image content types are rejected.
public class UploadsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public UploadsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UploadBackgroundImage_InstitutionAdmin_Succeeds_AndFileIsPubliclyServedBack()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();

        var response = await PostImage(client, slug, tokenAdmin, "image/png", new byte[] { 1, 2, 3, 4 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>();
        Assert.Contains("/uploads/", body!.Url);

        // No X-Tenant-Slug, no Authorization — proves a plain <img src>
        // request (which can't carry either) can actually fetch it back.
        var fetchResponse = await client.GetAsync(body.Url);
        Assert.Equal(HttpStatusCode.OK, fetchResponse.StatusCode);
    }

    [Fact]
    public async Task UploadBackgroundImage_RejectsNonImageContentType()
    {
        var (client, slug, _, tokenAdmin) = await SetUpTenant();
        var response = await PostImage(client, slug, tokenAdmin, "text/plain", new byte[] { 1, 2, 3, 4 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadBackgroundImage_Registrar_Forbidden()
    {
        var (client, slug, tokenReg, _) = await SetUpTenant();
        var response = await PostImage(client, slug, tokenReg, "image/png", new byte[] { 1, 2, 3, 4 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostImage(HttpClient client, string slug, string token, string contentType, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", "background.png");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads/background-image") { Content = content };
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

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

        var adminLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest($"admin@{suffix}.test", adminPassword)) };
        adminLoginRequest.Headers.Add("X-Tenant-Slug", slug);
        var adminLogin = await client.SendAsync(adminLoginRequest);
        var tokenAdmin = (await adminLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var createUserRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users") { Content = JsonContent.Create(new CreateUserRequest($"registrar@{suffix}.test", "Test Registrar", regPassword, "Registrar")) };
        createUserRequest.Headers.Add("X-Tenant-Slug", slug);
        createUserRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);
        await client.SendAsync(createUserRequest);

        var regLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest($"registrar@{suffix}.test", regPassword)) };
        regLoginRequest.Headers.Add("X-Tenant-Slug", slug);
        var regLogin = await client.SendAsync(regLoginRequest);
        var tokenReg = (await regLogin.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        return (client, slug, tokenReg, tokenAdmin);
    }
}
