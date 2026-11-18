using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Certyfi.Infrastructure.Services;

// Sends queued WebhookDeliveries ("credential.updated") to each partner's
// webhook, institution by institution, following the partner contract
// (docs/partner-api.md, "Webhooks"):
//   - signed: X-Certyfi-Timestamp (Unix seconds) and X-Certyfi-Signature
//     "v1=" + hex HMAC-SHA256(secret, "{timestamp}.{body}"), a fresh
//     timestamp on every attempt;
//   - 2xx delivered; 400/401/other 4xx stopped (not retried, shown to the
//     institution); 408/429/5xx/timeouts retried after 1 min, 5 min, 30 min,
//     2 h, 6 h and 24 h, then Failed.
// Also queues an "Expired" call once for each certificate that has expired
// (Expired is computed, so no status change marks it).
public class WebhookDispatcher
{
    public const string HttpClientName = "webhooks";
    private const int BatchSize = 50;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(24),
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SecretProtector _secrets;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, SecretProtector secrets, ILogger<WebhookDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        List<Tenant> tenants;
        using (var scope = _scopeFactory.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended).ToListAsync(ct);
        }
        foreach (var tenant in tenants)
        {
            try
            {
                await RunForTenantAsync(tenant, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Webhook dispatch failed for tenant {Slug}.", tenant.Slug);
            }
        }
    }

    // A "ping" to check the URL and secret (the API keys page's "Send test").
    public async Task<(int? StatusCode, string? Error)> PingAsync(ApiClient client, CancellationToken ct)
    {
        if (client.WebhookUrl is null || client.WebhookSecretEncrypted is null) return (null, "No webhook is set up for this key.");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { id = $"evt_ping_{Guid.NewGuid():N}", type = "ping", createdAt = DateTimeOffset.UtcNow });
        return await PostAsync(client.WebhookUrl, _secrets.Unprotect(client.WebhookSecretEncrypted), body, ct);
    }

    private async Task RunForTenantAsync(Tenant tenant, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug, tenant.SchemaName);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var now = DateTimeOffset.UtcNow;

        await QueueExpiriesAsync(db, scope.ServiceProvider.GetRequiredService<WebhookQueue>(), now, ct);

        await db.WebhookDeliveries
            .Where(d => d.State == WebhookDeliveryState.Sending && d.LastAttemptAt < now - StuckAfter)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.State, WebhookDeliveryState.Pending), ct);

        var due = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.State == WebhookDeliveryState.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Select(d => d.Id).Take(BatchSize).ToListAsync(ct);
        foreach (var id in due)
        {
            var claimed = await db.WebhookDeliveries
                .Where(d => d.Id == id && d.State == WebhookDeliveryState.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.State, WebhookDeliveryState.Sending).SetProperty(d => d.LastAttemptAt, DateTimeOffset.UtcNow), ct);
            if (claimed == 0) continue;

            var delivery = await db.WebhookDeliveries.FirstAsync(d => d.Id == id, ct);
            await SendOneAsync(db, delivery, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private async Task QueueExpiriesAsync(TenantDbContext db, WebhookQueue queue, DateTimeOffset now, CancellationToken ct)
    {
        var serviceUsers = await db.ApiClients.AsNoTracking()
            .Where(a => a.WebhookUrl != null && a.RevokedAt == null).Select(a => a.ServiceUserId).ToListAsync(ct);
        if (serviceUsers.Count == 0) return;
        var expired = await db.Credentials
            .Where(c => c.Status == CredentialStatus.Active && c.ExpiresOn != null && c.ExpiresOn <= now
                && c.ExpiryEventQueuedAt == null && serviceUsers.Contains(c.RequestedByUserId))
            .Take(BatchSize).ToListAsync(ct);
        if (expired.Count == 0) return;
        foreach (var credential in expired)
        {
            credential.ExpiryEventQueuedAt = now;
            await queue.QueueAsync(credential, ct, statusOverride: "Expired");
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task SendOneAsync(TenantDbContext db, WebhookDelivery delivery, CancellationToken ct)
    {
        delivery.Attempts++;
        delivery.LastAttemptAt = DateTimeOffset.UtcNow;
        var client = await db.ApiClients.AsNoTracking().FirstOrDefaultAsync(a => a.Id == delivery.ApiClientId, ct);
        if (client?.WebhookUrl is null || client.WebhookSecretEncrypted is null || client.RevokedAt is not null)
        {
            Stop(delivery, null, "The webhook was removed (or the API key revoked) before this was sent.");
            return;
        }

        var externalReference = await db.Credentials.AsNoTracking()
            .Where(c => c.Id == delivery.CredentialId).Select(c => c.ExternalReference).FirstOrDefaultAsync(ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = $"evt_{delivery.Id:N}",
            type = "credential.updated",
            createdAt = delivery.CreatedAt,
            data = new { credentialId = delivery.CredentialId, externalReference, status = delivery.CredentialStatus },
        });

        var (status, error) = await PostAsync(client.WebhookUrl, _secrets.Unprotect(client.WebhookSecretEncrypted), body, ct);
        delivery.LastStatusCode = status;
        if (status is >= 200 and < 300)
        {
            delivery.State = WebhookDeliveryState.Delivered;
            delivery.DeliveredAt = DateTimeOffset.UtcNow;
            delivery.LastError = null;
        }
        else if (status == 401)
        {
            Stop(delivery, status, "The partner refused the signature. Check the webhook URL and secret.");
        }
        else if (status is >= 400 and < 500 and not 408 and not 429)
        {
            Stop(delivery, status, $"The partner refused the call ({status}).");
        }
        else
        {
            delivery.LastError = error ?? $"The partner answered {status}.";
            if (delivery.Attempts > Backoff.Length)
            {
                delivery.State = WebhookDeliveryState.Failed;
                _logger.LogWarning("Webhook {Id} failed after {Attempts} attempts: {Error}", delivery.Id, delivery.Attempts, delivery.LastError);
            }
            else
            {
                delivery.State = WebhookDeliveryState.Pending;
                delivery.NextAttemptAt = DateTimeOffset.UtcNow + Backoff[delivery.Attempts - 1];
            }
        }
    }

    private static void Stop(WebhookDelivery delivery, int? status, string message)
    {
        delivery.State = WebhookDeliveryState.Stopped;
        delivery.LastStatusCode = status;
        delivery.LastError = message;
    }

    // Status code, or null with an error for timeouts and connection failures.
    private async Task<(int? StatusCode, string? Error)> PostAsync(string url, string secret, byte[] body, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Certyfi-Timestamp", timestamp);
        request.Headers.Add("X-Certyfi-Signature", Sign(secret, timestamp, body));
        request.Headers.UserAgent.ParseAdd("Certyfi-Webhooks/1");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _httpFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            return ((int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "No answer within 10 seconds.");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.InnerException is WebhookAddressBlockedException blocked ? blocked.Message : $"Couldn't connect: {ex.Message}");
        }
    }

    // "v1=" + lowercase hex HMAC-SHA256(UTF-8 secret, "{timestamp}." + body).
    public static string Sign(string secret, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message, prefix.Length);
        return "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message)).ToLowerInvariant();
    }

    // The webhook HTTP handler: no redirects, and (unless allowed, as in
    // development) no connections to loopback, private, link-local or other
    // internal addresses, checked on the address actually connected to, so a
    // partner URL can't be used to reach Certyfi's own network.
    public static SocketsHttpHandler CreateHandler(bool allowPrivateAddresses) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => allowPrivateAddresses || IsPublic(a)).ToArray();
            if (allowed.Length == 0) throw new WebhookAddressBlockedException(context.DnsEndPoint.Host);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (address.GetAddressBytes()[0] & 0xFE) == 0xFC || address.Equals(IPAddress.IPv6None));
    }
}

public class WebhookAddressBlockedException : Exception
{
    public WebhookAddressBlockedException(string host)
        : base($"{host} resolves only to private or internal addresses, which webhooks can't call.") { }
}

// Runs WebhookDispatcher every Webhooks:DispatchIntervalSeconds (default 10;
// 0 turns it off, as in the integration tests, which run it explicitly).
public class WebhookBackgroundService : BackgroundService
{
    private readonly WebhookDispatcher _dispatcher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebhookBackgroundService> _logger;

    public WebhookBackgroundService(WebhookDispatcher dispatcher, IConfiguration configuration, ILogger<WebhookBackgroundService> logger)
    {
        _dispatcher = dispatcher;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = _configuration.GetValue("Webhooks:DispatchIntervalSeconds", 10);
        if (seconds <= 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _dispatcher.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Webhook dispatch run failed.");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
