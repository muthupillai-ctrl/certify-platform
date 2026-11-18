using System.Text;
using Certyfi.Api.Auth;
using Certyfi.Api.Middleware;
using Certyfi.Infrastructure.Anchoring;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Certyfi.Infrastructure.Signing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------- Configuration ----------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured. See README 'Local setup'.");

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration section is missing.");

// Same "fail fast at startup, not on first use" treatment as Jwt:SigningKey
// above — LocalTenantSigningKeyProvider's own constructor re-validates this
// too (defense in depth, since that class can be unit-tested independent of
// this startup path), but a missing/malformed master key should never be
// discovered for the first time on someone's first credential approval.
builder.Services.Configure<KmsOptions>(builder.Configuration.GetSection("Kms"));
var kmsOptions = builder.Configuration.GetSection("Kms").Get<KmsOptions>()
    ?? throw new InvalidOperationException("Kms configuration section is missing.");
if (string.IsNullOrWhiteSpace(kmsOptions.LocalMasterKey) || Convert.FromBase64String(kmsOptions.LocalMasterKey).Length != 32)
{
    throw new InvalidOperationException("Kms:LocalMasterKey must be a base64-encoded 32-byte key. See README 'Local setup'.");
}

// ---------- Persistence ----------
// PlatformDbContext: single fixed schema ("platform"), standard AddDbContext.
builder.Services.AddDbContext<PlatformDbContext>(opt =>
    opt.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.SchemaName)));

// TenantDbContext: schema resolved per-request from the scoped ITenantContext.
// TenantModelCacheKeyFactory (see MultiTenancy/) makes this safe to cache
// one compiled model per distinct schema rather than per DbContext type.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddDbContext<TenantDbContext>((sp, opt) =>
{
    opt.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"));
    opt.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, TenantModelCacheKeyFactory>();
});

// TenantDbContextFactory is used by TenantProvisioningService to
// create/migrate a brand-new tenant's schema OUTSIDE a resolved-tenant HTTP
// request. It builds TenantDbContext directly against an explicit
// ITenantContext instance rather than resolving one from DI — the standard
// AddDbContextFactory can't be used here because it resolves the DbContext's
// constructor parameters from its own internal DI scope and would just hand
// back an unresolved ITenantContext. See TenantDbContextFactory for the full
// reasoning.
// MigrationsHistoryTable is schema-qualified with the SAME placeholder
// TenantDbContext.CurrentSchema uses when unresolved — TenantDbContextFactory
// substitutes that placeholder with the real tenant schema when generating
// each tenant's provisioning script, so this gives every tenant its own
// independent __ef_migrations_history table instead of one shared table
// whose migration-id rows would collide across tenants.
var tenantDbContextOptions = new DbContextOptionsBuilder<TenantDbContext>()
    .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TenantDbContext.UnresolvedSchemaPlaceholder))
    .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
    .Options;
builder.Services.AddSingleton(tenantDbContextOptions);
builder.Services.AddSingleton<TenantDbContextFactory>();

builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<StudentDirectory>();
builder.Services.AddScoped<StudentSignInIndex>();
builder.Services.AddScoped<ControlRequestService>();
builder.Services.AddScoped<CredentialIssuanceService>();
builder.Services.AddScoped<RevocationService>();
builder.Services.AddScoped<TenantUserService>();
builder.Services.AddScoped<LedgerIntegrityService>();
// Local for now — a real cloud KMS provider swaps in here later as a
// second ITenantSigningKeyProvider implementation, no caller changes.
builder.Services.AddScoped<ITenantSigningKeyProvider, LocalTenantSigningKeyProvider>();
builder.Services.AddScoped<OfflineProofSigner>();
builder.Services.AddScoped<OpenBadgeCredentialIssuer>();

