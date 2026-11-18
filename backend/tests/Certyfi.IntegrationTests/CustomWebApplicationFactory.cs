using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Certyfi.IntegrationTests;

// Spins up a throwaway Postgres container per test run (rather than pointing
// at the dev-time docker-compose instance) so these tests are hermetic and
// never collide with, or depend on, whatever tenants happen to exist in a
// developer's local database.
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("certyfi_test")
        .WithUsername("certyfi_test")
        .WithPassword("certyfi_test_only")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development, not Test/Production — SuperAdminSeeder and the
        // Swagger UI are both gated on Development, and the seeder is
        // harmless here (no SuperAdminSeed:* config means it's a no-op).
        builder.UseEnvironment("Development");
        // Tests of other features provision one admin and use it to request
        // and approve certificates, as admins could before final approval
        // existed; see SingleAdminTestProvisioning. FinalApprovalTests
        // provisions two admins and gets none of this.
        builder.ConfigureServices(services =>
        {
            services.AddScoped<Certyfi.Infrastructure.Services.TenantProvisioningService>();
            services.AddScoped<Certyfi.Infrastructure.Services.ITenantProvisioningService, SingleAdminTestProvisioning>();
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Program.cs reads Jwt:SigningKey eagerly at the very top (to build
        // AddJwtBearer's TokenValidationParameters) — before
        // ConfigureAppConfiguration callbacks get merged in. Environment
        // variables are added as a config source synchronously inside
        // WebApplication.CreateBuilder(args) itself, so they're visible even
        // to that eager read, unlike ConfigureAppConfiguration overrides.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-only-signing-key-do-not-reuse-0123456789");
        // Must decode to exactly 32 bytes (AES-256) — see Kms:LocalMasterKey's
        // own validation in Program.cs. Fixed, not random, so a failing test
        // is reproducible.
        // Emails are captured in memory (MemoryEmailTransport); tests send
        // the outbox explicitly (EmailDispatcher.SendDueAsync).
        Environment.SetEnvironmentVariable("Email__Provider", "Memory");
        Environment.SetEnvironmentVariable("Email__DispatchIntervalSeconds", "0");
        // Payment deadlines are run explicitly (PaymentDeadlineRunner) with a chosen "now".
        Environment.SetEnvironmentVariable("PaymentDeadlines__IntervalMinutes", "0");
        // Webhooks are sent explicitly (WebhookDispatcher.RunAsync).
        Environment.SetEnvironmentVariable("Webhooks__DispatchIntervalSeconds", "0");
        Environment.SetEnvironmentVariable("PublicApp__BaseUrl", "https://certyfi.test");
        // Most tests make templates without signatories; SignatoryTests turns
        // the rule on (see CertificateTemplatesController).
        Environment.SetEnvironmentVariable("Certificates__EnforceMandatoryContent", "false");
        // Keys with "issue without approval" work at once in most tests;
        // FinalApprovalTests turns the second-admin confirmation on.
        Environment.SetEnvironmentVariable("Security__AutoIssueNeedsSecondAdmin", "false");
        Environment.SetEnvironmentVariable("Kms__LocalMasterKey",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")));

        // Migrate via a standalone context BEFORE ever touching Services —
        // Services is what triggers the real host to start (Program.cs's
        // app.Run(), including SuperAdminSeeder), and SuperAdminSeeder
        // queries platform.platform_admins, which would fail if that table
        // doesn't exist yet.
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.SchemaName))
            .Options;
        await using (var platformDb = new PlatformDbContext(options))
        {
            await platformDb.Database.MigrateAsync();
        }
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}
