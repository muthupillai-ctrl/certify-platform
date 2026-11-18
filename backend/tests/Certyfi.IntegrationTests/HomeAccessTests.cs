using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Auth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// The private home page's gate (AccessController) and the operator's list of
// allowed mobile numbers (HomeAccessNumbersController).
public class HomeAccessTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HomeAccessTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AListedNumber_GetsAPass_InLocalOrInternationalFormat()
    {
        var (client, platform) = Start();
        var national = RandomNational();
        var added = await Json<HomeAccessNumberResponse>(await Send(client, HttpMethod.Post, "/api/v1/platform/home-access", platform,
            new AddHomeAccessNumberRequest($"+234 {national[..3]} {national[3..6]} {national[6..]}", "Dr. Bello, Lagos")));
        Assert.Equal("234" + national, added.Number);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/access")).StatusCode);
        var enter = await Enter(client, "0" + national); // local format, same last 10 digits
        Assert.Equal(HttpStatusCode.NoContent, enter.StatusCode);
        var cookie = Assert.Single(enter.Headers.GetValues("Set-Cookie"));
        Assert.Contains("certyfi_home=", cookie);
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("max-age=2592000", cookie.ToLowerInvariant()); // 30 days

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/access")).StatusCode);
        var list = await Json<List<HomeAccessNumberResponse>>(await Send(client, HttpMethod.Get, "/api/v1/platform/home-access", platform));
        Assert.NotNull(list.Single(n => n.Id == added.Id).LastUsedAt);
    }

    [Fact]
    public async Task UnlistedOrMalformedNumbers_AreRefused()
    {
        var (client, _) = Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await Enter(client, "9" + RandomNational())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enter(client, "12345")).StatusCode);            // too short
        Assert.Equal(HttpStatusCode.BadRequest, (await Enter(client, "12345678901234567")).StatusCode); // 17 digits
        Assert.Equal(HttpStatusCode.BadRequest, (await Enter(client, "0706abc1305")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/access")).StatusCode);
    }

    [Fact]
    public async Task RemovingANumber_EndsItsPass_AndForgedPassesAreRejected()
    {
        var (client, platform) = Start();
        var number = "234" + RandomNational();
        var added = await Json<HomeAccessNumberResponse>(await Send(client, HttpMethod.Post, "/api/v1/platform/home-access", platform,
            new AddHomeAccessNumberRequest(number, "Visitor")));
        Assert.Equal(HttpStatusCode.NoContent, (await Enter(client, number)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/access")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Delete, $"/api/v1/platform/home-access/{added.Id}", platform)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/access")).StatusCode);

        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/access");
        forged.Headers.Add("Cookie", $"certyfi_home={added.Id:N}.9999999999.0000");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task OnlyThePlatformOperatorManagesTheList()
    {
        var (client, platform) = Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/platform/home-access")).StatusCode);
        var number = "234" + RandomNational();
        await Json<HomeAccessNumberResponse>(await Send(client, HttpMethod.Post, "/api/v1/platform/home-access", platform, new AddHomeAccessNumberRequest(number, "A")));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, "/api/v1/platform/home-access", platform, new AddHomeAccessNumberRequest(number, "B"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, "/api/v1/platform/home-access", platform, new AddHomeAccessNumberRequest("123", "C"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, "/api/v1/platform/home-access", platform, new AddHomeAccessNumberRequest("234" + RandomNational(), " "))).StatusCode);
    }

    [Fact]
    public async Task ManyAttempts_AreSlowedDown()
    {
        var (client, _) = Start();
        var ip = $"198.51.100.{Random.Shared.Next(1, 250)}";
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/access") { Content = JsonContent.Create(new EnterHomeAccessRequest("9" + RandomNational())) };
            request.Headers.Add("CF-Connecting-IP", ip);
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    // ---------- helpers ----------

    private (HttpClient Client, string PlatformToken) Start()
    {
        using var scope = _factory.Services.CreateScope();
        var token = TestPlatformAdmins.CreateWithToken(_factory.Services, $"home-access-{Guid.NewGuid():N}@platform.test", "Home Access Test Admin");
        return (_factory.CreateClient(), token);
    }

    // A random 10-digit national number starting 7–9 (never collides across tests in practice).
    private static string RandomNational() => $"{Random.Shared.Next(7, 10)}{Random.Shared.NextInt64(0, 1_000_000_000):D9}";

    // Each attempt from its own made-up client IP, so the rate limit test
    // doesn't slow down the others.
    private static Task<HttpResponseMessage> Enter(HttpClient client, string mobile)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/access") { Content = JsonContent.Create(new EnterHomeAccessRequest(mobile)) };
        request.Headers.Add("CF-Connecting-IP", $"203.0.113.{Random.Shared.Next(1, 250)}-{Guid.NewGuid():N}");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
