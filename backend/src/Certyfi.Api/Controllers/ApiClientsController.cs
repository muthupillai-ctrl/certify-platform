using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped. An InstitutionAdmin issues and revokes Partner API keys for
// their own institution. Keys can't be edited after creation — changing
// scopes or auto-issue means issuing a new key and revoking the old one, so
// what a key could do is always exactly what it was created with.
[ApiController]
[Route("api/v1/api-clients")]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class ApiClientsController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly WebhookDispatcher _webhooks;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    private readonly ControlRequestService _controlRequests;
    private readonly bool _autoIssueNeedsSecondAdmin;

    public ApiClientsController(TenantDbContext db, SecretProtector secrets, WebhookDispatcher webhooks, IHostEnvironment environment,
        IConfiguration configuration, ControlRequestService controlRequests)
    {
        _controlRequests = controlRequests;
        _autoIssueNeedsSecondAdmin = configuration.GetValue("Security:AutoIssueNeedsSecondAdmin", true);
        _configuration = configuration;
        _db = db;
        _secrets = secrets;
        _webhooks = webhooks;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ApiClientResponse>>> List(CancellationToken ct)
    {
        var clients = await _db.ApiClients.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ApiClientResponse(
                c.Id, c.Name, c.KeyPrefix, c.Scopes, c.AutoIssue,
                c.CreatedByUser.FullName, c.CreatedAt, c.LastUsedAt, c.RevokedAt,
                _db.Credentials.Count(cr => cr.RequestedByUserId == c.ServiceUserId), c.RequiresPayment, c.WebhookUrl,
                _db.WebhookDeliveries.Where(w => w.ApiClientId == c.Id && w.State != WebhookDeliveryState.Pending && w.State != WebhookDeliveryState.Sending)
                    .OrderByDescending(w => w.LastAttemptAt).Select(w => w.State == WebhookDeliveryState.Stopped || w.State == WebhookDeliveryState.Failed).FirstOrDefault(),
                _db.ControlRequests.Any(r => r.Kind == ControlRequestKind.EnableAutoIssue && r.TargetId == c.Id && r.Status == ControlRequestStatus.Pending)))
            .ToListAsync(ct);
        return Ok(clients);
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiClientResponse>> Create(CreateApiClientRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return BadRequest(new { error = "Name is required (max 200 characters)." });
        }
        var scopes = (request.Scopes ?? Array.Empty<string>()).Distinct().ToList();
        if (scopes.Count == 0)
        {
            return BadRequest(new { error = "At least one scope is required." });
        }
        var unknown = scopes.Where(s => !PartnerScope.All.Contains(s)).ToList();
        if (unknown.Count > 0)
        {
            return BadRequest(new { error = $"Unknown scope(s): {string.Join(", ", unknown)}." });
        }
        if (request.AutoIssue && !scopes.Contains(PartnerScope.CredentialsWrite))
        {
            return BadRequest(new { error = $"Auto-issue requires the '{PartnerScope.CredentialsWrite}' scope." });
        }
        if (request.RequiresPayment && !scopes.Contains(PartnerScope.CredentialsWrite))
        {
            return BadRequest(new { error = $"Students paying before issue requires the '{PartnerScope.CredentialsWrite}' scope." });
        }

        var developerRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == SystemRole.Developer, ct);
        if (developerRole is null)
        {
            return BadRequest(new { error = $"Role '{SystemRole.Developer}' is not seeded for this tenant." });
        }

        var (plaintextKey, keyPrefix, secretHash) = ApiKeyGenerator.Generate();

        // The service account's password is random and never stored in
        // clear; AuthController also rejects service accounts outright, so
        // this hash exists only to satisfy the column.
        var serviceUser = new ApplicationUser
        {
            Email = $"apikey-{keyPrefix}@service.certyfi.invalid",
            FullName = $"API: {name}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            IsActive = true,
            IsServiceAccount = true,
        };
        var client = new ApiClient
        {
            Name = name,
            KeyPrefix = keyPrefix,
            SecretHash = secretHash,
            Scopes = scopes,
            // Issuing without approval needs a second institution admin to
            // confirm it (dual control): asked for below, off until then.
            // Security:AutoIssueNeedsSecondAdmin (default on) can switch the
            // confirmation off; only the test suite does.
            AutoIssue = request.AutoIssue && !_autoIssueNeedsSecondAdmin,
            RequiresPayment = request.RequiresPayment,
            ServiceUserId = serviceUser.Id,
            CreatedByUserId = CurrentUserId(),
        };

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            _db.Users.Add(serviceUser);
            await _db.SaveChangesAsync(ct);
            _db.UserRoles.Add(new UserRole { UserId = serviceUser.Id, RoleId = developerRole.Id });
            _db.ApiClients.Add(client);
            TenantUserService.Audit(_db, await ActorAsync(ct), "ApiKeyCreated", name,
                $"Permissions: {string.Join(", ", scopes)}; students pay first: {(request.RequiresPayment ? "yes" : "no")}; issue without approval: {(request.AutoIssue ? "asked for" : "no")}");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        if (request.AutoIssue && _autoIssueNeedsSecondAdmin)
        {
            await _controlRequests.RequestAsync(ControlRequestKind.EnableAutoIssue, client.Id, $"API key \"{name}\": issue without approval", await ActorAsync(ct), ct);
        }

        var created = await _db.ApiClients.AsNoTracking()
            .Where(c => c.Id == client.Id)
            .Select(c => new ApiClientResponse(
                c.Id, c.Name, c.KeyPrefix, c.Scopes, c.AutoIssue,
                c.CreatedByUser.FullName, c.CreatedAt, c.LastUsedAt, c.RevokedAt, 0, c.RequiresPayment))
            .FirstAsync(ct);
        return CreatedAtAction(nameof(List), new CreateApiClientResponse(created with { AutoIssuePending = request.AutoIssue && _autoIssueNeedsSecondAdmin }, plaintextKey));
    }

    // Takes effect on the partner's very next request — the key is checked
    // against the database on every call, nothing is cached.
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var client = await _db.ApiClients.Include(c => c.ServiceUser).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();
        if (client.RevokedAt is not null)
        {
            return Conflict(new { error = "API key is already revoked." });
        }

        client.RevokedAt = DateTimeOffset.UtcNow;
        client.RevokedByUserId = CurrentUserId();
        client.ServiceUser.IsActive = false;
        TenantUserService.Audit(_db, await ActorAsync(ct), "ApiKeyRevoked", client.Name);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Webhook ----------
    // The partner's URL and the secret it gave you (e.g. the LMS's "whsec_…").
    // Certyfi then calls the URL whenever one of this key's certificates
    // changes status (see WebhookDispatcher).
    [HttpPut("{id:guid}/webhook")]
    public async Task<ActionResult<ApiClientResponse>> SetWebhook(Guid id, SetWebhookRequest request, CancellationToken ct)
    {
        var client = await _db.ApiClients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();
        if (client.RevokedAt is not null) return Conflict(new { error = "This API key is revoked." });

        var url = request.Url?.Trim() ?? "";
        if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && _configuration.GetValue("Webhooks:AllowHttp", _environment.IsDevelopment()))))
        {
            return BadRequest(new { error = "Enter the full webhook URL, starting with https://." });
        }
        var secret = request.Secret?.Trim();
        if (string.IsNullOrEmpty(secret) && client.WebhookSecretEncrypted is null)
        {
            return BadRequest(new { error = "Enter the webhook secret the partner gave you." });
        }
        if (secret is { Length: > 0 } && (secret.Length < 16 || secret.Length > 200))
        {
            return BadRequest(new { error = "The secret should be between 16 and 200 characters." });
        }

        client.WebhookUrl = uri.ToString();
        if (!string.IsNullOrEmpty(secret)) client.WebhookSecretEncrypted = _secrets.Protect(secret);
        client.WebhookUpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(await Response(id, ct));
    }

    // Stops calls; anything still queued is marked Stopped when its turn comes.
    [HttpDelete("{id:guid}/webhook")]
    public async Task<ActionResult<ApiClientResponse>> RemoveWebhook(Guid id, CancellationToken ct)
    {
        var client = await _db.ApiClients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();
        client.WebhookUrl = null;
        client.WebhookSecretEncrypted = null;
        client.WebhookUpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(await Response(id, ct));
    }

    // Sends a signed "ping" now and reports the partner's answer.
    [HttpPost("{id:guid}/webhook/test")]
    public async Task<ActionResult<WebhookTestResponse>> TestWebhook(Guid id, CancellationToken ct)
    {
        var client = await _db.ApiClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();
        var (status, error) = await _webhooks.PingAsync(client, ct);
        var ok = status is >= 200 and < 300;
        return Ok(new WebhookTestResponse(ok, status, ok ? null : error ?? (status == 401
            ? "The partner refused the signature. Check the URL and secret."
            : $"The partner answered {status}.")));
    }

    [HttpGet("{id:guid}/webhook/deliveries")]
    public async Task<ActionResult<IEnumerable<WebhookDeliveryResponse>>> Deliveries(Guid id, CancellationToken ct)
    {
        if (!await _db.ApiClients.AnyAsync(c => c.Id == id, ct)) return NotFound();
        var rows = await _db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.ApiClientId == id)
            .OrderByDescending(d => d.CreatedAt).Take(50)
            .ToListAsync(ct);
        return Ok(rows.Select(d => new WebhookDeliveryResponse(
            d.Id, d.CredentialId, d.CredentialStatus, d.CreatedAt, d.State.ToString(), d.Attempts, d.LastAttemptAt,
            d.State == WebhookDeliveryState.Pending ? d.NextAttemptAt : null, d.LastStatusCode, d.LastError, d.DeliveredAt)));
    }

    private Task<ApiClientResponse> Response(Guid id, CancellationToken ct) =>
        _db.ApiClients.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new ApiClientResponse(
                c.Id, c.Name, c.KeyPrefix, c.Scopes, c.AutoIssue,
                c.CreatedByUser.FullName, c.CreatedAt, c.LastUsedAt, c.RevokedAt,
                _db.Credentials.Count(cr => cr.RequestedByUserId == c.ServiceUserId), c.RequiresPayment, c.WebhookUrl,
                _db.WebhookDeliveries.Where(w => w.ApiClientId == c.Id && w.State != WebhookDeliveryState.Pending && w.State != WebhookDeliveryState.Sending)
                    .OrderByDescending(w => w.LastAttemptAt).Select(w => w.State == WebhookDeliveryState.Stopped || w.State == WebhookDeliveryState.Failed).FirstOrDefault(),
                _db.ControlRequests.Any(r => r.Kind == ControlRequestKind.EnableAutoIssue && r.TargetId == c.Id && r.Status == ControlRequestStatus.Pending)))
            .FirstAsync(ct);

    private async Task<TenantUserService.Actor> ActorAsync(CancellationToken ct)
    {
        var id = CurrentUserId();
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "Unknown";
        return new TenantUserService.Actor(id, name);
    }

    private Guid CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub!);
    }
}
