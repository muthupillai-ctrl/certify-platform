using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// The integrity check's timestamp-precision fix and its failure details.
// Postgres keeps microseconds; .NET timestamps carry 100 ns ticks. Hashing
// the unrounded value made production (Linux, sub-microsecond clock)
// report "entry data has been altered" for untouched entries on
// 2026-09-29. Timestamps are now rounded before hashing; entries written
// before that pass only if restoring the one dropped digit reproduces the
// hash. Real edits and deletions still fail, with details an admin can use.
public class LedgerIntegrityDetailsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public LedgerIntegrityDetailsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SubMicrosecondTimestamps_NowVerify()
    {
        var t = await SetUp();
        await using (var db = TenantDb(t))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var ledger = new LedgerService(db);
            await ledger.AppendAsync(t.CredentialId, "Note", t.AdminUserId, new DateTimeOffset(2026, 9, 29, 6, 55, 16, TimeSpan.Zero).AddTicks(7_152_357), "{}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var report = await Integrity(t);
        Assert.True(report.Intact, report.Reason);
        Assert.Equal(0, report.PrecisionAdjustedEntries);
    }

    [Fact]
    public async Task EntriesWrittenBeforeTheFix_PassWithANote()
    {
        var t = await SetUp();
        await using (var db = TenantDb(t))
        {
            var previous = await db.LedgerEntries.OrderByDescending(e => e.SequenceNumber).Select(e => e.EntryHash).FirstAsync();
            var unrounded = new DateTimeOffset(2026, 9, 29, 6, 55, 16, TimeSpan.Zero).AddTicks(7_152_356);
            // Exactly what the old code did: hash the 100 ns value, let Postgres drop the last digit.
            db.LedgerEntries.Add(new LedgerEntry
            {
                CredentialId = t.CredentialId, EventType = "Note", ActorUserId = t.AdminUserId, OccurredAt = unrounded, EventDataJson = "{}",
                PreviousEntryHash = previous,
                EntryHash = LedgerService.ComputeEntryHash(previous, "Note", t.CredentialId, t.AdminUserId, unrounded, "{}"),
            });
            await db.SaveChangesAsync();
        }

        var report = await Integrity(t);
        Assert.True(report.Intact, report.Reason);
        Assert.Equal(1, report.PrecisionAdjustedEntries);
    }

    [Fact]
    public async Task EditedEntry_FailsWithDetailsForTheAdmin()
    {
        var t = await SetUp();
        await Tamper(t, $"UPDATE \"{t.Schema}\".ledger_entries SET \"EventDataJson\" = 'edited' WHERE \"SequenceNumber\" = (SELECT MIN(\"SequenceNumber\") FROM \"{t.Schema}\".ledger_entries)");

        var report = await Integrity(t);
        Assert.False(report.Intact);
        var problem = Assert.IsType<LedgerIntegrityProblem>(report.Problem);
        Assert.Equal(LedgerProblemKind.ContentChanged, problem.Kind);
        Assert.Equal("Requested", problem.EventType);
        Assert.Equal(t.CredentialId, problem.CredentialId);
        Assert.Equal("Jane Doe", problem.RecipientName);
        Assert.Equal("Course Completion", problem.AchievementTitle);
        Assert.Matches("^[2-9A-Z]{4}-[2-9A-Z]{4}-[2-9A-Z]{4}$", problem.CertificateCode);
        Assert.NotNull(problem.ActorName);
        Assert.NotEqual(problem.StoredEntryHash, problem.RecomputedEntryHash);
        Assert.Contains("Contact Certyfi support", problem.WhatToDo);
    }

    [Fact]
    public async Task DeletedEntry_IsReportedAsABrokenChain()
    {
        var t = await SetUp();
        await Tamper(t, $"DELETE FROM \"{t.Schema}\".ledger_entries WHERE \"SequenceNumber\" = (SELECT MIN(\"SequenceNumber\") FROM \"{t.Schema}\".ledger_entries)");

        var report = await Integrity(t);
        Assert.False(report.Intact);
        Assert.Equal(LedgerProblemKind.ChainBroken, report.Problem!.Kind);
        Assert.NotEqual(report.Problem.ExpectedPreviousHash, report.Problem.StoredPreviousHash);
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema, string AdminToken, Guid AdminUserId, Guid CredentialId);

    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Tenant tenant;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!");
        }
        var client = _factory.CreateClient();
        var admin = await Login(client, tenant.Slug, $"admin@{suffix}.test", "TenantAdmin123!");
        await Send(client, tenant.Slug, admin, HttpMethod.Post, "/api/v1/users", new CreateUserRequest($"app@{suffix}.test", "Approver", "Password123!", "Approver"));
        var approver = await Login(client, tenant.Slug, $"app@{suffix}.test", "Password123!");

        var achievement = await (await Send(client, tenant.Slug, admin, HttpMethod.Post, "/api/v1/achievements",
            new CreateAchievementRequest("Course Completion", null, null, null, null, null))).Content.ReadFromJsonAsync<AchievementResponse>();
        var draft = await (await Send(client, tenant.Slug, admin, HttpMethod.Post, "/api/v1/credentials",
            new CreateCredentialRequest(achievement!.Id, "Jane Doe", "jane@example.com", null))).Content.ReadFromJsonAsync<CredentialResponse>();
        Assert.Equal(HttpStatusCode.OK, (await Send(client, tenant.Slug, approver, HttpMethod.Post, $"/api/v1/credentials/{draft!.Id}/approve", null)).StatusCode);

        var users = await (await Send(client, tenant.Slug, admin, HttpMethod.Get, "/api/v1/users", null)).Content.ReadFromJsonAsync<List<TenantUserDetail>>();
        return new Setup(client, tenant.Slug, tenant.Id, tenant.SchemaName, admin, users!.Single(u => u.Email == $"admin@{suffix}.test").Id, draft.Id);
    }

    private TenantDbContext TenantDb(Setup t) =>
        _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema);

    // The append-only trigger stops edits; a determined attacker with
    // database access could still disable it, which is what the check exists for.
    private async Task Tamper(Setup t, string sql)
    {
        await using var db = TenantDb(t);
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{t.Schema}\".ledger_entries DISABLE TRIGGER trg_ledger_entries_append_only; {sql}; " +
            $"ALTER TABLE \"{t.Schema}\".ledger_entries ENABLE TRIGGER trg_ledger_entries_append_only;");
    }

    private static async Task<LedgerIntegrityReport> Integrity(Setup t)
    {
        var response = await Send(t.Client, t.Slug, t.AdminToken, HttpMethod.Get, "/api/v1/ledger/integrity", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LedgerIntegrityReport>())!;
    }

    private static async Task<string> Login(HttpClient client, string slug, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", slug);
        return (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string slug, string token, HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }
}
