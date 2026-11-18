using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// The Certificates page's server-side list (plan phase 1): paging, filters,
// sorting, search (including custom field values and codes), status counts,
// Learners seeing only their own certificates, and speed at 5,000 rows.
public class CredentialListTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CredentialListTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Pages_WithStableTotals()
    {
        var t = await SetUp();
        var page1 = await List(t, "pageSize=3&page=1&sort=recipient");
        var page2 = await List(t, "pageSize=3&page=2&sort=recipient");

        Assert.Equal(6, page1.Total);
        Assert.Equal(3, page1.Items.Count);
        Assert.Equal(3, page2.Items.Count);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
        Assert.Equal(page1.Items.Select(i => i.RecipientName).OrderBy(n => n), page1.Items.Select(i => i.RecipientName));
        Assert.True(string.Compare(page1.Items[^1].RecipientName, page2.Items[0].RecipientName, StringComparison.OrdinalIgnoreCase) <= 0);
    }

    [Fact]
    public async Task StatusFilter_SeparatesActiveFromExpired_AndCountsIgnoreStatus()
    {
        var t = await SetUp();

        var active = await List(t, "status=Active");
        var expired = await List(t, "status=Expired");
        var pending = await List(t, "status=PendingApproval");

        Assert.Equal(3, active.Total);
        Assert.All(active.Items, i => Assert.Equal("Active", i.Status));
        Assert.Equal("Expired", Assert.Single(expired.Items).Status);
        Assert.Equal(2, pending.Total);

        // Counts are the same whichever status is selected.
        Assert.Equal(active.StatusCounts, pending.StatusCounts);
        Assert.Equal(3, active.StatusCounts["Active"]);
        Assert.Equal(1, active.StatusCounts["Expired"]);
        Assert.Equal(2, active.StatusCounts["PendingApproval"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, $"/api/v1/credentials?status=Bogus", t.AdminToken)).StatusCode);
    }

    [Fact]
    public async Task AchievementAndRequesterFilters_AndTheirOptions()
    {
        var t = await SetUp();
        var options = await Json<CredentialFilterOptionsResponse>(await Send(t, "/api/v1/credentials/filter-options", t.AdminToken));
        Assert.Equal(new[] { "Fire Safety", "First Aid" }, options.Achievements.Select(a => a.Name).OrderBy(n => n));

        var fireSafety = options.Achievements.Single(a => a.Name == "Fire Safety").Id;
        Assert.Equal(4, (await List(t, $"achievementId={fireSafety}")).Total);

        var registrar = options.Requesters.Single(r => r.Name == "Registrar").Id;
        Assert.Equal(6, (await List(t, $"requestedBy={registrar}")).Total);
    }

    [Theory]
    [InlineData("alice", 1)]            // name, case-insensitive
    [InlineData("BOB@EXAMPLE", 1)]      // email
    [InlineData("ROLL-2026-003", 1)]    // custom field value
    [InlineData("first aid", 2)]        // achievement title
    [InlineData("100%", 0)]             // wildcard characters are literal
    [InlineData("x", 6)]                // below 2 characters: ignored
    public async Task Search_MatchesNamesEmailsFieldsAndTitles(string q, int expected)
    {
        var t = await SetUp();
        Assert.Equal(expected, (await List(t, $"q={Uri.EscapeDataString(q)}")).Total);
    }

    [Fact]
    public async Task Search_FindsByCodeTypedAnyWay_AndById()
    {
        var t = await SetUp();
        var any = (await List(t, "status=Active&pageSize=1")).Items.Single();

        var typed = any.VerificationCode.Replace("-", " ").ToLowerInvariant();
        Assert.Contains(any.Id, (await List(t, $"q={Uri.EscapeDataString(typed)}")).Items.Select(i => i.Id));
        Assert.Contains(any.Id, (await List(t, $"q={any.Id.ToString()[..13]}")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Learner_SeesOnlyTheirOwnCertificates()
    {
        var t = await SetUp();
        await Send(t, "/api/v1/users", t.AdminToken, HttpMethod.Post,
            new CreateUserRequest("alice@example.com", "Alice Learner", "Learner-password-1", "Learner"));
        var learner = await Login(t, "alice@example.com", "Learner-password-1");

        var mine = await List(t, "", learner);
        var only = Assert.Single(mine.Items);
        Assert.Equal("alice@example.com", only.RecipientEmail);

        var someoneElse = (await List(t, "q=bob")).Items.Single().Id;
        Assert.Equal(HttpStatusCode.NotFound, (await Send(t, $"/api/v1/credentials/{someoneElse}", learner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(t, $"/api/v1/credentials/{someoneElse}/ledger", learner)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(t, $"/api/v1/credentials/{only.Id}", learner)).StatusCode);
    }

    [Fact]
    public async Task FiveThousandCertificates_ListAndSearchStayFast()
    {
        var t = await SetUp();
        await using (var db = _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema))
        {
            // Clone an existing certificate 5,000 times with unique ids, codes and names.
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO ""{t.Schema}"".credentials
  (""Id"", ""AchievementId"", ""RecipientName"", ""RecipientEmail"", ""Status"", ""RequestedByUserId"", ""CreatedAt"",
   ""FieldValuesJson"", ""FieldSchemaJson"", ""VerificationCode"")
SELECT gen_random_uuid(), c.""AchievementId"", 'Bulk Recipient ' || g, 'bulk' || g || '@example.com', c.""Status"", c.""RequestedByUserId"",
       now() - (g || ' minutes')::interval, json_build_object('roll', 'BULK-' || g)::text, c.""FieldSchemaJson"",
       upper(substr(md5(g::text || random()::text), 1, 12))
FROM generate_series(1, 5000) g
CROSS JOIN (SELECT * FROM ""{t.Schema}"".credentials LIMIT 1) c;
ANALYZE ""{t.Schema}"".credentials;");
        }

        await List(t, "pageSize=25"); // warm-up
        var sw = Stopwatch.StartNew();
        var firstPage = await List(t, "pageSize=25");
        var search = await List(t, "q=BULK-4321");
        sw.Stop();

        Assert.Equal(5006, firstPage.Total);
        Assert.Equal(25, firstPage.Items.Count);
        Assert.Equal(1, search.Total);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"List + search took {sw.ElapsedMilliseconds} ms");
    }

    // ---------- Phase 2: quick views and the sidebar badge ----------

    [Fact]
    public async Task NeedsMyApproval_IsPendingNotRequestedByMe_OldestFirst()
    {
        var t = await SetUp();
        // The admin requests one too: it must not be in the admin's own queue.
        var achievement = (await Json<CredentialFilterOptionsResponse>(await Send(t, "/api/v1/credentials/filter-options", t.AdminToken))).Achievements[0].Id;
        var own = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", t.AdminToken, HttpMethod.Post,
            new CreateCredentialRequest(achievement, "Own Request", "own@example.com", null)));

        var queue = await List(t, "status=PendingApproval&excludeMine=true&sort=created");
        Assert.Equal(2, queue.Total);
        Assert.DoesNotContain(own.Id, queue.Items.Select(i => i.Id));
        Assert.Equal(queue.Items.OrderBy(i => i.CreatedAt).Select(i => i.Id), queue.Items.Select(i => i.Id));

        var summary = await Json<CredentialSummaryCountsResponse>(await Send(t, "/api/v1/credentials/summary", t.AdminToken));
        Assert.True(summary.CanApprove);
        Assert.Equal(2, summary.PendingForMe);
        Assert.Equal(1, summary.MyRequestsPending);
    }

    [Fact]
    public async Task MyRequests_AndRegistrarSummary()
    {
        var t = await SetUp();
        Assert.Equal(6, (await List(t, "mine=true", t.RegistrarToken)).Total);
        Assert.Equal(0, (await List(t, "mine=true")).Total); // the admin requested none

        var pending = (await List(t, "status=PendingApproval")).Items[0].Id;
        Assert.Equal(HttpStatusCode.OK, (await Send(t, $"/api/v1/credentials/{pending}/reject", t.AdminToken, HttpMethod.Post,
            new RejectCredentialRequest("Wrong name"))).StatusCode);

        var summary = await Json<CredentialSummaryCountsResponse>(await Send(t, "/api/v1/credentials/summary", t.RegistrarToken));
        Assert.False(summary.CanApprove);
        Assert.Equal(0, summary.PendingForMe);
        Assert.Equal(6, summary.MyRequests);
        Assert.Equal(1, summary.MyRequestsPending);
        Assert.Equal(1, summary.MyRequestsRejected);
    }

    [Fact]
    public async Task RecentlyIssued_PutsNotYetIssuedLast()
    {
        var t = await SetUp();
        var items = (await List(t, "sort=-issued")).Items;
        Assert.All(items.Take(4), i => Assert.NotNull(i.IssuedAt));
        Assert.All(items.Skip(4), i => Assert.Null(i.IssuedAt));
        Assert.Equal(items.Take(4).OrderByDescending(i => i.IssuedAt).Select(i => i.Id), items.Take(4).Select(i => i.Id));
    }

    // ---------- Phase 4: bulk actions and CSV export ----------

    [Fact]
    public async Task BulkApprove_AppliesMakerChecker_AndSkipsWithReasons()
    {
        var t = await SetUp();
        var achievement = (await Json<CredentialFilterOptionsResponse>(await Send(t, "/api/v1/credentials/filter-options", t.AdminToken))).Achievements[0].Id;
        var own = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", t.AdminToken, HttpMethod.Post,
            new CreateCredentialRequest(achievement, "Own Request", "own@example.com", null)));
        var pending = (await List(t, "status=PendingApproval&mine=false&excludeMine=true")).Items.Select(i => i.Id).ToList();
        var active = (await List(t, "status=Active")).Items[0].Id;

        var result = await Json<BulkActionResponse>(await Send(t, "/api/v1/credentials/bulk/approve", t.AdminToken, HttpMethod.Post,
            new BulkActionRequest(pending.Append(own.Id).Append(active).ToList(), null, null)));

        Assert.Equal(2, result.Done);
        Assert.Equal(2, result.Skipped);
        Assert.Contains("You requested it", result.Results.Single(r => r.Id == own.Id).Reason);
        Assert.Contains("not waiting for approval", result.Results.Single(r => r.Id == active).Reason);
        foreach (var id in pending)
        {
            var verify = await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{id}");
            Assert.Equal("Active", (await Json<VerifyCredentialResponse>(verify)).Status);
        }

        var integrity = await Json<LedgerIntegrityReport>(await Send(t, "/api/v1/ledger/integrity", t.AdminToken));
        Assert.True(integrity.Intact, integrity.Reason);
    }

    [Fact]
    public async Task BulkSuspendAndRevoke_AreNotAvailable()
    {
        var t = await SetUp();
        foreach (var operation in new[] { "suspend", "revoke" })
        {
            var response = await Send(t, $"/api/v1/credentials/bulk/{operation}", t.AdminToken, HttpMethod.Post,
                new BulkActionRequest(null, new CredentialListQuery { Status = "Active" }, "Issued in error"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(3, (await List(t, "status=Active")).Total);
    }

    [Fact]
    public async Task Bulk_RequiresReason_ApproverRole_AndRespectsTheLimit()
    {
        var t = await SetUp();
        var ids = (await List(t, "status=Active")).Items.Select(i => i.Id).ToList();

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, "/api/v1/credentials/bulk/reject", t.AdminToken, HttpMethod.Post,
            new BulkActionRequest(ids, null, " "))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, "/api/v1/credentials/bulk/approve", t.RegistrarToken, HttpMethod.Post,
            new BulkActionRequest(ids, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, "/api/v1/credentials/bulk/approve", t.AdminToken, HttpMethod.Post,
            new BulkActionRequest(Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList(), null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(t, "/api/v1/credentials/bulk/delete", t.AdminToken, HttpMethod.Post,
            new BulkActionRequest(ids, null, "x"))).StatusCode);
    }

    [Fact]
    public async Task ExportCsv_HasCustomFieldColumns_LinksOnlyWhenVerifiable_AndDefusesFormulas()
    {
        var t = await SetUp();
        var achievement = (await Json<CredentialFilterOptionsResponse>(await Send(t, "/api/v1/credentials/filter-options", t.AdminToken))).Achievements[0].Id;
        await Send(t, "/api/v1/credentials", t.AdminToken, HttpMethod.Post,
            new CreateCredentialRequest(achievement, "=HYPERLINK(\"http://evil\",\"x\")", "evil@example.com", null));

        var response = await Send(t, "/api/v1/credentials/export.csv?sort=recipient", t.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = System.Text.Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(8, lines.Length); // header + 7 certificates
        Assert.StartsWith("Recipient,Email,Achievement,Status,Certificate code", lines[0]);
        Assert.EndsWith(",Roll number", lines[0]);
        var alice = lines.Single(l => l.StartsWith("Alice Smith,"));
        Assert.Contains($"/verify/{t.Slug}/", alice);
        Assert.EndsWith(",ROLL-2026-001", alice);
        var erin = lines.Single(l => l.StartsWith("Erin Green,")); // pending: no public link
        Assert.DoesNotContain("/verify/", erin);
        Assert.Contains(lines, l => l.StartsWith("\"'=HYPERLINK("));

        var pendingOnly = await Send(t, "/api/v1/credentials/export.csv?status=PendingApproval", t.AdminToken);
        Assert.Equal(4, (await pendingOnly.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // ---------- phase 5: more filters ----------

    [Fact]
    public async Task IssuedBetween_UsesTheRangeGiven_AndSkipsNotYetIssued()
    {
        var t = await SetUp();
        await using (var db = Db(t))
            await db.Credentials.Where(c => c.RecipientName == "Alice Smith")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.IssuedAt, DateTimeOffset.UtcNow.AddDays(-40)));
        var cutoff = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));

        var recent = await List(t, $"issuedFrom={cutoff}");
        var older = await List(t, $"issuedBefore={cutoff}");

        Assert.Equal(new[] { "Bob Jones", "Carol White", "Dan Brown" }, recent.Items.Select(i => i.RecipientName).OrderBy(n => n));
        Assert.Equal("Alice Smith", Assert.Single(older.Items).RecipientName);
    }

    [Fact]
    public async Task ExpiringWithin_IsActiveOnly_AndExcludesAlreadyExpired()
    {
        var t = await SetUp();
        await using (var db = Db(t))
        {
            await db.Credentials.Where(c => c.RecipientName == "Bob Jones")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresOn, DateTimeOffset.UtcNow.AddDays(10)));
            await db.Credentials.Where(c => c.RecipientName == "Carol White")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresOn, DateTimeOffset.UtcNow.AddDays(50)));
        }

        var in30 = await List(t, "expiringWithinDays=30");
        var in60 = await List(t, "expiringWithinDays=60&sort=expires");

        Assert.Equal("Bob Jones", Assert.Single(in30.Items).RecipientName);
        Assert.Equal(new[] { "Bob Jones", "Carol White" }, in60.Items.Select(i => i.RecipientName));
    }

    [Fact]
    public async Task SourceAndApiKey_Filters_AndApiKeyOptions()
    {
        var t = await SetUp();
        var created = await Json<CreateApiClientResponse>(await Send(t, "/api/v1/api-clients", t.AdminToken, HttpMethod.Post,
            new CreateApiClientRequest("Student system", new[] { "credentials:write" }, false)));
        await using (var db = Db(t))
        {
            var serviceUserId = await db.ApiClients.Where(a => a.Id == created.Client.Id).Select(a => a.ServiceUserId).SingleAsync();
            await db.Credentials.Where(c => c.RecipientName == "Erin Green")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.RequestedByUserId, serviceUserId));
        }

        var api = await List(t, "source=Api");
        Assert.Equal("Erin Green", Assert.Single(api.Items).RecipientName);
        Assert.Equal("Api", api.Items[0].Source);
        Assert.Equal(5, (await List(t, "source=Staff")).Total);
        Assert.Equal("Erin Green", Assert.Single((await List(t, $"apiClientId={created.Client.Id}")).Items).RecipientName);
        Assert.Equal(0, (await List(t, $"apiClientId={Guid.NewGuid()}")).Total);

        var options = await Json<CredentialFilterOptionsResponse>(await Send(t, "/api/v1/credentials/filter-options", t.AdminToken));
        Assert.Equal("Student system", Assert.Single(options.ApiClients).Name);
    }

    // ---------- helpers ----------

    private record Setup(HttpClient Client, string Slug, Guid TenantId, string Schema, string AdminToken, string RegistrarToken = "");

    // Six certificates: 3 active, 1 expired (active but past expiry), 2 pending;
    // Fire Safety x4, First Aid x2; each with a roll number custom field.
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
        var partial = new Setup(client, tenant.Slug, tenant.Id, tenant.SchemaName, "");
        var admin = await Login(partial, $"admin@{suffix}.test", "TenantAdmin123!");
        var t = partial with { AdminToken = admin };
        await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"reg@{suffix}.test", "Registrar", "Password123!", "Registrar"));
        var registrar = await Login(t, $"reg@{suffix}.test", "Password123!");

        const string schema = "[{\"key\":\"roll\",\"label\":\"Roll number\",\"type\":\"text\"}]";
        var fire = (await Json<AchievementResponse>(await Send(t, "/api/v1/achievements", admin, HttpMethod.Post,
            new CreateAchievementRequest("Fire Safety", null, null, null, schema, null)))).Id;
        var aid = (await Json<AchievementResponse>(await Send(t, "/api/v1/achievements", admin, HttpMethod.Post,
            new CreateAchievementRequest("First Aid", null, null, null, schema, null)))).Id;

        var people = new (string Name, string Email, Guid Achievement, bool Approve)[]
        {
            ("Alice Smith", "alice@example.com", fire, true), ("Bob Jones", "bob@example.com", fire, true),
            ("Carol White", "carol@example.com", fire, true), ("Dan Brown", "dan@example.com", fire, true),
            ("Erin Green", "erin@example.com", aid, false), ("Frank Black", "frank@example.com", aid, false),
        };
        var ids = new List<Guid>();
        for (var i = 0; i < people.Length; i++)
        {
            var p = people[i];
            var draft = await Json<CredentialResponse>(await Send(t, "/api/v1/credentials", registrar, HttpMethod.Post,
                new CreateCredentialRequest(p.Achievement, p.Name, p.Email, JsonSerializer.Serialize(new { roll = $"ROLL-2026-{i + 1:000}" }))));
            if (p.Approve) Assert.Equal(HttpStatusCode.OK, (await Send(t, $"/api/v1/credentials/{draft.Id}/approve", admin, HttpMethod.Post)).StatusCode);
            ids.Add(draft.Id);
        }

        // Dan's certificate: active but past its expiry date.
        await using var db = _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        await db.Credentials.Where(c => c.Id == ids[3]).ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresOn, DateTimeOffset.UtcNow.AddDays(-1)));
        return t with { RegistrarToken = registrar };
    }

    private TenantDbContext Db(Setup t) =>
        _factory.Services.GetRequiredService<TenantDbContextFactory>().CreateForSchema(t.TenantId, t.Slug, t.Schema);

    private static async Task<CredentialPageResponse> List(Setup t, string query, string? token = null) =>
        await Json<CredentialPageResponse>(await Send(t, $"/api/v1/credentials?{query}", token ?? t.AdminToken));

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
