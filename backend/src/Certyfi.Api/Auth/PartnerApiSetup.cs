using System.Threading.RateLimiting;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Certyfi.Api.Auth;

public static class PartnerApiDocs
{
    // OpenAPI document name AND the ApiExplorer group PartnerController
    // belongs to — only that controller appears in this document.
    public const string DocumentName = "partner";
    public const string PolicyPrefix = "partner:";

    // Served in every environment (partners need it), under /api/v1/docs so
    // it goes through the same /api/ reverse-proxy route as the API itself.
    // The full staff API document stays Development-only.
    public const string RoutePrefix = "api/v1/docs";
}

public static class PartnerRateLimit
{
    public const string PolicyName = "partner";
}

public static class PartnerApiSetup
{
    public static IServiceCollection AddPartnerApi(this IServiceCollection services, IConfiguration configuration, bool includeStaffApiDocs)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

        services.AddAuthorization(options =>
        {
            foreach (var scope in PartnerScope.All)
            {
                options.AddPolicy(PartnerApiDocs.PolicyPrefix + scope, policy => policy
                    .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, scope));
            }
        });

        // Fixed window per API key prefix (the non-secret part), so one noisy
        // partner can't use up another's allowance. Requests without a
        // well-formed key share one partition per client IP; they fail
        // authentication anyway.
        var permitPerMinute = configuration.GetValue("Partner:RateLimitPerMinute", 120);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                // One OnRejected serves every policy (see also
                // VerificationLookupRateLimit), so the message follows the path.
                context.HttpContext.Response.Headers.RetryAfter = "60";
                var isLookup = context.HttpContext.Request.Path.StartsWithSegments("/api/v1/verify");
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = isLookup
                        ? "Too many certificate look-ups from this connection. Please wait a minute and try again."
                        : $"Rate limit exceeded: {permitPerMinute} requests per minute per API key." }, ct);
            };
            options.AddPolicy(PartnerRateLimit.PolicyName, httpContext =>
            {
                var header = httpContext.Request.Headers[ApiKeyAuthenticationHandler.HeaderName].ToString().Trim();
                var partitionKey = ApiKeyGenerator.TryParsePrefix(header, out var prefix)
                    ? $"key:{prefix}"
                    : $"ip:{httpContext.Connection.RemoteIpAddress}";
                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });

        services.AddSwaggerGen(options =>
        {
            if (includeStaffApiDocs)
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "Certyfi API (staff, internal)", Version = "v1" });
            }
            options.SwaggerDoc(PartnerApiDocs.DocumentName, new OpenApiInfo
            {
                Title = "Certyfi Partner API",
                Version = "v1",
                Description = "Issue and manage Certyfi credentials from your own system. Every request needs two headers: "
                    + "X-Tenant-Slug (your institution's code) and X-Api-Key (a key issued by your institution's admin). "
                    + "Each key only sees the credentials it created.",
            });
            options.DocInclusionPredicate((documentName, api) => documentName == PartnerApiDocs.DocumentName
                ? api.GroupName == PartnerApiDocs.DocumentName
                : api.GroupName != PartnerApiDocs.DocumentName);

            var xmlDocs = Path.Combine(AppContext.BaseDirectory, "Certyfi.Api.xml");
            if (File.Exists(xmlDocs)) options.IncludeXmlComments(xmlDocs);

            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = ApiKeyAuthenticationHandler.HeaderName,
            });
            options.AddSecurityDefinition("TenantSlug", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Tenant-Slug",
            });
            options.DocumentFilter<PartnerSecurityRequirementFilter>();
        });

        return services;
    }

    public static WebApplication UsePartnerApiDocs(this WebApplication app, bool includeStaffApiDocs)
    {
        app.UseSwagger(options => options.RouteTemplate = PartnerApiDocs.RoutePrefix + "/{documentName}/openapi.json");
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = PartnerApiDocs.RoutePrefix;
            options.SwaggerEndpoint($"/{PartnerApiDocs.RoutePrefix}/{PartnerApiDocs.DocumentName}/openapi.json", "Partner API");
            if (includeStaffApiDocs)
            {
                options.SwaggerEndpoint($"/{PartnerApiDocs.RoutePrefix}/v1/openapi.json", "Staff API (internal)");
            }
        });
        return app;
    }
}

// Applies both headers to every operation in the partner document only.
public class PartnerSecurityRequirementFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        if (context.DocumentName != PartnerApiDocs.DocumentName) return;
        swaggerDoc.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>(),
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "TenantSlug" } }] = Array.Empty<string>(),
        });
    }
}