// ---------- Ledger checkpoint anchoring (OpenTimestamps) ----------
// Plain typed HttpClient, no NuGet dependency — the calendar wire protocol
// is simple enough (see OpenTimestampsCalendarClient) to talk to directly.
builder.Services.AddHttpClient<OpenTimestampsCalendarClient>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
});
// Used only to independently verify a confirmed checkpoint's merkle root
// against a real Bitcoin block header (Blockstream's Esplora API) — see
// LedgerAnchorBackgroundService.VerifyAgainstBitcoinBlockAsync.
builder.Services.AddHttpClient("BlockExplorer", http =>
{
    http.BaseAddress = new Uri("https://blockstream.info");
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<ILedgerCheckpointService, LedgerCheckpointService>();
builder.Services.AddSingleton<AnchoringRunStatus>();
builder.Services.AddScoped<AnchoringDiagnosticsService>();
builder.Services.AddHostedService<LedgerAnchorBackgroundService>();

// ---------- Auth ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // Platform tokens are checked against the account on every request:
        // deactivated, or issued before the last password change or reset
        // (SessionVersion), and the token stops working at once.
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                if (principal.HasClaim(c => c.Type == "tenant_id") || !principal.IsInRole(Certyfi.Domain.Enums.SystemRole.SuperAdmin)) return;
                var sub = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var adminId)) { context.Fail("Unknown platform account."); return; }
                var db = context.HttpContext.RequestServices.GetRequiredService<PlatformDbContext>();
                var admin = await db.PlatformAdmins.AsNoTracking()
                    .Where(a => a.Id == adminId).Select(a => new { a.IsActive, a.SessionVersion }).FirstOrDefaultAsync();
                var tokenVersion = int.TryParse(principal.FindFirst(JwtTokenService.SessionVersionClaim)?.Value, out var v) ? v : 0;
                if (admin is null || !admin.IsActive || admin.SessionVersion != tokenVersion)
                {
                    context.Fail("This session has ended. Sign in again.");
                }
            },
        };
    });

builder.Services.AddAuthorization();

// Partner API: X-Api-Key authentication scheme (never the default — see
// ApiKeyAuthenticationHandler), one authorization policy per PartnerScope,
// per-key rate limiting and the partner OpenAPI document.
builder.Services.AddPartnerApi(builder.Configuration, includeStaffApiDocs: builder.Environment.IsDevelopment());
builder.Services.AddVerificationLookupRateLimit(builder.Configuration);
builder.Services.AddHomeAccessRateLimit(builder.Configuration);
builder.Services.AddSingleton<HomeAccessPass>();
builder.Services.AddSingleton<TemplatePreviewLink>();
builder.Services.AddSingleton<StudentPortalLink>();
builder.Services.AddSingleton<PlatformEmailChangeLink>();
builder.Services.AddSingleton<PlatformPasswordResetLink>();

// Email: queued in the outbox, sent in the background (EmailDispatcher)
// through the configured provider — Mailpit (Smtp) locally, Resend in
// production; see EmailOptions.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
var emailProvider = builder.Configuration[$"{EmailOptions.SectionName}:Provider"] ?? "None";
switch (emailProvider)
{
    case "Smtp": builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>(); break;
    case "Resend": builder.Services.AddHttpClient<IEmailTransport, ResendEmailTransport>(c => c.Timeout = TimeSpan.FromSeconds(30)); break;
    case "Memory": builder.Services.AddSingleton<MemoryEmailTransport>(); builder.Services.AddSingleton<IEmailTransport>(sp => sp.GetRequiredService<MemoryEmailTransport>()); break;
    case "None": builder.Services.AddSingleton<IEmailTransport, NoEmailTransport>(); break;
    default: throw new InvalidOperationException($"Email:Provider must be None, Smtp, Resend or Memory (got '{emailProvider}').");
}
builder.Services.AddScoped<EmailOutbox>();
builder.Services.AddScoped<EmailDispatcher>();
builder.Services.AddScoped<StudentNotifications>();
builder.Services.AddHostedService<EmailBackgroundService>();

