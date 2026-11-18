using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Email;
using Certyfi.Api.Controllers;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Certyfi.IntegrationTests;

// Students and the student portal: a partner app's request creates (or
// finds) the student and a certificate awaiting payment, the student sets a
// password from the portal link, pays (dummy) and the certificate goes to an
// approver; staff certificates link to students by email.
public class StudentPortalTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string FieldSchemaJson = """[{"key":"rollNumber","label":"Roll Number","type":"text"}]""";
    private const string StudentPassword = "Student-Pass-2026";

    private readonly CustomWebApplicationFactory _factory;

    public StudentPortalTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PartnerRequest_AwaitsPayment_ThenStudentPays_ThenApproverIssues()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);

        var issued = await Issue(t, key, "S-1001", "ada@example.com", "enrol-1");
        Assert.Equal(HttpStatusCode.Created, issued.Status);
        Assert.Equal("AwaitingPayment", issued.Body.Status);
        Assert.Equal("S-1001", issued.Body.StudentId);
        var requested = await LastEmailTo("ada@example.com");
        Assert.Contains("Course Completion", requested.Subject);
        Assert.Contains("complete the payment", requested.Text);
        var portalUrl = PortalUrlIn(requested);
        Assert.Contains("/student/welcome/", portalUrl);

        // Not approvable before payment.
        Assert.Equal(HttpStatusCode.Conflict, (await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Body.Id}/approve", t.TokenApprover)).StatusCode);

        var link = Token(portalUrl);
        var welcome = await Json<StudentWelcomeResponse>(await Anonymous(t, HttpMethod.Get, $"/api/v1/student-portal/welcome/{link}"));
        Assert.Equal("Ada Lovelace", welcome.FullName);
        Assert.False(welcome.AlreadySetUp);

        var signedIn = await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/setup", new StudentSetupRequest(link, StudentPassword)));
        Assert.Equal("ada@example.com", signedIn.Student.Email);
        Assert.Equal("S-1001", signedIn.Student.StudentNumber);
        Assert.Equal(HttpStatusCode.Conflict, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/setup", new StudentSetupRequest(link, StudentPassword))).StatusCode);

        var mine = await Json<List<StudentCertificateResponse>>(await AsStudent(t, signedIn.AccessToken, HttpMethod.Get, "/api/v1/student-portal/certificates"));
        var cert = Assert.Single(mine);
        Assert.Equal("AwaitingPayment", cert.Status);
        Assert.Null(cert.VerificationPath);

        var paid = await Json<StudentCertificateResponse>(await AsStudent(t, signedIn.AccessToken, HttpMethod.Post, $"/api/v1/student-portal/certificates/{cert.Id}/pay"));
        Assert.Equal("PendingApproval", paid.Status);
        Assert.NotNull(paid.PaidAt);
        Assert.Equal(HttpStatusCode.Conflict, (await AsStudent(t, signedIn.AccessToken, HttpMethod.Post, $"/api/v1/student-portal/certificates/{cert.Id}/pay")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{cert.Id}/approve", t.TokenApprover)).StatusCode);
        var after = Assert.Single(await Json<List<StudentCertificateResponse>>(await AsStudent(t, signedIn.AccessToken, HttpMethod.Get, "/api/v1/student-portal/certificates")));
        Assert.Equal("Active", after.Status);
        Assert.Equal($"/verify/{t.Slug}/{cert.Id}", after.VerificationPath);
        var issuedEmail = await LastEmailTo("ada@example.com");
        Assert.StartsWith("Your certificate is ready", issuedEmail.Subject);
        Assert.Contains($"https://certyfi.test/verify/{t.Slug}/{cert.Id}", issuedEmail.Text);

        // Next certificate for the same student: same student, and the link is
        // now the sign-in page, since they've set a password.
        var second = await Issue(t, key, "S-1001", "ada@example.com", "enrol-2");
        Assert.EndsWith($"/student/{t.Slug}/login", PortalUrlIn(await LastEmailTo("ada@example.com")));
        var login = await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("ADA@example.com", StudentPassword)));
        Assert.Equal(2, (await Json<List<StudentCertificateResponse>>(await AsStudent(t, login.AccessToken, HttpMethod.Get, "/api/v1/student-portal/certificates"))).Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("ada@example.com", "wrong-password-1"))).StatusCode);
    }

    [Fact]
    public async Task AutoIssueKey_WithPayment_IssuesWhenTheStudentPays()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: true);
        var issued = await Issue(t, key, "S-2", "grace@example.com", "x-1");
        Assert.Equal("AwaitingPayment", issued.Body.Status);

        var student = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("grace@example.com")));
        var paid = await Json<StudentCertificateResponse>(await AsStudent(t, student, HttpMethod.Post, $"/api/v1/student-portal/certificates/{issued.Body.Id}/pay"));
        Assert.Equal("Active", paid.Status);
        Assert.NotNull(paid.VerificationCode);
    }

    [Fact]
    public async Task Students_AreMatchedByEmail_AndPartnerIdsMustAgree()
    {
        var t = await SetUpTenant();

        // A staff-made certificate creates the student by email.
        var staffMade = await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.TokenAdmin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Staff Award", "Lin Wei", "Lin.Wei@example.com", null)));
        Assert.NotNull(staffMade.StudentId);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().SendDueAsync(CancellationToken.None);
        }
        Assert.DoesNotContain(_factory.Services.GetRequiredService<MemoryEmailTransport>().Sent, e => e.To == "lin.wei@example.com");

        // The partner's id is attached to that student on first sight.
        var key = await CreateKey(t, requiresPayment: false, autoIssue: false);
        var issued = await Issue(t, key, "LMS-77", "lin.wei@example.com", "p-1", name: "Lin Wei");
        Assert.Equal("PendingApproval", issued.Body.Status);
        Assert.Equal("LMS-77", issued.Body.StudentId);

        var list = await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=lin", t.TokenAdmin));
        var row = Assert.Single(list.Items);
        Assert.Equal("LMS-77", row.StudentNumber);
        Assert.Equal(2, row.CertificateCount);

        // An email already tied to another id; a known id moved onto another student's email.
        Assert.Equal(HttpStatusCode.Conflict, (await IssueRaw(t, key, "LMS-78", "lin.wei@example.com", "p-3")).StatusCode);
        await Issue(t, key, "LMS-90", "other.student@example.com", "p-4", name: "Other Student");
        Assert.Equal(HttpStatusCode.Conflict, (await IssueRaw(t, key, "LMS-77", "other.student@example.com", "p-5")).StatusCode);
    }

    [Fact]
    public async Task StudentTokens_SeeOnlyTheirOwn_AndCannotUseStaffEndpoints()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var a = await Issue(t, key, "A-1", "a@example.com", "a-1");
        var b = await Issue(t, key, "B-1", "b@example.com", "b-1");
        var tokenA = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("a@example.com")));

        Assert.Single(await Json<List<StudentCertificateResponse>>(await AsStudent(t, tokenA, HttpMethod.Get, "/api/v1/student-portal/certificates")));
        Assert.Equal(HttpStatusCode.NotFound, (await AsStudent(t, tokenA, HttpMethod.Post, $"/api/v1/student-portal/certificates/{b.Body.Id}/pay")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsStudent(t, tokenA, HttpMethod.Get, "/api/v1/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsStudent(t, tokenA, HttpMethod.Get, "/api/v1/students")).StatusCode);

        var other = await SetUpTenant();
        var crossTenant = new HttpRequestMessage(HttpMethod.Get, "/api/v1/student-portal/certificates");
        crossTenant.Headers.Add("X-Tenant-Slug", other.Slug);
        crossTenant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Client.SendAsync(crossTenant)).StatusCode);

        // A link only works at its own institution.
        var linkB = Token(PortalUrlIn(await LastEmailTo("b@example.com")));
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous(other, HttpMethod.Get, $"/api/v1/student-portal/welcome/{linkB}")).StatusCode);
    }

    [Fact]
    public async Task Links_Expire_AndStaffResetReplacesThem()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var issued = await Issue(t, key, "C-1", "carol@example.com", "c-1");
        var welcomeUrl = PortalUrlIn(await LastEmailTo("carol@example.com"));
        var oldLink = Token(welcomeUrl);
        await SetUpStudent(t, welcomeUrl);

        Guid studentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await platform.Tenants.FirstAsync(x => x.Slug == t.Slug);
            await using var tenantDb = db.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            var student = await tenantDb.Students.FirstAsync(s => s.Email == "carol@example.com");
            studentId = student.Id;

            var expired = scope.ServiceProvider.GetRequiredService<StudentPortalLink>().Issue(t.Slug, student, StudentPortalLink.Purpose.Welcome, DateTimeOffset.UtcNow.AddDays(-31));
            Assert.Equal(HttpStatusCode.Gone, (await Anonymous(t, HttpMethod.Get, $"/api/v1/student-portal/welcome/{expired}")).StatusCode);
        }

        // Forgot password: staff reset gives a fresh welcome link; the old one is dead.
        var reset = await Json<StudentSignInLinkResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/students/{studentId}/sign-in-link", t.TokenAdmin, new StudentSignInLinkRequest(true)));
        Assert.True(reset.IsWelcomeLink);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous(t, HttpMethod.Get, $"/api/v1/student-portal/welcome/{oldLink}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("carol@example.com", StudentPassword))).StatusCode);
        await SetUpStudent(t, reset.Url);

        var detail = await Json<StudentDetailResponse>(await Staff(t, HttpMethod.Get, $"/api/v1/students/{studentId}", t.TokenAdmin));
        Assert.True(detail.Student.SignInSetUp);
        Assert.Equal("AwaitingPayment", Assert.Single(detail.Certificates).Status);
    }

    [Fact]
    public async Task ErasingAStudentsOnlyCertificate_RemovesTheStudent()
    {
        var t = await SetUpTenant();
        var created = await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.TokenAdmin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Award", "Erin Okafor", "erin@example.com", null)));
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{created.Id}/reject", t.TokenApprover, new RejectCredentialRequest("Wrong course")));

        var erin = Assert.Single((await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=erin", t.TokenAdmin))).Items);
        Assert.True((await Json<StudentEmailLinkResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/students/{erin.Id}/email-link", t.TokenAdmin))).Queued);
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{created.Id}/erase-personal-data", t.TokenAdmin,
            new ErasePersonalDataRequest(created.VerificationCode)));
        Assert.Equal(0, (await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=erin", t.TokenAdmin))).Total);
        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().EmailOutbox.AnyAsync(e => e.StudentId == erin.Id));
    }

    [Fact]
    public async Task ForgotPassword_EmailsAOneTimeResetLink()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        await Issue(t, key, "D-1", "dami@example.com", "d-1", name: "Dami Adeyemi");
        await SetUpStudent(t, PortalUrlIn(await LastEmailTo("dami@example.com")));

        Assert.Equal(HttpStatusCode.Accepted, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/forgot-password", new StudentForgotPasswordRequest("DAMI@example.com"))).StatusCode);
        var reset = await LastEmailTo("dami@example.com");
        Assert.StartsWith("Reset your", reset.Subject);
        var token = Token(PortalUrlIn(reset));

        var page = await Json<StudentWelcomeResponse>(await Anonymous(t, HttpMethod.Get, $"/api/v1/student-portal/welcome/{token}"));
        Assert.True(page.IsReset);
        Assert.False(page.AlreadySetUp);
        await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/setup", new StudentSetupRequest(token, "A-Brand-New-Pass-1")));

        // Works once; the old password is gone, the new one works.
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/setup", new StudentSetupRequest(token, "Another-New-Pass-2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("dami@example.com", StudentPassword))).StatusCode);
        await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("dami@example.com", "A-Brand-New-Pass-1")));

        // Unknown emails get the same answer and no email.
        var before = _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/forgot-password", new StudentForgotPasswordRequest("nobody@example.com"))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().SendDueAsync(CancellationToken.None);
        }
        Assert.DoesNotContain(_factory.Services.GetRequiredService<MemoryEmailTransport>().Sent, e => e.To == "nobody@example.com");
    }

    [Fact]
    public async Task StaffCanEmailASetUpLink_AndSentEmailsKeepNoBody()
    {
        var t = await SetUpTenant();
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.TokenAdmin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Old Award", "Femi Bello", "femi@example.com", null)));
        var femi = Assert.Single((await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=femi", t.TokenAdmin))).Items);

        Assert.True((await Json<StudentEmailLinkResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/students/{femi.Id}/email-link", t.TokenAdmin))).Queued);
        var email = await LastEmailTo("femi@example.com");
        Assert.StartsWith("Set up your", email.Subject);
        Assert.Contains("Femi Bello", email.Html);
        await SetUpStudent(t, PortalUrlIn(email));

        using var scope = _factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().EmailOutbox.SingleAsync(e => e.StudentId == femi.Id);
        Assert.Equal(Certyfi.Domain.Entities.EmailStatus.Sent, record.Status);
        Assert.Null(record.HtmlBody);
        Assert.Null(record.TextBody);
        Assert.Equal(EmailTemplates.StudentSignInLink, record.Kind);
    }

    [Fact]
    public async Task PartnerCancel_StopsPaymentAndApproval_AndTellsTheStudent()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);

        // Unpaid: cancelled, can't be paid, student emailed, shown as Cancelled.
        var unpaid = await Issue(t, key, "X-1", "xia@example.com", "x-1", name: "Xia Lin");
        var student = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("xia@example.com")));
        Assert.Equal(HttpStatusCode.Conflict, (await PartnerPost(t, key, $"/api/v1/partner/credentials/{unpaid.Body.Id}/revoke", new PartnerRevokeCredentialRequest("Enrolment cancelled"))).StatusCode);
        var cancelled = await Json<PartnerCredentialResponse>(await PartnerPost(t, key, $"/api/v1/partner/credentials/{unpaid.Body.Id}/cancel", new PartnerCancelCredentialRequest("Enrolment cancelled")));
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal("Cancelled", (await Json<PartnerCredentialResponse>(await PartnerPost(t, key, $"/api/v1/partner/credentials/{unpaid.Body.Id}/cancel", new PartnerCancelCredentialRequest("again")))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await AsStudent(t, student, HttpMethod.Post, $"/api/v1/student-portal/certificates/{unpaid.Body.Id}/pay")).StatusCode);
        Assert.Equal("Cancelled", Assert.Single(await Json<List<StudentCertificateResponse>>(await AsStudent(t, student, HttpMethod.Get, "/api/v1/student-portal/certificates"))).Status);
        var email = await LastEmailTo("xia@example.com");
        Assert.StartsWith("Certificate request cancelled", email.Subject);
        Assert.Contains("No payment is needed", email.Text);

        // Paid, waiting for approval: cancelled, can't be approved; email mentions the payment.
        var paid = await Issue(t, key, "X-1", "xia@example.com", "x-2", name: "Xia Lin");
        await Json<StudentCertificateResponse>(await AsStudent(t, student, HttpMethod.Post, $"/api/v1/student-portal/certificates/{paid.Body.Id}/pay"));
        await Json<PartnerCredentialResponse>(await PartnerPost(t, key, $"/api/v1/partner/credentials/{paid.Body.Id}/cancel", new PartnerCancelCredentialRequest("Duplicate")));
        Assert.Equal(HttpStatusCode.Conflict, (await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{paid.Body.Id}/approve", t.TokenApprover)).StatusCode);
        Assert.Contains("already paid", (await LastEmailTo("xia@example.com")).Text);

        // Issued: revoke, not cancel. Another key's credential: 404.
        var issued = await Issue(t, key, "X-1", "xia@example.com", "x-3", name: "Xia Lin");
        await Json<StudentCertificateResponse>(await AsStudent(t, student, HttpMethod.Post, $"/api/v1/student-portal/certificates/{issued.Body.Id}/pay"));
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Body.Id}/approve", t.TokenApprover));
        Assert.Equal(HttpStatusCode.Conflict, (await PartnerPost(t, key, $"/api/v1/partner/credentials/{issued.Body.Id}/cancel", new PartnerCancelCredentialRequest("Too late"))).StatusCode);
        var otherKey = await CreateKey(t, requiresPayment: true, autoIssue: false);
        Assert.Equal(HttpStatusCode.NotFound, (await PartnerPost(t, otherKey, $"/api/v1/partner/credentials/{paid.Body.Id}/cancel", new PartnerCancelCredentialRequest("Not mine"))).StatusCode);

        // A cancelled certificate's personal data can be erased.
        var staffView = await Json<CredentialResponse>(await Staff(t, HttpMethod.Get, $"/api/v1/credentials/{unpaid.Body.Id}", t.TokenAdmin));
        Assert.Equal("Enrolment cancelled", staffView.CancellationReason);
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{unpaid.Body.Id}/erase-personal-data", t.TokenAdmin,
            new ErasePersonalDataRequest(staffView.VerificationCode)));
    }

    [Fact]
    public async Task RejectingAfterPayment_EmailsTheStudentWithReasonAndRefundNote()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var issued = await Issue(t, key, "J-1", "joy@example.com", "j-1", name: "Joy Mensah");
        var student = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("joy@example.com")));
        await Json<StudentCertificateResponse>(await AsStudent(t, student, HttpMethod.Post, $"/api/v1/student-portal/certificates/{issued.Body.Id}/pay"));

        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Body.Id}/reject", t.TokenApprover,
            new RejectCredentialRequest("Course not completed")));

        var email = await LastEmailTo("joy@example.com");
        Assert.StartsWith("Your certificate wasn't approved", email.Subject);
        Assert.Contains("Reason: Course not completed", email.Text);
        Assert.Contains("Contact Tenant", email.Text);
        Assert.Contains("about a refund", email.Text);
        Assert.EndsWith($"/student/{t.Slug}/login", PortalUrlIn(email));

        var mine = Assert.Single(await Json<List<StudentCertificateResponse>>(await AsStudent(t, student, HttpMethod.Get, "/api/v1/student-portal/certificates")));
        Assert.Equal("Rejected", mine.Status);
        Assert.Equal("Course not completed", mine.RejectionReason);
    }

    [Fact]
    public async Task SuspendReactivateAndRevoke_EmailTheStudent_WithoutQuotingReasons()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: false, autoIssue: true);
        var issued = await Issue(t, key, "K-1", "kofi@example.com", "k-1", name: "Kofi Asante");
        Assert.Equal("Active", issued.Body.Status);
        Assert.StartsWith("Your certificate is ready", (await LastEmailTo("kofi@example.com")).Subject);
        var code = issued.Body.VerificationCode;
        var verifyUrl = $"https://certyfi.test/verify/{t.Slug}/{issued.Body.Id}";

        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Body.Id}/suspend", t.TokenAdmin,
            new SuspendCredentialRequest("Secret investigation note", code)));
        var suspended = await LastEmailTo("kofi@example.com");
        Assert.StartsWith("Your certificate has been suspended", suspended.Subject);
        Assert.Contains(verifyUrl, suspended.Text);
        Assert.DoesNotContain("Secret investigation note", suspended.Text);

        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Body.Id}/reactivate", t.TokenAdmin,
            new ReactivateCredentialRequest("Cleared", code)));
        Assert.StartsWith("Your certificate is valid again", (await LastEmailTo("kofi@example.com")).Subject);

        // Revoke: the partner asks, a person approves, then the student is told.
        Assert.Equal(HttpStatusCode.Accepted, (await PartnerPost(t, key, $"/api/v1/partner/credentials/{issued.Body.Id}/revoke",
            new PartnerRevokeCredentialRequest("Enrolment fraud"))).StatusCode);
        Assert.StartsWith("Your certificate is valid again", (await LastEmailTo("kofi@example.com")).Subject);
        var request = Assert.Single(await Json<List<RevocationRequestResponse>>(await Staff(t, HttpMethod.Get, $"/api/v1/revocation-requests?credentialId={issued.Body.Id}", t.TokenApprover)));
        await Json<RevocationRequestResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/revocation-requests/{request.Id}/approve", t.TokenApprover,
            new ApproveRevocationRequest(code)));
        var revoked = await LastEmailTo("kofi@example.com");
        Assert.StartsWith("Your certificate has been revoked", revoked.Subject);
        Assert.DoesNotContain("fraud", revoked.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentDeadline_RemindsOnce_ThenCancelsUnpaid_AndLeavesPaidAlone()
    {
        var t = await SetUpTenant();
        Assert.Equal(14, (await Json<TenantPaymentSettings>(await Staff(t, HttpMethod.Get, "/api/v1/tenant-settings/payments", t.TokenApprover))).PaymentDays);
        Assert.Equal(HttpStatusCode.Forbidden, (await Staff(t, HttpMethod.Put, "/api/v1/tenant-settings/payments", t.TokenApprover, new TenantPaymentSettings(5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Put, "/api/v1/tenant-settings/payments", t.TokenAdmin, new TenantPaymentSettings(-1))).StatusCode);
        await Json<TenantPaymentSettings>(await Staff(t, HttpMethod.Put, "/api/v1/tenant-settings/payments", t.TokenAdmin, new TenantPaymentSettings(5)));

        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var unpaid = await Issue(t, key, "P-1", "pat@example.com", "p-1", name: "Pat Obi");
        var paid = await Issue(t, key, "Q-1", "quin@example.com", "q-1", name: "Quin Ade");
        var created = DateTimeOffset.UtcNow;
        Assert.Equal(5, unpaid.Body.PaymentDays);
        Assert.InRange(unpaid.Body.PaymentDueAt!.Value, created.AddDays(5).AddMinutes(-2), created.AddDays(5).AddMinutes(1));
        Assert.Contains("complete the payment by", (await LastEmailTo("pat@example.com")).Text);
        var quin = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("quin@example.com")));
        await Json<StudentCertificateResponse>(await AsStudent(t, quin, HttpMethod.Post, $"/api/v1/student-portal/certificates/{paid.Body.Id}/pay"));

        var runner = _factory.Services.GetRequiredService<PaymentDeadlineRunner>();
        // 5-day period: reminder halfway (day 2.5), as that's later than 3 days before.
        await runner.RunAsync(created.AddDays(2), CancellationToken.None);
        Assert.DoesNotContain("Reminder", (await LastEmailTo("pat@example.com")).Subject);
        await runner.RunAsync(created.AddDays(2.6), CancellationToken.None);
        var reminder = await LastEmailTo("pat@example.com");
        Assert.StartsWith("Reminder: pay for your certificate by", reminder.Subject);
        var sentBefore = _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent.Count(e => e.To == "pat@example.com");
        await runner.RunAsync(created.AddDays(3), CancellationToken.None);
        await LastEmailTo("pat@example.com");
        Assert.Equal(sentBefore, _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent.Count(e => e.To == "pat@example.com"));

        await runner.RunAsync(created.AddDays(5).AddMinutes(5), CancellationToken.None);
        var after = await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{unpaid.Body.Id}"));
        Assert.Equal("Cancelled", after.Status);
        Assert.True(after.CancelledForNonPayment);
        Assert.Equal(PaymentDeadlineRunner.CancellationReason, after.CancellationReason);
        Assert.StartsWith("Certificate request cancelled (not paid)", (await LastEmailTo("pat@example.com")).Subject);
        Assert.Equal("PendingApproval", (await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{paid.Body.Id}"))).Status);
    }

    [Fact]
    public async Task NoDeadlineWhenDaysIsZero_AndPayingAfterTheDeadlineIsRefused()
    {
        var t = await SetUpTenant();
        await Json<TenantPaymentSettings>(await Staff(t, HttpMethod.Put, "/api/v1/tenant-settings/payments", t.TokenAdmin, new TenantPaymentSettings(0)));
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var open = await Issue(t, key, "Z-1", "zara@example.com", "z-1", name: "Zara Bello");
        Assert.Null(open.Body.PaymentDueAt);
        await _factory.Services.GetRequiredService<PaymentDeadlineRunner>().RunAsync(DateTimeOffset.UtcNow.AddYears(1), CancellationToken.None);
        Assert.Equal("AwaitingPayment", (await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{open.Body.Id}"))).Status);

        // A certificate whose deadline has just passed (before the job ran) can't be paid.
        await Json<TenantPaymentSettings>(await Staff(t, HttpMethod.Put, "/api/v1/tenant-settings/payments", t.TokenAdmin, new TenantPaymentSettings(14)));
        var late = await Issue(t, key, "Z-1", "zara@example.com", "z-2", name: "Zara Bello");
        using (var scope = _factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await platform.Tenants.FirstAsync(x => x.Slug == t.Slug);
            await using var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>().CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            await tenantDb.Credentials.Where(c => c.Id == late.Body.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.PaymentDueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var zara = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("zara@example.com")));
        Assert.Equal(HttpStatusCode.Conflict, (await AsStudent(t, zara, HttpMethod.Post, $"/api/v1/student-portal/certificates/{late.Body.Id}/pay")).StatusCode);
    }

    [Fact]
    public async Task LmsSendingANewEmail_UpdatesTheStudent_AndTellsTheOldAddress()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var first = await Issue(t, key, "M-1", "mo.old@example.com", "m-1", name: "Mo Bello");
        await SetUpStudent(t, PortalUrlIn(await LastEmailTo("mo.old@example.com")));

        var second = await Issue(t, key, "M-1", "Mo.New@example.com", "m-2", name: "Mohammed Bello");
        Assert.Equal(HttpStatusCode.Created, second.Status);
        var notice = await LastEmailTo("mo.old@example.com");
        Assert.Contains("sign-in email was changed", notice.Subject);
        Assert.Contains("m•••@example.com", notice.Text);
        Assert.DoesNotContain("mo.new@example.com", notice.Text);
        Assert.StartsWith("Action needed: pay for your certificate", (await LastEmailTo("mo.new@example.com")).Subject);

        // Same password, new email; the old one no longer signs in.
        await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("mo.new@example.com", StudentPassword)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("mo.old@example.com", StudentPassword))).StatusCode);

        var row = Assert.Single((await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=M-1", t.TokenAdmin))).Items);
        Assert.Equal("Mohammed Bello", row.FullName);
        var detail = await Json<StudentDetailResponse>(await Staff(t, HttpMethod.Get, $"/api/v1/students/{row.Id}", t.TokenAdmin));
        Assert.Contains(detail.Changes!, c => c.Field == "Email" && c.OldValue == "mo.old@example.com" && c.NewValue == "mo.new@example.com" && c.ChangedByName.EndsWith("(API)"));
        Assert.Contains(detail.Changes!, c => c.Field == "Name" && c.NewValue == "Mohammed Bello");

        // The first certificate keeps the details it was issued with.
        var firstNow = await Json<CredentialResponse>(await Staff(t, HttpMethod.Get, $"/api/v1/credentials/{first.Body.Id}", t.TokenAdmin));
        Assert.Equal("mo.old@example.com", firstNow.RecipientEmail);
        Assert.Equal("Mo Bello", firstNow.RecipientName);
    }

    [Fact]
    public async Task StaffCanEditAStudent_WithChecksAndHistory()
    {
        var t = await SetUpTenant();
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.TokenAdmin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Award", "Sade Ojo", "sade@example.com", null)));
        await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.TokenAdmin,
            new CreateCredentialFromTemplateRequest(t.TemplateId, "Award", "Tayo Ojo", "tayo@example.com", null)));
        var sade = Assert.Single((await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=sade", t.TokenAdmin))).Items);
        await Json<StudentEmailLinkResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/students/{sade.Id}/email-link", t.TokenAdmin));
        await SetUpStudent(t, PortalUrlIn(await LastEmailTo("sade@example.com")));

        var url = $"/api/v1/students/{sade.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await Staff(t, HttpMethod.Put, url, t.TokenApprover, new UpdateStudentRequest("Sade Ojo", "x@example.com", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Put, url, t.TokenAdmin, new UpdateStudentRequest("Sade Ojo", "not-an-email", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Put, url, t.TokenAdmin, new UpdateStudentRequest(" ", "sade@example.com", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Staff(t, HttpMethod.Put, url, t.TokenAdmin, new UpdateStudentRequest("Sade Ojo", "tayo@example.com", null, null))).StatusCode);

        var updated = await Json<StudentDetailResponse>(await Staff(t, HttpMethod.Put, url, t.TokenAdmin,
            new UpdateStudentRequest("Sade Ojo-Bello", "sade.new@example.com", "+234 800 000 0000", "ROLL-7")));
        Assert.Equal("sade.new@example.com", updated.Student.Email);
        Assert.Equal("ROLL-7", updated.Student.StudentNumber);
        Assert.Equal("+234 800 000 0000", updated.Phone);
        Assert.Equal(4, updated.Changes!.Count);
        Assert.All(updated.Changes!, c => Assert.Equal("Institution Administrator", c.ChangedByName));

        Assert.Contains("sign-in email was changed", (await LastEmailTo("sade@example.com")).Subject);
        var link = await LastEmailTo("sade.new@example.com");
        Assert.EndsWith($"/student/{t.Slug}/login", PortalUrlIn(link));
        await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("sade.new@example.com", StudentPassword)));

        // Student ID taken by another student.
        var tayo = Assert.Single((await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students?q=tayo", t.TokenAdmin))).Items);
        Assert.Equal(HttpStatusCode.Conflict, (await Staff(t, HttpMethod.Put, $"/api/v1/students/{tayo.Id}", t.TokenAdmin,
            new UpdateStudentRequest("Tayo Ojo", "tayo@example.com", null, "ROLL-7"))).StatusCode);
    }

    [Fact]
    public async Task InviteAll_EmailsOnlyStudentsNotSignedUpOrRecentlyInvited()
    {
        var t = await SetUpTenant();
        foreach (var (name, email) in new[] { ("Ama", "ama@example.com"), ("Bayo", "bayo@example.com"), ("Chi", "chi@example.com"), ("Dele", "dele@example.com") })
        {
            await Json<CredentialResponse>(await Staff(t, HttpMethod.Post, "/api/v1/credentials/from-template", t.TokenAdmin,
                new CreateCredentialFromTemplateRequest(t.TemplateId, "Old Award", name, email, null)));
        }
        var all = (await Json<StudentListResponse>(await Staff(t, HttpMethod.Get, "/api/v1/students", t.TokenAdmin))).Items;
        // Chi was emailed a link today; Dele has signed up.
        await Json<StudentEmailLinkResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/students/{all.Single(x => x.Email == "chi@example.com").Id}/email-link", t.TokenAdmin));
        await Json<StudentEmailLinkResponse>(await Staff(t, HttpMethod.Post, $"/api/v1/students/{all.Single(x => x.Email == "dele@example.com").Id}/email-link", t.TokenAdmin));
        await SetUpStudent(t, PortalUrlIn(await LastEmailTo("dele@example.com")));
        await LastEmailTo("chi@example.com");
        var sent = _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent;
        var chiBefore = sent.Count(e => e.To == "chi@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await Staff(t, HttpMethod.Get, "/api/v1/students/invitations/preview", t.TokenApprover)).StatusCode);
        var preview = await Json<StudentInvitationPreview>(await Staff(t, HttpMethod.Get, "/api/v1/students/invitations/preview", t.TokenAdmin));
        Assert.Equal(3, preview.NotSignedUp);
        Assert.Equal(1, preview.RecentlyInvited);
        Assert.Equal(2, preview.ToInvite);

        var result = await Json<StudentInvitationResult>(await Staff(t, HttpMethod.Post, "/api/v1/students/invitations", t.TokenAdmin));
        Assert.Equal(2, result.Queued);
        Assert.Equal(1, result.SkippedRecentlyInvited);
        Assert.StartsWith("Set up your", (await LastEmailTo("ama@example.com")).Subject);
        Assert.Contains("/student/welcome/", PortalUrlIn(await LastEmailTo("bayo@example.com")));
        Assert.Equal(chiBefore, sent.Count(e => e.To == "chi@example.com"));
        Assert.Equal(1, sent.Count(e => e.To == "dele@example.com"));

        // Again (or a double click): nobody is emailed twice.
        var again = await Json<StudentInvitationResult>(await Staff(t, HttpMethod.Post, "/api/v1/students/invitations", t.TokenAdmin));
        Assert.Equal(0, again.Queued);
        Assert.Equal(3, again.SkippedRecentlyInvited);
    }

    [Fact]
    public async Task DashboardAndReports_CountPaymentsCancellationsStudentsAndFailingWebhooks()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var paid = await Issue(t, key, "D-1", "dee@example.com", "d-1", name: "Dee Ike");
        await Issue(t, key, "E-1", "ebi@example.com", "e-1", name: "Ebi Okon");
        var cancelledByLms = await Issue(t, key, "F-1", "fola@example.com", "f-1", name: "Fola Ade");
        var dee = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("dee@example.com")));
        await Json<StudentCertificateResponse>(await AsStudent(t, dee, HttpMethod.Post, $"/api/v1/student-portal/certificates/{paid.Body.Id}/pay"));
        await Json<PartnerCredentialResponse>(await PartnerPost(t, key, $"/api/v1/partner/credentials/{cancelledByLms.Body.Id}/cancel", new PartnerCancelCredentialRequest("Enrolment withdrawn")));

        // A webhook that's refused.
        var keys = await Json<List<ApiClientResponse>>(await Staff(t, HttpMethod.Get, "/api/v1/api-clients", t.TokenAdmin));
        var keyId = keys.Single().Id;
        using (var scope = _factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await platform.Tenants.FirstAsync(x => x.Slug == t.Slug);
            await using var db = scope.ServiceProvider.GetRequiredService<TenantDbContextFactory>().CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
            await db.ApiClients.Where(a => a.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(a => a.WebhookUrl, "https://lms.example.com/hook"));
            db.WebhookDeliveries.Add(new Certyfi.Domain.Entities.WebhookDelivery
            {
                ApiClientId = keyId, CredentialId = paid.Body.Id, CredentialStatus = "PendingApproval",
                State = Certyfi.Domain.Entities.WebhookDeliveryState.Stopped, LastAttemptAt = DateTimeOffset.UtcNow, LastStatusCode = 401,
            });
            await db.SaveChangesAsync();
        }

        var before = await Json<DashboardResponse>(await Staff(t, HttpMethod.Get, "/api/v1/dashboard", t.TokenAdmin));
        Assert.Equal(1, before.Attention.AwaitingPayment);
        Assert.Equal(1, before.Attention.PendingApproval);
        Assert.Equal(1, before.Attention.WebhooksFailing);
        Assert.Equal(3, before.Totals.Students);
        Assert.Equal(1, before.Totals.StudentsSignedUp);

        // The deadline cancels the unpaid one.
        await _factory.Services.GetRequiredService<PaymentDeadlineRunner>().RunAsync(DateTimeOffset.UtcNow.AddDays(15), CancellationToken.None);

        var period = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&before={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}";
        var changes = await Json<StatusChangesReportResponse>(await Staff(t, HttpMethod.Get, $"/api/v1/reports/status-changes?{period}", t.TokenAdmin));
        Assert.Equal(1, changes.ByChange.Single(c => c.Name == "Paid by the student").Count);
        Assert.Equal(2, changes.ByChange.Single(c => c.Name == "Cancelled").Count);
        Assert.Equal("Student", changes.Rows.Single(r => r.Change == "Paid by the student").By);
        var byLms = changes.Rows.Single(r => r.Change == "Cancelled" && r.RecipientName == "Fola Ade");
        Assert.Equal("Enrolment withdrawn", byLms.Reason);
        Assert.NotEqual("Payment deadline (automatic)", byLms.By);
        var byDeadline = changes.Rows.Single(r => r.Change == "Cancelled" && r.RecipientName == "Ebi Okon");
        Assert.Equal("Payment deadline (automatic)", byDeadline.By);
        Assert.Equal(PaymentDeadlineRunner.CancellationReason, byDeadline.Reason);

        var activity = await Json<StaffActivityReportResponse>(await Staff(t, HttpMethod.Get, $"/api/v1/reports/staff-activity?{period}", t.TokenAdmin));
        var apiRow = activity.People.Single(p => p.IsPartnerApi);
        Assert.Equal(1, apiRow.Counts["Paid"]);
        Assert.Equal(2, apiRow.Counts["Cancelled"]);
    }

    [Fact]
    public async Task Banners_AreManagedByAdmins_AndShownByDateAndOrder()
    {
        var t = await SetUpTenant();
        var img = "https://certyfi.test/uploads/x/banner.png";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.Forbidden, (await Staff(t, HttpMethod.Get, "/api/v1/portal-banners", t.TokenApprover)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Post, "/api/v1/portal-banners", t.TokenAdmin,
            new SavePortalBannerRequest("No image", "", null, "Left", null, null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Staff(t, HttpMethod.Post, "/api/v1/portal-banners", t.TokenAdmin,
            new SavePortalBannerRequest("Bad link", img, "javascript:alert(1)", "Left", null, null, true))).StatusCode);

        var admissions = await Json<PortalBannerResponse>(await Staff(t, HttpMethod.Post, "/api/v1/portal-banners", t.TokenAdmin,
            new SavePortalBannerRequest("Admissions open", img, "https://example.edu/admissions", "Left", null, null, true)));
        var open = await Json<PortalBannerResponse>(await Staff(t, HttpMethod.Post, "/api/v1/portal-banners", t.TokenAdmin,
            new SavePortalBannerRequest("Open day", img, null, "Left", today, today.AddDays(7), true)));
        await Json<PortalBannerResponse>(await Staff(t, HttpMethod.Post, "/api/v1/portal-banners", t.TokenAdmin,
            new SavePortalBannerRequest("Next year", img, null, "Right", today.AddYears(1), null, true)));
        await Json<PortalBannerResponse>(await Staff(t, HttpMethod.Post, "/api/v1/portal-banners", t.TokenAdmin,
            new SavePortalBannerRequest("Switched off", img, null, "Right", null, null, false)));

        // Public (no sign-in): only today's active ones, in order.
        var shown = await Json<List<StudentPortalBanner>>(await Anonymous(t, HttpMethod.Get, "/api/v1/student-portal/banners"));
        Assert.Equal(["Admissions open", "Open day"], shown.Select(b => b.Title).ToArray());

        await Json<List<PortalBannerResponse>>(await Staff(t, HttpMethod.Put, "/api/v1/portal-banners/order", t.TokenAdmin,
            new ReorderPortalBannersRequest([open.Id], [admissions.Id])));
        shown = await Json<List<StudentPortalBanner>>(await Anonymous(t, HttpMethod.Get, "/api/v1/student-portal/banners"));
        Assert.Equal("Open day", shown.Single(b => b.Side == "Left").Title);
        Assert.Equal("Admissions open", shown.Single(b => b.Side == "Right").Title);

        Assert.Equal(HttpStatusCode.NoContent, (await Anonymous(t, HttpMethod.Post, $"/api/v1/student-portal/banners/{admissions.Id}/click")).StatusCode);
        var list = await Json<List<PortalBannerResponse>>(await Staff(t, HttpMethod.Get, "/api/v1/portal-banners", t.TokenAdmin));
        Assert.Equal(1, list.Single(b => b.Id == admissions.Id).ClickCount);

        var institution = await Json<StudentPortalInstitution>(await Anonymous(t, HttpMethod.Get, "/api/v1/student-portal/institution"));
        Assert.Equal(t.Slug, institution.Slug);
        Assert.Equal(HttpStatusCode.NoContent, (await Staff(t, HttpMethod.Delete, $"/api/v1/portal-banners/{open.Id}", t.TokenAdmin)).StatusCode);
    }

    [Fact]
    public async Task StudentCertificates_IncludeTheirFields_ForSearch()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: false, autoIssue: true);
        await Issue(t, key, "F-9", "fields@example.com", "f-9", name: "Fi Elds");
        var token = await SetUpStudent(t, PortalUrlIn(await LastEmailTo("fields@example.com")));
        var mine = Assert.Single(await Json<List<StudentCertificateResponse>>(await AsStudent(t, token, HttpMethod.Get, "/api/v1/student-portal/certificates")));
        var field = Assert.Single(mine.Fields!);
        Assert.Equal("Roll Number", field.Label);
        Assert.Equal("R-1", field.Value);
    }

    [Fact]
    public async Task EmailOnlySignIn_FindsTheStudentsInstitution()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: false, autoIssue: true);
        var email = $"solo.{Guid.NewGuid():N}@example.com";
        await Issue(t, key, "S-1", email, "s-1", name: "Solo Student");
        await SetUpStudent(t, PortalUrlIn(await LastEmailTo(email)));

        var signedIn = await Json<StudentSignInResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(email.ToUpperInvariant(), StudentPassword)));
        Assert.Null(signedIn.Institutions);
        Assert.Equal(t.Slug, signedIn.SignedIn!.Student.InstitutionSlug);
        // The token works for the portal.
        Assert.Single(await Json<List<StudentCertificateResponse>>(await AsStudent(t, signedIn.SignedIn.AccessToken, HttpMethod.Get, "/api/v1/student-portal/certificates")));

        // Wrong password and unknown email get the same answer.
        var wrong = await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(email, "Wrong-Pass-2026"));
        var unknown = await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest($"nobody.{Guid.NewGuid():N}@example.com", StudentPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmailOnlySignIn_AtTwoInstitutions_AsksWhichOne_OnlyWhenBothPasswordsMatch()
    {
        var a = await SetUpTenant();
        var b = await SetUpTenant();
        var email = $"twice.{Guid.NewGuid():N}@example.com";
        await Issue(a, await CreateKey(a, requiresPayment: false, autoIssue: true), "T-1", email, "t-a", name: "Two Places");
        await SetUpStudent(a, PortalUrlIn(await LastEmailTo(email)));
        await Issue(b, await CreateKey(b, requiresPayment: false, autoIssue: true), "T-1", email, "t-b", name: "Two Places");
        await SetUpStudent(b, PortalUrlIn(await LastEmailTo(email)));

        var both = await Json<StudentSignInResponse>(await Anonymous(a, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(email, StudentPassword)));
        Assert.Null(both.SignedIn);
        Assert.Equal(new[] { a.Slug, b.Slug }.Order(), both.Institutions!.Select(i => i.Slug).Order());

        // A different password at B: the password decides.
        var bToken = (await Json<StudentLoginResponse>(await Anonymous(b, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest(email, StudentPassword)))).AccessToken;
        Assert.Equal(HttpStatusCode.NoContent, (await AsStudent(b, bToken, HttpMethod.Post, "/api/v1/student-portal/change-password",
            new StudentChangePasswordRequest(StudentPassword, "Other-Pass-2026!"))).StatusCode);
        var onlyB = await Json<StudentSignInResponse>(await Anonymous(a, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(email, "Other-Pass-2026!")));
        Assert.Equal(b.Slug, onlyB.SignedIn!.Student.InstitutionSlug);

        // Forgot password: a link from each institution.
        var before = _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent.Count(e => e.To == email);
        Assert.Equal(HttpStatusCode.Accepted, (await Anonymous(a, HttpMethod.Post, "/api/v1/student-sign-in/forgot-password", new StudentForgotPasswordRequest(email))).StatusCode);
        await LastEmailTo(email);
        Assert.Equal(before + 2, _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent.Count(e => e.To == email));
        Assert.Equal(HttpStatusCode.Accepted, (await Anonymous(a, HttpMethod.Post, "/api/v1/student-sign-in/forgot-password", new StudentForgotPasswordRequest("nobody@example.com"))).StatusCode);
    }

    [Fact]
    public async Task EmailOnlySignIn_FollowsAnEmailChange_AndTheRebuild()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: false, autoIssue: true);
        var email = $"mover.{Guid.NewGuid():N}@example.com";
        var newEmail = $"moved.{Guid.NewGuid():N}@example.com";
        await Issue(t, key, "M-1", email, "m-1", name: "Mo Ver");
        await SetUpStudent(t, PortalUrlIn(await LastEmailTo(email)));
        var me = await Json<StudentSignInResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(email, StudentPassword)));

        await Json<StudentDetailResponse>(await Staff(t, HttpMethod.Put, $"/api/v1/students/{me.SignedIn!.Student.Id}", t.TokenAdmin,
            new UpdateStudentRequest("Mo Ver", newEmail, null, "M-1")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(email, StudentPassword))).StatusCode);
        Assert.NotNull((await Json<StudentSignInResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(newEmail, StudentPassword)))).SignedIn);

        // Lost entries come back with the rebuild (run by migrate-schemas).
        using (var scope = _factory.Services.CreateScope())
        {
            var index = scope.ServiceProvider.GetRequiredService<StudentSignInIndex>();
            var hash = index.Hash(newEmail);
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().StudentSignInIndex.Where(e => e.EmailHash == hash).ExecuteDeleteAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(newEmail, StudentPassword))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<StudentSignInIndex>().RebuildAsync(CancellationToken.None);
        }
        Assert.NotNull((await Json<StudentSignInResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-sign-in", new StudentLoginRequest(newEmail, StudentPassword)))).SignedIn);
    }

    [Fact]
    public async Task PartnerResponses_SayWhatTheStudentDoesNext_AndThePaymentEmailAsksForIt()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        var email = $"next.{Guid.NewGuid():N}@example.com";
        var issued = await Issue(t, key, "N-1", email, "n-1", name: "Nia Next");

        var step = issued.Body.NextStep!;
        Assert.Equal("check_email_to_pay", step.Code);
        Assert.Contains("no-reply@", step.Message);
        Assert.Contains($"by {issued.Body.PaymentDueAt!.Value.UtcDateTime:d MMMM yyyy}", step.Message);

        var mail = await LastEmailTo(email);
        Assert.StartsWith("Action needed: pay for your certificate \"Course Completion\" by", mail.Subject);
        Assert.Contains("won't be issued until it's paid", mail.Text);
        Assert.Contains("Set up your account and pay", mail.Html);

        // Paid: waiting for the institution; approved: issued.
        var token = await SetUpStudent(t, PortalUrlIn(mail));
        await Json<StudentCertificateResponse>(await AsStudent(t, token, HttpMethod.Post, $"/api/v1/student-portal/certificates/{issued.Body.Id}/pay"));
        Assert.Equal("awaiting_approval", (await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{issued.Body.Id}"))).NextStep!.Code);
        Assert.Equal(HttpStatusCode.OK, (await Staff(t, HttpMethod.Post, $"/api/v1/credentials/{issued.Body.Id}/approve", t.TokenApprover)).StatusCode);
        var done = await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{issued.Body.Id}"));
        Assert.Equal(("issued", "Active"), (done.NextStep!.Code, done.Status));

        // Cancelled for not paying says why.
        var unpaid = await Issue(t, key, "N-2", $"late.{Guid.NewGuid():N}@example.com", "n-2", name: "Lee Late");
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PaymentDeadlineRunner>().RunAsync(DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None);
        }
        var late = await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{unpaid.Body.Id}"));
        Assert.Equal("cancelled", late.NextStep!.Code);
        Assert.Contains("wasn't paid by the due date", late.NextStep.Message);
    }

    [Fact]
    public async Task PayingAndCancellingAtOnce_OnlyOneWins()
    {
        var t = await SetUpTenant();
        var key = await CreateKey(t, requiresPayment: true, autoIssue: false);
        for (var i = 0; i < 5; i++)
        {
            var issued = await Issue(t, key, "R-1", "ravi@example.com", $"r-{i}", name: "Ravi Kumar");
            var student = i == 0
                ? await SetUpStudent(t, PortalUrlIn(await LastEmailTo("ravi@example.com")))
                : (await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/login", new StudentLoginRequest("ravi@example.com", StudentPassword)))).AccessToken;

            var pay = AsStudent(t, student, HttpMethod.Post, $"/api/v1/student-portal/certificates/{issued.Body.Id}/pay");
            var cancel = PartnerPost(t, key, $"/api/v1/partner/credentials/{issued.Body.Id}/cancel", new PartnerCancelCredentialRequest("Race"));
            var results = await Task.WhenAll(pay, cancel);

            var final = await Json<PartnerCredentialResponse>(await PartnerGet(t, key, $"/api/v1/partner/credentials/{issued.Body.Id}"));
            if (results[0].IsSuccessStatusCode && results[1].IsSuccessStatusCode)
            {
                // Paid first, then cancelled (cancel is allowed after payment).
                Assert.Equal("Cancelled", final.Status);
                Assert.NotNull(final.PaidAt);
            }
            else
            {
                Assert.Contains(HttpStatusCode.Conflict, results.Select(r => r.StatusCode));
                Assert.Equal(results[0].IsSuccessStatusCode ? "PendingApproval" : "Cancelled", final.Status);
            }
        }
    }

    // ---------- helpers ----------

    private record TenantSetup(HttpClient Client, string Slug, string TokenAdmin, string TokenApprover, Guid TemplateId);

    private async Task<TenantSetup> SetUpTenant()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const string adminPassword = "TenantAdmin123!";
        const string approverPassword = "Approver123!";
        string slug;
        using (var scope = _factory.Services.CreateScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            slug = (await provisioning.ProvisionAsync($"Tenant {suffix}", $"tenant-{suffix}", $"admin@{suffix}.test", adminPassword)).Slug;
        }

        var client = _factory.CreateClient();
        var setup = new TenantSetup(client, slug, "", "", Guid.Empty);
        var tokenAdmin = (await Json<LoginResponse>(await Anonymous(setup, HttpMethod.Post, "/api/v1/auth/login", new TenantLoginRequest($"admin@{suffix}.test", adminPassword)))).AccessToken;
        setup = setup with { TokenAdmin = tokenAdmin };
        await Staff(setup, HttpMethod.Post, "/api/v1/users", tokenAdmin, new CreateUserRequest($"approver@{suffix}.test", "Test Approver", approverPassword, SystemRole.Approver));
        var tokenApprover = (await Json<LoginResponse>(await Anonymous(setup, HttpMethod.Post, "/api/v1/auth/login", new TenantLoginRequest($"approver@{suffix}.test", approverPassword)))).AccessToken;
        var template = await Json<CertificateTemplateResponse>(await Staff(setup, HttpMethod.Post, "/api/v1/certificate-templates", tokenAdmin,
            new CreateCertificateTemplateRequest("Course Certificate", null, "Certificate", FieldSchemaJson)));
        return setup with { TokenApprover = tokenApprover, TemplateId = template.Id };
    }

    private static async Task<string> CreateKey(TenantSetup t, bool requiresPayment, bool autoIssue)
    {
        var response = await Staff(t, HttpMethod.Post, "/api/v1/api-clients", t.TokenAdmin,
            new CreateApiClientRequest($"LMS {Guid.NewGuid():N}"[..20], PartnerScope.All, autoIssue, requiresPayment));
        var created = await Json<CreateApiClientResponse>(response);
        Assert.Equal(requiresPayment, created.Client.RequiresPayment);
        return created.ApiKey;
    }

    private static Task<HttpResponseMessage> IssueRaw(TenantSetup t, string key, string studentId, string email, string reference, string name = "Ada Lovelace")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/credentials")
        {
            Content = JsonContent.Create(new PartnerIssueCredentialRequest(t.TemplateId, "Course Completion", name, email,
                JsonSerializer.SerializeToElement(new { rollNumber = "R-1" }), reference, studentId)),
        };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Add("X-Api-Key", key);
        return t.Client.SendAsync(request);
    }

    private static async Task<(HttpStatusCode Status, PartnerCredentialResponse Body)> Issue(TenantSetup t, string key, string studentId, string email, string reference, string name = "Ada Lovelace")
    {
        var response = await IssueRaw(t, key, studentId, email, reference, name);
        return (response.StatusCode, await Json<PartnerCredentialResponse>(response));
    }

    private static Task<HttpResponseMessage> PartnerPost(TenantSetup t, string key, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Add("X-Api-Key", key);
        return t.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PartnerGet(TenantSetup t, string key, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        request.Headers.Add("X-Api-Key", key);
        return t.Client.SendAsync(request);
    }

    private static string Token(string url) => url[(url.LastIndexOf('/') + 1)..];

    // Sends the outbox, then the latest email to this address.
    private async Task<OutgoingEmail> LastEmailTo(string address)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().SendDueAsync(CancellationToken.None);
        }
        var sent = _factory.Services.GetRequiredService<MemoryEmailTransport>().Sent;
        return sent.Last(e => e.To == address);
    }

    // The student-portal link in an email (set-up, reset or sign-in).
    private static string PortalUrlIn(OutgoingEmail email) =>
        Regex.Match(email.Text, @"https://certyfi\.test/student/\S+").Value;

    // Sets the password from a portal link; returns the student's token.
    private static async Task<string> SetUpStudent(TenantSetup t, string portalUrl) =>
        (await Json<StudentLoginResponse>(await Anonymous(t, HttpMethod.Post, "/api/v1/student-portal/setup", new StudentSetupRequest(Token(portalUrl), StudentPassword)))).AccessToken;

    private static Task<HttpResponseMessage> Anonymous(TenantSetup t, HttpMethod method, string url, object? body = null) => Send(t, method, url, null, body);
    private static Task<HttpResponseMessage> AsStudent(TenantSetup t, string token, HttpMethod method, string url, object? body = null) => Send(t, method, url, token, body);
    private static Task<HttpResponseMessage> Staff(TenantSetup t, HttpMethod method, string url, string token, object? body = null) => Send(t, method, url, token, body);

    private static Task<HttpResponseMessage> Send(TenantSetup t, HttpMethod method, string url, string? token, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null || method == HttpMethod.Post) request.Content = JsonContent.Create(body ?? new { });
        request.Headers.Add("X-Tenant-Slug", t.Slug);
        // Each request from its own made-up client IP, so the sign-in rate
        // limit doesn't trip across tests.
        request.Headers.Add("CF-Connecting-IP", $"198.18.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 255)}-{Guid.NewGuid():N}");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return t.Client.SendAsync(request);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
