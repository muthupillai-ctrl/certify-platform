using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// GET /dashboard (certificates plan phase 7): attention counts, issuing and
// verification trends, months in the caller's time zone, staff only.
public class DashboardTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DashboardTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Attention_Totals_Sources_AndVerifications()
    {
        var t = await SetUp();
        var d = await Dashboard(t, "UTC");

        Assert.Equal(1, d.Attention.PendingApproval);
        Assert.NotNull(d.Attention.OldestPendingAt);
        Assert.Equal(1, d.Attention.RejectedLast30Days);
        Assert.Equal(1, d.Attention.ExpiringIn30Days);
        Assert.Equal(1, d.Attention.AnchoringFailed);
        Assert.Equal(0, d.Attention.AnchoringDelayed);

        Assert.Equal(4, d.Totals.Active); // Dan too: issued long ago, still valid
        Assert.Equal(12, d.IssuedByMonth.Count);
        Assert.Equal(3, d.IssuedByMonth.Sum(m => m.Count)); // the 13-month-old one is outside the range
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM"), d.IssuedByMonth[^1].Month);

        var achievement = Assert.Single(d.IssuedByAchievement);
        Assert.Equal(("Fire Safety", 3), (achievement.Name, achievement.Count));
        var staff = Assert.Single(d.IssuedBySource);
        Assert.Equal(("Staff", 3), (staff.Source, staff.Count));

        Assert.Equal(5, d.VerificationsByMonth.Sum(m => m.Count));
        Assert.Equal(5, d.Totals.VerificationsThisMonth);
        Assert.Equal(1, d.UnmatchedLookups);
        Assert.Equal(2, d.VerificationsByChannel.Single(c => c.Channel == VerificationChannel.VerifyPage).Count);
        var top = d.MostVerified[0];
        Assert.Equal(("Alice Smith", 3), (top.RecipientName, top.Count));
        Assert.Equal(2, d.MostVerified.Count);
    }

    [Fact]
    public async Task NewAndApprovedCertificates_ShowAtOnce_DespiteTheCache()
    {
        var t = await SetUp();
        var before = await Dashboard(t, "UTC");
        Assert.Equal(before.GeneratedAt, (await Dashboard(t, "UTC")).GeneratedAt); // cached while nothing changes

        var fire = (await Json<List<AchievementResponse>>(await Send(t, "/api/v1/achievements", t.AdminToken))).Single().Id;
        var created = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", t.RegistrarToken, HttpMethod.Post,
            new CreateCredentialRequest(fire, "Gina New", "gina@example.com", "{}")));
        Assert.Equal(before.Attention.PendingApproval + 1, (await Dashboard(t, "UTC")).Attention.PendingApproval);

        Assert.Equal(HttpStatusCode.OK, (await Send(t, $"/api/v1/credentials/{created.Id}/approve", t.AdminToken, HttpMethod.Post)).StatusCode);
        var after = await Dashboard(t, "UTC");
        Assert.Equal(before.Attention.PendingApproval, after.Attention.PendingApproval);
        Assert.Equal(before.Totals.Active + 1, after.Totals.Active);
    }

    [Fact]
    public async Task Months_FollowTheCallersTimeZone()
    {
        var t = await SetUp();
        // 19:30 UTC on the last day of last month = 01:00 on the 1st in India.
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, kolkata);
        var monthStartIndia = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, TimeSpan.FromHours(5.5));
        await using (var db = Db(t))
            await db.Credentials.Where(c => c.RecipientName == "Bob Jones")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.IssuedAt, monthStartIndia.AddHours(1).ToUniversalTime()));

        var india = await Dashboard(t, "Asia/Kolkata");
        // Older browsers report the legacy name; it must mean the same zone.
        Assert.Equal(india.IssuedByMonth, (await Dashboard(t, "Asia/Calcutta")).IssuedByMonth);
        var utc = await Dashboard(t, "UTC");

        Assert.Equal(india.IssuedByMonth[^1].Count, utc.IssuedByMonth[^1].Count + 1);
        Assert.Equal($"{local:yyyy-MM}-01".Substring(0, 7), india.IssuedByMonth[^1].Month);
    }

    [Fact]
    public async Task StaffOnly_AndRejectsUnknownZones()
    {
        var t = await SetUp();
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "/api/v1/dashboard", t.LearnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(t, "/api/v1/dashboard", t.RegistrarToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, "/api/v1/dashboard?tz=Mars/Olympus", t.AdminToken)).StatusCode);
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema, string AdminToken, string RegistrarToken, string LearnerToken);

    // Fire Safety: Alice, Bob, Carol active (issued now; Carol expires in 10
    // days), Dan active but issued 13 months ago; Erin pending; Frank
    // rejected. Verifications: Alice x3, Bob x1, one unmatched look-up. One
    // failed anchoring checkpoint.
    private async Task<Setup> SetUp()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Tenant tenant;
        using (var scope = _factory.Services.CreateScope())
        {
            tenant = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
                .ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", "TenantAdmin123!");
        }
        var t = new Setup(_factory.CreateClient(), tenant.Slug, tenant.Id, tenant.SchemaName, "", "", "");
        var admin = await Login(t, $"admin@{suffix}.test", "TenantAdmin123!");
        t = t with { AdminToken = admin };
        await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"reg@{suffix}.test", "Registrar", "Password123!", "Registrar"));
        await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"learner@{suffix}.test", "Learner", "Password123!", "Learner"));
        t = t with
        {
            RegistrarToken = await Login(t, $"reg@{suffix}.test", "Password123!"),
            LearnerToken = await Login(t, $"learner@{suffix}.test", "Password123!"),
        };

        var fire = (await Json<AchievementResponse>(await Send(t, "/api/v1/achievements", admin, HttpMethod.Post,
            new CreateAchievementRequest("Fire Safety", null, null, null, "[]", null)))).Id;
        var ids = new Dictionary<string, Guid>();
        foreach (var name in new[] { "Alice Smith", "Bob Jones", "Carol White", "Dan Brown", "Erin Green", "Frank Black" })
        {
            var draft = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", t.RegistrarToken, HttpMethod.Post,
                new CreateCredentialRequest(fire, name, $"{name.Split(' ')[0].ToLowerInvariant()}@example.com", "{}")));
            ids[name] = draft.Id;
        }
        foreach (var name in new[] { "Alice Smith", "Bob Jones", "Carol White", "Dan Brown" })
            Assert.Equal(HttpStatusCode.OK, (await Send(t, $"/api/v1/credentials/{ids[name]}/approve", admin, HttpMethod.Post)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(t, $"/api/v1/credentials/{ids["Frank Black"]}/reject", admin, HttpMethod.Post, new { reason = "Wrong name" })).StatusCode);

        await using var db = Db(t);
        var now = DateTimeOffset.UtcNow;
        await db.Credentials.Where(c => c.Id == ids["Carol White"]).ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresOn, now.AddDays(10)));
        await db.Credentials.Where(c => c.Id == ids["Dan Brown"]).ExecuteUpdateAsync(u => u.SetProperty(c => c.IssuedAt, now.AddMonths(-13)));
        db.VerificationEvents.AddRange(
            new VerificationEvent { CredentialId = ids["Alice Smith"], Channel = VerificationChannel.VerifyPage, Result = "Active" },
            new VerificationEvent { CredentialId = ids["Alice Smith"], Channel = VerificationChannel.VerifyPage, Result = "Active" },
            new VerificationEvent { CredentialId = ids["Alice Smith"], Channel = VerificationChannel.CodeLookup, Result = "Active" },
            new VerificationEvent { CredentialId = ids["Bob Jones"], Channel = VerificationChannel.Api, Result = "Active" },
            new VerificationEvent { CredentialId = null, Channel = VerificationChannel.CodeLookup, Result = "NotFound" });
        db.LedgerCheckpoints.Add(new LedgerCheckpoint { UpToSequenceNumber = 1, CheckpointHash = "x", Status = "Failed", OtsProof = [] });
        await db.SaveChangesAsync();
        return t;
    }

    private TenantDbContext Db(Setup t) =>
        _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema);

    private static async Task<DashboardResponse> Dashboard(Setup t, string tz) =>
        await Json<DashboardResponse>(await Send(t, $"/api/v1/dashboard?tz={Uri.EscapeDataString(tz)}", t.AdminToken));

    private static async Task<string> Login(Setup t, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new TenantLoginRequest(email, password)) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        return (await Json<LoginResponse>(await t.Client.SendAsync(request))).AccessToken;
    }

    private static Task<HttpResponseMessage> Send(Setup t, string url, string token, HttpMethod? method = null, object? body = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
