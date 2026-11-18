using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Webhooks to a partner (the LMS contract: ahamsys-lms/docs/certyfi-webhooks.md),
// against a real local HTTP receiver that checks every signature.
public class WebhookTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string FieldSchemaJson = """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""";
    private const string Secret = "whsec_integration_test_secret";
    private readonly CustomWebApplicationFactory _factory;

    public WebhookTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Signature_MatchesThePartnersTestVector()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"credential.updated","data":{"credentialId":"9b1e0000-0000-0000-0000-000000000001"}}""");
        Assert.Equal("v1=07b3231847d2bee3d8ec09544d2b8db61bd4f04ae846da0e08715c58282549ba",
            WebhookDispatcher.Sign("whsec_test_secret", "1760000000", body));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void OnlyPublicAddresses_AreCalledInProduction(string address, bool isPublic) =>
        Assert.Equal(isPublic, WebhookDispatcher.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task StatusChanges_AreSentSigned_AndPingWorks()
    {
        using var receiver = new Receiver();
        var t = await SetUpTenant();
        var (keyId, key) = await CreateKey(t);
        await Json<ApiClientResponse>(await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest(receiver.Url, Secret)));

        var test = await Json<WebhookTestResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/api-clients/{keyId}/webhook/test"));
        Assert.True(test.Ok, test.Error);
        Assert.Equal("ping", receiver.Next().Type);

        // Created: no call (the partner has the response). Approved: one call.
        var issued = await Json<PartnerCredentialResponse>(await Partner(t, key, "/api/v1/partner/credentials", new PartnerIssueCredentialRequest(
            t.TemplateId, "Course", "Amaka Obi", "amaka@example.com", JsonSerializer.SerializeToElement(new { rollNumber = "1" }), "ref-1", "S-1")));
        await RunDispatcher();
        Assert.True(receiver.IsEmpty);

        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Id}/approve", token: t.TokenApprover));
        await RunDispatcher();
        var call = receiver.Next();
        Assert.Equal("credential.updated", call.Type);
        Assert.Equal(issued.Id, call.CredentialId);
        Assert.Equal("ref-1", call.ExternalReference);
        Assert.Equal("Active", call.Status);
        Assert.True(call.SignatureValid);
        Assert.InRange(call.TimestampSkew, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        // Suspend: another call. Delivered entries show in the log.
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Id}/suspend", body: new SuspendCredentialRequest("Check", issued.VerificationCode)));
        await RunDispatcher();
        Assert.Equal("Suspended", receiver.Next().Status);
        var log = await Json<List<WebhookDeliveryResponse>>(await Staff(t, HttpMethod.Get, $"/api/v1/api-clients/{keyId}/webhook/deliveries"));
        Assert.Equal(2, log.Count);
        Assert.All(log, d => Assert.Equal("Delivered", d.State));
    }

    [Fact]
    public async Task Refused_IsStoppedAndShown_Unavailable_IsRetriedLater()
    {
        using var receiver = new Receiver();
        var t = await SetUpTenant();
        var (keyId, key) = await CreateKey(t);
        await Json<ApiClientResponse>(await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest(receiver.Url, Secret)));
        var a = await Issue(t, key, "ref-a", "A-1", "ada@example.com");
        var b = await Issue(t, key, "ref-b", "B-1", "bola@example.com");

        receiver.Respond(HttpStatusCode.ServiceUnavailable);
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{a.Id}/approve", token: t.TokenApprover));
        await RunDispatcher();
        var retrying = Assert.Single(await Json<List<WebhookDeliveryResponse>>(await Staff(t, HttpMethod.Get, $"/api/v1/api-clients/{keyId}/webhook/deliveries")));
        Assert.Equal("Pending", retrying.State);
        Assert.Equal(503, retrying.LastStatusCode);
        Assert.True(retrying.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(50));

        receiver.Respond(HttpStatusCode.Unauthorized);
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{b.Id}/approve", token: t.TokenApprover));
        await RunDispatcher();
        var log = await Json<List<WebhookDeliveryResponse>>(await Staff(t, HttpMethod.Get, $"/api/v1/api-clients/{keyId}/webhook/deliveries"));
        var refused = log.Single(d => d.CredentialId == b.Id);
        Assert.Equal("Stopped", refused.State);
        Assert.Contains("signature", refused.LastError);
        var keys = await Json<List<ApiClientResponse>>(await Staff(t, HttpMethod.Get, "/api/v1/api-clients"));
        Assert.True(keys.Single(k => k.Id == keyId).WebhookFailing);
    }

    [Fact]
    public async Task SetUp_IsChecked_AndKeysWithoutAWebhookSendNothing()
    {
        var t = await SetUpTenant();
        var (keyId, key) = await CreateKey(t);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest("not a url", Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest("https://lms.example.com/hook", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest("https://lms.example.com/hook", Secret), t.TokenApprover)).StatusCode);

        var issued = await Issue(t, key, "ref-n", "N-1", "nobody@example.com");
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Id}/approve", token: t.TokenApprover));
        Assert.Empty(await Json<List<WebhookDeliveryResponse>>(await Staff(t, HttpMethod.Get, $"/api/v1/api-clients/{keyId}/webhook/deliveries")));

        // Secret is kept when only the URL changes; removing stops it.
        await Json<ApiClientResponse>(await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest("https://lms.example.com/a", Secret)));
        var moved = await Json<ApiClientResponse>(await Staff(t, HttpMethod.Put, $"/api/v1/api-clients/{keyId}/webhook", new SetWebhookRequest("https://lms.example.com/b", null)));
        Assert.Equal("https://lms.example.com/b", moved.WebhookUrl);
        Assert.Null((await Json<ApiClientResponse>(await Staff(t, HttpMethod.Delete, $"/api/v1/api-clients/{keyId}/webhook"))).WebhookUrl);
    }

    // ---------- helpers ----------

    private Task RunDispatcher() => _factory.Services.GetRequiredService<WebhookDispatcher>().RunAsync(CancellationToken.None);

    private record TenantSetup(HttpClient Client, string Slug, string TokenAdmin, string TokenApprover, Guid TemplateId);

    private async Task<TenantSetup> SetUpTenant()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string slug;
        using (var scope = _factory.Services.CreateScope())
        {
            slug = (await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!")).Slug;
        }
        var client = _factory.CreateClient();
        var t = new TenantSetup(client, slug, "", "", Guid.Empty);
        var admin = (await Json<LoginResponse>(await Staff(t, HttpMethod.Post, "/api/v1/auth/login", new TenantLoginRequest($"admin@{suffix}.test", "TenantAdmin123!"), ""))).AccessToken;
        t = t with { TokenAdmin = admin };
        await Staff(t, HttpMethod.Post, "/api/v1/users", new CreateUserRequest($"approver@{suffix}.test", "Approver", "Approver123!", SystemRole.Approver));
        var approver = (await Json<LoginResponse>(await Staff(t, HttpMethod.Post, "/api/v1/auth/login", new TenantLoginRequest($"approver@{suffix}.test", "Approver123!"), ""))).AccessToken;
        var template = await Json<CertificateTemplateResponse>(await Staff(t, HttpMethod.Post, "/api/v1/certificate-templates",
            new CreateCertificateTemplateRequest("Course Certificate", null, "Certificate", FieldSchemaJson)));
        return t with { TokenApprover = approver, TemplateId = template.Id };
    }

    private static async Task<(Guid Id, string Key)> CreateKey(TenantSetup t)
    {
        var created = await Json<CreateApiClientResponse>(await Staff(t, HttpMethod.Post, "/api/v1/api-clients",
            new CreateApiClientRequest($"LMS {Guid.NewGuid():N}"[..20], PartnerScope.All, false, false)));
        return (created.Client.Id, created.ApiKey);
    }

    private static async Task<PartnerCredentialResponse> Issue(TenantSetup t, string key, string reference, string studentId, string email) =>
        await Json<PartnerCredentialResponse>(await Partner(t, key, "/api/v1/partner/credentials", new PartnerIssueCredentialRequest(
            t.TemplateId, "Course", "Learner", email, JsonSerializer.SerializeToElement(new { rollNumber = "1" }), reference, studentId)));

    private static Task<HttpResponseMessage> Partner(TenantSetup t, string key, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Add("X-Api-Key", key);
        return t.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Staff(TenantSetup t, HttpMethod method, string url, object? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null || method == HttpMethod.Post) request.Content = JsonContent.Create(body ?? new { });
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        token ??= t.TokenAdmin;
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // A local webhook endpoint: records each call (checking its signature
    // with Secret) and answers with the configured status.
    private sealed class Receiver : IDisposable
    {
        public record Call(string Type, Guid CredentialId, string? ExternalReference, string? Status, bool SignatureValid, TimeSpan TimestampSkew);

        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<Call> _calls = new();
        private volatile int _status = 200;
        public string Url { get; }

        public Receiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://localhost:{port}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public void Respond(HttpStatusCode status) => _status = (int)status;
        public bool IsEmpty => _calls.IsEmpty;
        public Call Next() => _calls.TryDequeue(out var call) ? call : throw new Xunit.Sdk.XunitException("No webhook call received.");

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                var body = ms.ToArray();
                var timestamp = ctx.Request.Headers["X-Certyfi-Timestamp"] ?? "";
                var signature = ctx.Request.Headers["X-Certyfi-Signature"] ?? "";
                var valid = signature == WebhookDispatcher.Sign(Secret, timestamp, body);
                var skew = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp));
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var data = root.TryGetProperty("data", out var d) ? d : default;
                _calls.Enqueue(new Call(
                    root.GetProperty("type").GetString()!,
                    data.ValueKind == JsonValueKind.Object ? data.GetProperty("credentialId").GetGuid() : Guid.Empty,
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("externalReference", out var r) ? r.GetString() : null,
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("status", out var s) ? s.GetString() : null,
                    valid, skew));
                ctx.Response.StatusCode = _status;
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
