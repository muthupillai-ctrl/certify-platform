using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Digitising paper certificates (ImportsController): reading CSV/Excel,
// the dry run, chunked import, batch approval by a second person, the paper
// date and number on the signed certificate, and the public paper look-up.
public class PaperImportTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PaperImportTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---------- reading files ----------

    [Fact]
    public async Task Parse_ReadsCsv_AndExcelWithRealDates()
    {
        var t = await SetUp();
        var csv = await Json<ImportParseResponse>(await Upload(t, "old.csv",
            Encoding.UTF8.GetBytes("Name,Email,Date,Number\r\nJane Doe,jane@example.com,12/03/2015,ACME/2015/0042\r\n,,,\r\nJohn Roe,,01/04/2016,ACME/2016/7\r\n")));
        Assert.Equal(["Name", "Email", "Date", "Number"], csv.Headers);
        Assert.Equal(2, csv.Rows.Count); // blank line skipped
        Assert.Equal("ACME/2015/0042", csv.Rows[0][3]);

        var xlsx = await Json<ImportParseResponse>(await Upload(t, "old.xlsx", MinimalXlsx()));
        Assert.Equal(["Name", "Date", "Number"], xlsx.Headers);
        Assert.Equal(["Jane Doe", "2015-03-12", "42"], xlsx.Rows[0]);

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(t, "old.pdf", [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(t, "old.csv", Encoding.UTF8.GetBytes("a\r\nb"), t.ApproverToken)).StatusCode);
    }

    // ---------- dry run ----------

    [Fact]
    public async Task Check_ReportsEveryProblem_WithoutCreatingAnything()
    {
        var t = await SetUp();
        var rows = new[]
        {
            Row("Jane Doe", "12/03/2015", "ACME/2015/1", roll: "R-1", passed: "01/03/2015"),
            Row("", "12/03/2015", "ACME/2015/2"),                          // no name
            Row("Bad Date", "31/02/2015", "ACME/2015/3"),                  // no such day
            Row("Future", "01/01/2099", "ACME/2015/4"),                    // future
            Row("Twin", "12/03/2015", "acme / 2015 / 1"),                  // same number as row 2
            Row("Bad Email", "12/03/2015", "ACME/2015/5", email: "not-an-email"),
            Row("No Email", "2015-03-12", "ACME/2015/6", email: ""),       // fine, with a warning
            Row("Field Date", "12/03/2015", "ACME/2015/7", passed: "32/13/2015"),
        };
        var check = await Json<ImportCheckResponse>(await Send(t, "/api/v1/imports/check", t.RegistrarToken, HttpMethod.Post,
            new ImportCheckRequest(t.TemplateId, "Fire Safety", "DMY", rows)));

        Assert.Equal((2, 6), (check.Ready, check.WithErrors));
        string Problem(int row) => string.Join(" ", check.Problems.Single(p => p.Row == row).Errors);
        Assert.Contains("name is missing", Problem(1));
        Assert.Contains("isn't a date", Problem(2));
        Assert.Contains("future", Problem(3));
        Assert.Contains("Same paper certificate number as row 2", Problem(4));
        Assert.Contains("not a valid email", Problem(5));
        Assert.Contains("Passed on", Problem(7));
        Assert.Contains("No email", check.Problems.Single(p => p.Row == 6).Warnings.Single());

        // Month-first reading of the same date.
        var mdy = await Json<ImportCheckResponse>(await Send(t, "/api/v1/imports/check", t.RegistrarToken, HttpMethod.Post,
            new ImportCheckRequest(t.TemplateId, "Fire Safety", "MDY", [Row("US Style", "03/12/2015", "US-1")])));
        Assert.Equal(1, mdy.Ready);

        var list = await Json<CredentialPageResponse>(await Send(t, "/api/v1/credentials", t.AdminToken));
        Assert.Equal(0, list.Total);
    }

    // ---------- import, approve, verify ----------

    [Fact]
    public async Task Import_ThenASecondPersonApproves_TheBatch_SignedWithThePaperDetails()
    {
        var t = await SetUp();
        var batch = await ImportBatch(t, [
            Row("Jane Doe", "12/03/2015", "ACME/2015/0042", roll: "R-42", passed: "01/03/2015"),
            Row("R. K. Sharma", "01/04/2016", "ACME/2016/7", email: ""),
            Row("Asha Rao", "05/05/2017", null),
        ]);
        Assert.Equal(("PendingApproval", 3), (batch.Status, batch.Pending));

        // Waiting, and only as a batch.
        Assert.Equal(1, (await Json<CredentialSummaryCountsResponse>(await Send(t, "/api/v1/credentials/summary", t.ApproverToken))).ImportsForMe);
        Assert.Equal(0, (await Json<CredentialSummaryCountsResponse>(await Send(t, "/api/v1/credentials/summary", t.ApproverToken))).PendingForMe);
        var page = await Json<CredentialPageResponse>(await Send(t, $"/api/v1/credentials?importBatchId={batch.Id}", t.AdminToken));
        Assert.All(page.Items, i => Assert.Equal("Import", i.Source));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(t, $"/api/v1/credentials/{page.Items[0].Id}/approve", t.ApproverToken, HttpMethod.Post)).StatusCode);
        Assert.Equal("ACME/2015/0042", Assert.Single((await Json<CredentialPageResponse>(await Send(t, "/api/v1/credentials?q=2015%2F0042", t.AdminToken))).Items).LegacyCertificateNumber);

        // Maker–checker and the typed count.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(t, $"/api/v1/imports/{batch.Id}/approve", t.RegistrarToken, HttpMethod.Post, new ApproveImportRequest(3))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, $"/api/v1/imports/{batch.Id}/approve", t.ApproverToken, HttpMethod.Post, new ApproveImportRequest(2))).StatusCode);

        var progress = await Json<ImportProgressResponse>(await Send(t, $"/api/v1/imports/{batch.Id}/approve", t.ApproverToken, HttpMethod.Post, new ApproveImportRequest(3)));
        Assert.Equal(("Approved", 3, 0), (progress.Status, progress.Done, progress.Remaining));

        // The signed certificate carries both dates.
        var jane = (await Json<CredentialPageResponse>(await Send(t, "/api/v1/credentials?q=Jane", t.AdminToken))).Items.Single();
        var verify = await Json<VerifyCredentialResponse>(await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{jane.Id}"));
        Assert.Equal(("Active", new DateOnly(2015, 3, 12), "ACME/2015/0042"), (verify.Status, verify.OriginalIssueDate, verify.LegacyCertificateNumber));
        Assert.True(verify.IssuedAt > DateTimeOffset.UtcNow.AddMinutes(-5)); // digitised today
        Assert.Contains("\"passed\":\"2015-03-01\"", verify.FieldValuesJson);

        // The portable credential says when it was really awarded; no email identifier when there's none.
        var vc = await Claims(t, jane.Id);
        Assert.Equal("2015-03-12T00:00:00Z", vc.GetProperty("vc").GetProperty("awardedDate").GetString());
        var sharma = (await Json<CredentialPageResponse>(await Send(t, "/api/v1/credentials?q=Sharma", t.AdminToken))).Items.Single();
        var identifiers = (await Claims(t, sharma.Id)).GetProperty("vc").GetProperty("credentialSubject").GetProperty("identifier");
        Assert.Equal(1, identifiers.GetArrayLength());

        var detail = await Json<ImportBatchDetailResponse>(await Send(t, $"/api/v1/imports/{batch.Id}", t.AdminToken));
        Assert.Equal((0, 3, new DateOnly(2015, 3, 12)), (detail.Batch.Pending, detail.Batch.Issued, detail.Batch.EarliestPaperDate!.Value));
    }

    [Fact]
    public async Task Reject_ReturnsTheNumbers_AndTheImporterCanWithdraw()
    {
        var t = await SetUp();
        var batch = await ImportBatch(t, [Row("Jane Doe", "12/03/2015", "ACME/2015/0042")]);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(t, $"/api/v1/imports/{batch.Id}/reject", t.ApproverToken, HttpMethod.Post, new RejectImportRequest(" "))).StatusCode);
        var rejected = await Json<ImportProgressResponse>(await Send(t, $"/api/v1/imports/{batch.Id}/reject", t.ApproverToken, HttpMethod.Post, new RejectImportRequest("Wrong year")));
        Assert.Equal(("Rejected", 0), (rejected.Status, rejected.Remaining));

        // The paper number is free again, and a new import can use it.
        var check = await Json<ImportCheckResponse>(await Send(t, "/api/v1/imports/check", t.RegistrarToken, HttpMethod.Post,
            new ImportCheckRequest(t.TemplateId, "Fire Safety", "DMY", [Row("Jane Doe", "12/03/2016", "ACME/2015/0042")])));
        Assert.Equal(1, check.Ready);

        // The importer withdraws their own, even mid-upload.
        var created = await Json<ImportBatchResponse>(await Send(t, "/api/v1/imports", t.RegistrarToken, HttpMethod.Post, new CreateImportBatchRequest(t.TemplateId, "second.csv", 1)));
        var withdrawn = await Json<ImportProgressResponse>(await Send(t, $"/api/v1/imports/{created.Id}/reject", t.RegistrarToken, HttpMethod.Post, new RejectImportRequest(null)));
        Assert.Equal("Withdrawn", withdrawn.Status);
    }

    [Fact]
    public async Task ImportingTheSameNumberTwice_IsRefused_AndLikelyDuplicatesCanBeSkipped()
    {
        var t = await SetUp();
        await ImportBatch(t, [Row("Jane Doe", "12/03/2015", "ACME/2015/0042")]);

        var check = await Json<ImportCheckResponse>(await Send(t, "/api/v1/imports/check", t.RegistrarToken, HttpMethod.Post,
            new ImportCheckRequest(t.TemplateId, "Fire Safety", "DMY", [
                Row("Jane Doe", "12/03/2015", "ACME/2015/0042"),  // number taken
                Row("Jane Doe", "12/03/2015", null),               // same person/achievement/date
            ])));
        Assert.Equal((1, 1, 1), (check.Ready, check.WithErrors, check.LikelyDuplicates));
        Assert.Contains("already on a certificate", check.Problems.Single(p => p.Row == 0).Errors.Single());

        var created = await Json<ImportBatchResponse>(await Send(t, "/api/v1/imports", t.RegistrarToken, HttpMethod.Post, new CreateImportBatchRequest(t.TemplateId, "again.csv", 2)));
        var result = await Json<ImportRowsResponse>(await Send(t, $"/api/v1/imports/{created.Id}/rows", t.RegistrarToken, HttpMethod.Post,
            new ImportRowsRequest("Fire Safety", "DMY", true, [Row("Jane Doe", "12/03/2015", "ACME/2015/0042"), Row("Jane Doe", "12/03/2015", null)], 0)));
        Assert.Equal((0, 2), (result.Imported, result.Skipped)); // taken number + likely duplicate
        Assert.Contains("Skipped: already imported.", result.Problems.Single(p => p.Row == 1).Errors);
    }

    // ---------- public paper look-up ----------

    [Fact]
    public async Task PaperLookup_NeedsTheNumberAndTheNameAsPrinted()
    {
        var t = await SetUp();
        var batch = await ImportBatch(t, [Row("R. K. Sharma", "01/04/2016", "ACME/2016/7"), Row("Jane Mary Doe", "12/03/2015", "ACME/2015/0042")]);
        await Json<ImportProgressResponse>(await Send(t, $"/api/v1/imports/{batch.Id}/approve", t.ApproverToken, HttpMethod.Post, new ApproveImportRequest(2)));

        async Task<HttpStatusCode> Lookup(string number, string name) =>
            (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/lookup-paper?number={Uri.EscapeDataString(number)}&name={Uri.EscapeDataString(name)}")).StatusCode;

        Assert.Equal(HttpStatusCode.OK, await Lookup("acme/2016/7", "Sharma"));            // initials optional
        Assert.Equal(HttpStatusCode.OK, await Lookup("ACME / 2015 / 0042", "jane mary doe"));
        Assert.Equal(HttpStatusCode.NotFound, await Lookup("ACME/2015/0042", "Jane Doe")); // "Mary" missing
        Assert.Equal(HttpStatusCode.NotFound, await Lookup("ACME/2016/7", "Rahul Sharma")); // not on record
        Assert.Equal(HttpStatusCode.NotFound, await Lookup("ACME/2016/8", "Sharma"));
        Assert.Equal(HttpStatusCode.NotFound, await Lookup("ACME/2016/7", ""));
    }

    // ---------- helpers ----------

    private static ImportRow Row(string name, string date, string? number, string? email = null, string? roll = null, string? passed = null) =>
        new(name, email ?? $"{name.Split(' ')[0].ToLowerInvariant()}@example.com", null, date, number, null,
            new Dictionary<string, string?> { ["roll"] = roll, ["passed"] = passed });

    // Registrar imports rows and sends the batch for approval.
    private async Task<ImportBatchResponse> ImportBatch(Setup t, ImportRow[] rows)
    {
        var created = await Json<ImportBatchResponse>(await Send(t, "/api/v1/imports", t.RegistrarToken, HttpMethod.Post,
            new CreateImportBatchRequest(t.TemplateId, "old-certificates.xlsx", rows.Length)));
        for (var i = 0; i < rows.Length; i += 2)
        {
            var chunk = rows.Skip(i).Take(2).ToList();
            var result = await Json<ImportRowsResponse>(await Send(t, $"/api/v1/imports/{created.Id}/rows", t.RegistrarToken, HttpMethod.Post,
                new ImportRowsRequest("Fire Safety", "DMY", false, chunk, i)));
            Assert.Equal(chunk.Count, result.Imported);
        }
        return await Json<ImportBatchResponse>(await Send(t, $"/api/v1/imports/{created.Id}/finish", t.RegistrarToken, HttpMethod.Post));
    }

    private async Task<JsonElement> Claims(Setup t, Guid credentialId)
    {
        var jwt = await (await t.Client.GetAsync($"/api/v1/verify/{t.Slug}/{credentialId}/credential")).Content.ReadAsStringAsync();
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }

    private record Setup(HttpClient Client, string Slug, string AdminToken, string RegistrarToken, string ApproverToken, Guid TemplateId);

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
        var t = new Setup(client, tenant.Slug, "", "", "", Guid.Empty);
        var admin = await Login(t, $"admin@{suffix}.test", "TenantAdmin123!");
        t = t with { AdminToken = admin };
        foreach (var role in new[] { "Registrar", "Approver" })
            await Send(t, "/api/v1/users", admin, HttpMethod.Post, new CreateUserRequest($"{role.ToLowerInvariant()}@{suffix}.test", role, "Password123!", role));
        const string schema = """[{"key":"roll","label":"Roll number","type":"text"},{"key":"passed","label":"Passed on","type":"date"}]""";
        var template = await Json<JsonElement>(await Send(t, "/api/v1/certificate-templates", admin, HttpMethod.Post,
            new CreateCertificateTemplateRequest("Paper certificate", null, "Certificate", schema)));
        return t with
        {
            RegistrarToken = await Login(t, $"registrar@{suffix}.test", "Password123!"),
            ApproverToken = await Login(t, $"approver@{suffix}.test", "Password123!"),
            TemplateId = template.GetProperty("id").GetGuid(),
        };
    }

    private static Task<HttpResponseMessage> Upload(Setup t, string fileName, byte[] bytes, string? token = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/imports/parse") { Content = content };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? t.RegistrarToken);
        return t.Client.SendAsync(request);
    }

    // The smallest .xlsx Excel would open: a header row and one row whose
    // date is a real date cell (serial 42075 = 12 Mar 2015, date format).
    private static byte[] MinimalXlsx()
    {
        var files = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""",
            ["_rels/.rels"] = """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""",
            ["xl/workbook.xml"] = """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""",
            ["xl/_rels/workbook.xml.rels"] = """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""",
            ["xl/styles.xml"] = """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs></styleSheet>""",
            ["xl/worksheets/sheet1.xml"] = """<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Name</t></is></c><c r="B1" t="inlineStr"><is><t>Date</t></is></c><c r="C1" t="inlineStr"><is><t>Number</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>Jane Doe</t></is></c><c r="B2" s="1"><v>42075</v></c><c r="C2"><v>42</v></c></row></sheetData></worksheet>""",
        };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(xml);
            }
        }
        return memory.ToArray();
    }

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