// Unpaid certificates: reminder, then cancellation at the institution's
// payment deadline (Tenant.PaymentDays).
builder.Services.AddSingleton<PaymentDeadlineRunner>();
builder.Services.AddHostedService<PaymentDeadlineBackgroundService>();

// Webhooks: status changes queued per API key (WebhookQueue), sent signed in
// the background (WebhookDispatcher). Private/internal addresses are refused
// outside development, so a partner URL can't reach Certyfi's own network.
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<WebhookQueue>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService<WebhookBackgroundService>();
var allowPrivateWebhooks = builder.Configuration.GetValue("Webhooks:AllowPrivateAddresses", builder.Environment.IsDevelopment());
builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => WebhookDispatcher.CreateHandler(allowPrivateWebhooks));
builder.Services.AddStudentSignInRateLimit(builder.Configuration);

// ---------- API plumbing ----------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? new[] { "http://localhost:4200" };
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

// Behind Cloudflare -> cloudflared -> the host's Nginx, the API only ever
// sees plain HTTP. Honour X-Forwarded-Proto/For so Request.Scheme is https
// (upload URLs are built from it) and client IPs are real. Known networks
// are cleared because the proxy's address on the Docker network isn't
// fixed; the container has no host port, so only that proxy can reach it.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

// Partner API docs at /api/v1/docs in every environment; the staff API
// document is added there only in Development.
app.UsePartnerApiDocs(includeStaffApiDocs: app.Environment.IsDevelopment());

if (app.Environment.IsDevelopment())
{

    // Known Gap #2 (README): bootstraps the first PlatformAdmin. No-op once
    // one already exists, and no-op if SuperAdminSeed:Email/Password aren't
    // configured (see README "Local setup"). Never runs outside Development
    // — there is deliberately no public API path to create a platform admin.
    using var seedScope = app.Services.CreateScope();
    var platformDb = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await SuperAdminSeeder.SeedAsync(platformDb, app.Configuration);

    // Backfills every existing tenant schema with any tenant-schema
    // migrations added since it was provisioned — convenient for local
    // iteration. Outside Development this is a deliberate, audited,
    // SuperAdmin-only action instead (see TenantsController.MigrateSchemas)
    // rather than something run unconditionally at startup — see that
    // endpoint's comment for why.
    var provisioning = seedScope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
    await provisioning.MigrateAllTenantSchemasAsync();
}

app.UseHttpsRedirection();
app.UseCors("frontend");

// Serves uploaded background images back out at /uploads/{tenant schema}/...
// (see UploadsController) — deliberately BEFORE TenantResolutionMiddleware:
// a plain <img src="..."> / CSS background-image request can't carry the
// X-Tenant-Slug header that middleware requires, so serving these files
// must never depend on tenant resolution succeeding. UseStaticFiles
// short-circuits the pipeline for a matched file, so these requests never
// reach TenantResolutionMiddleware at all. Directory is created up front so
// the StaticFileMiddleware's file provider has something to root itself at
// even before any tenant has uploaded anything.
var uploadsRoot = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "uploads");
Directory.CreateDirectory(uploadsRoot);
app.UseStaticFiles();

// Partner API rate limiting runs before tenant resolution and API-key
// authentication, both of which query the database, so a flood of requests
// is turned away before it costs a lookup. Partitioned by the key's public
// prefix (see PartnerApiSetup); only endpoints with [EnableRateLimiting] are
// limited.
app.UseRateLimiter();

// Order matters: tenant resolution before auth (JWT validation doesn't need
// it, but the tenant-claim guard right after auth does), auth before the
// tenant-claim guard, tenant-claim guard before authorization/controllers.
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantClaimGuardMiddleware>();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposes the top-level-statements-generated Program class to
// WebApplicationFactory<Program> in the integration test project — standard
// idiom for testing minimal-hosting-model APIs.
public partial class Program { }
