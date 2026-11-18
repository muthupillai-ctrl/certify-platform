using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Certyfi.Infrastructure.Email;

// The emails a student gets. Certificates from a partner app (LMS) email the
// student when requested and again when issued; staff can email a sign-in
// link; students can ask for a password reset. Links need PublicApp:BaseUrl;
// without it nothing is queued (and a warning is logged).
public class StudentNotifications
{
    private readonly EmailOutbox _outbox;
    private readonly StudentPortalLink _links;
    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<StudentNotifications> _logger;

    public StudentNotifications(EmailOutbox outbox, StudentPortalLink links, PlatformDbContext platformDb, TenantDbContext db,
        ITenantContext tenant, ILogger<StudentNotifications> logger)
    {
        _outbox = outbox;
        _links = links;
        _platformDb = platformDb;
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    // A partner app's request created this certificate and it's waiting
    // (for payment or approval); one already issued got the "issued" email.
    public async Task<bool> CertificateRequestedAsync(Credential credential, Student student, string title, CancellationToken ct)
    {
        if (credential.Status is not (CredentialStatus.AwaitingPayment or CredentialStatus.PendingApproval)) return false;
        if (BaseUrl() is not { } baseUrl) return false;
        var institution = await InstitutionName(ct);
        var content = EmailTemplates.Requested(student.FullName, institution, title,
            credential.Status == CredentialStatus.AwaitingPayment, _links.PortalUrl(baseUrl, _tenant.TenantSlug, student, DateTimeOffset.UtcNow),
            student.PasswordHash is null, credential.PaymentDueAt);
        return await Invited(student, await _outbox.QueueAsync(EmailTemplates.CertificateRequested, student.Email, student.FullName, content, _tenant.TenantId, student.Id, credential.Id, ct), ct);
    }

    // Called on every approval; emails only for certificates a partner app
    // requested (staff and paper imports don't email recipients).
    public async Task<bool> CertificateIssuedAsync(Credential credential, CancellationToken ct)
    {
        if (credential.StudentId is not { } studentId || credential.ImportBatchId is not null) return false;
        if (!await _db.ApiClients.AnyAsync(a => a.ServiceUserId == credential.RequestedByUserId, ct)) return false;
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null || BaseUrl() is not { } baseUrl) return false;

        var title = credential.Achievement?.Title
            ?? await _db.Achievements.Where(a => a.Id == credential.AchievementId).Select(a => a.Title).FirstAsync(ct);
        var content = EmailTemplates.Issued(student.FullName, await InstitutionName(ct), title,
            $"{baseUrl}/verify/{_tenant.TenantSlug}/{credential.Id}",
            _links.PortalUrl(baseUrl, _tenant.TenantSlug, student, DateTimeOffset.UtcNow), student.PasswordHash is null);
        return await Invited(student, await _outbox.QueueAsync(EmailTemplates.CertificateIssued, student.Email, student.FullName, content, _tenant.TenantId, student.Id, credential.Id, ct), ct);
    }

    // A partner app withdrew a certificate the student had been told about.
    public async Task<bool> CertificateCancelledAsync(Credential credential, CancellationToken ct)
    {
        if (await PartnerStudentAsync(credential, ct) is not var (student, title, _)) return false;
        var content = credential.CancelledForNonPayment && credential.PaymentDueAt is { } due
            ? EmailTemplates.CancelledUnpaid(student.FullName, await InstitutionName(ct), title, due)
            : EmailTemplates.Cancelled(student.FullName, await InstitutionName(ct), title, credential.PaidAt is not null);
        return await _outbox.QueueAsync(EmailTemplates.CertificateCancelled, student.Email, student.FullName, content, _tenant.TenantId, student.Id, credential.Id, ct);
    }

    // An approver turned down a certificate the student had been told about
    // (and, with payment required, had paid for). The reason is included,
    // as it is in the student portal.
    public async Task<bool> CertificateRejectedAsync(Credential credential, CancellationToken ct)
    {
        if (await PartnerStudentAsync(credential, ct) is not var (student, title, baseUrl)) return false;
        var content = EmailTemplates.Rejected(student.FullName, await InstitutionName(ct), title, credential.RejectionReason,
            credential.PaidAt is not null, _links.PortalUrl(baseUrl, _tenant.TenantSlug, student, DateTimeOffset.UtcNow), student.PasswordHash is null);
        return await Invited(student, await _outbox.QueueAsync(EmailTemplates.CertificateRejected, student.Email, student.FullName, content, _tenant.TenantId, student.Id, credential.Id, ct), ct);
    }

    // A few days before an unpaid certificate's due date.
    public async Task<bool> PaymentReminderAsync(Credential credential, CancellationToken ct)
    {
        if (credential.PaymentDueAt is not { } due) return false;
        if (await PartnerStudentAsync(credential, ct) is not var (student, title, baseUrl)) return false;
        var content = EmailTemplates.Reminder(student.FullName, await InstitutionName(ct), title, due,
            _links.PortalUrl(baseUrl, _tenant.TenantSlug, student, DateTimeOffset.UtcNow), student.PasswordHash is null);
        return await Invited(student, await _outbox.QueueAsync(EmailTemplates.PaymentReminder, student.Email, student.FullName, content, _tenant.TenantId, student.Id, credential.Id, ct), ct);
    }

    // After issue: suspended, reactivated, revoked. The link is the public
    // verify page, which shows the current status.
    public Task<bool> CertificateSuspendedAsync(Credential credential, CancellationToken ct) =>
        StatusChangeAsync(credential, EmailTemplates.CertificateSuspended, EmailTemplates.Suspended, ct);

    public Task<bool> CertificateReactivatedAsync(Credential credential, CancellationToken ct) =>
        StatusChangeAsync(credential, EmailTemplates.CertificateReactivated, EmailTemplates.Reactivated, ct);

    public Task<bool> CertificateRevokedAsync(Credential credential, CancellationToken ct) =>
        StatusChangeAsync(credential, EmailTemplates.CertificateRevoked, EmailTemplates.Revoked, ct);

    private async Task<bool> StatusChangeAsync(Credential credential, string kind,
        Func<string, string, string, string, EmailContent> template, CancellationToken ct)
    {
        if (await PartnerStudentAsync(credential, ct) is not var (student, title, baseUrl)) return false;
        var content = template(student.FullName, await InstitutionName(ct), title, $"{baseUrl}/verify/{_tenant.TenantSlug}/{credential.Id}");
        return await _outbox.QueueAsync(kind, student.Email, student.FullName, content, _tenant.TenantId, student.Id, credential.Id, ct);
    }

    // The student and title for a certificate a partner app requested, or
    // null when it wasn't one (staff and paper imports don't email
    // recipients), has no student, or links can't be built.
    private async Task<(Student Student, string Title, string BaseUrl)?> PartnerStudentAsync(Credential credential, CancellationToken ct)
    {
        if (credential.StudentId is not { } studentId || credential.ImportBatchId is not null) return null;
        if (!await _db.ApiClients.AnyAsync(a => a.ServiceUserId == credential.RequestedByUserId, ct)) return null;
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null || BaseUrl() is not { } baseUrl) return null;
        var title = credential.Achievement?.Title
            ?? await _db.Achievements.Where(a => a.Id == credential.AchievementId).Select(a => a.Title).FirstAsync(ct);
        return (student, title, baseUrl);
    }

    // The student's email changed: a notice to the old address, and (for a
    // staff edit) the sign-in or set-up link to the new one. A partner app's
    // change needs no link: its certificate email goes to the new address.
    public async Task EmailChangedAsync(Student student, string oldEmail, bool sendLinkToNewAddress, CancellationToken ct)
    {
        if (BaseUrl() is null) return;
        var content = EmailTemplates.EmailChanged(student.FullName, await InstitutionName(ct), EmailTemplates.MaskEmail(student.Email));
        await _outbox.QueueAsync(EmailTemplates.StudentEmailChanged, oldEmail, student.FullName, content, _tenant.TenantId, student.Id, null, ct);
        if (sendLinkToNewAddress) await SignInLinkAsync(student, ct);
    }

    // Staff: send the student their set-up link (or the sign-in page).
    public async Task<bool> SignInLinkAsync(Student student, CancellationToken ct)
    {
        if (BaseUrl() is not { } baseUrl) return false;
        var content = EmailTemplates.SignInLink(student.FullName, await InstitutionName(ct),
            _links.PortalUrl(baseUrl, _tenant.TenantSlug, student, DateTimeOffset.UtcNow), student.PasswordHash is null);
        return await Invited(student, await _outbox.QueueAsync(EmailTemplates.StudentSignInLink, student.Email, student.FullName, content, _tenant.TenantId, student.Id, null, ct), ct);
    }

    // Bulk: email a set-up link to each of these students (all without a
    // password). Returns how many were queued.
    public async Task<int> InviteManyAsync(IReadOnlyList<Student> students, CancellationToken ct)
    {
        if (students.Count == 0 || BaseUrl() is not { } baseUrl) return 0;
        var institution = await InstitutionName(ct);
        var now = DateTimeOffset.UtcNow;
        var entries = students.Select(student =>
        {
            var content = EmailTemplates.SignInLink(student.FullName, institution, _links.PortalUrl(baseUrl, _tenant.TenantSlug, student, now), isWelcome: true);
            return new EmailOutboxEntry
            {
                Kind = EmailTemplates.StudentSignInLink, ToEmail = student.Email, ToName = student.FullName,
                Subject = content.Subject, HtmlBody = content.Html, TextBody = content.Text,
                TenantId = _tenant.TenantId, StudentId = student.Id,
            };
        });
        var queued = await _outbox.QueueManyAsync(entries, ct);
        foreach (var ids in students.Select(s => s.Id).Chunk(500))
        {
            await _db.Students.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastInvitedAt, now), ct);
        }
        return queued;
    }

    // Student: "forgot password". A student who never set one gets the
    // set-up link instead.
    public async Task<bool> PasswordResetAsync(Student student, CancellationToken ct)
    {
        if (student.PasswordHash is null) return await SignInLinkAsync(student, ct);
        if (BaseUrl() is not { } baseUrl) return false;
        var token = _links.Issue(_tenant.TenantSlug, student, StudentPortalLink.Purpose.Reset, DateTimeOffset.UtcNow);
        var content = EmailTemplates.PasswordReset(student.FullName, await InstitutionName(ct), StudentPortalLink.LinkUrl(baseUrl, token));
        return await _outbox.QueueAsync(EmailTemplates.StudentPasswordReset, student.Email, student.FullName, content, _tenant.TenantId, student.Id, null, ct);
    }

    // Records that a set-up link went out (for bulk invitations to skip
    // recently invited students); only when they have no password yet.
    private async Task<bool> Invited(Student student, bool queued, CancellationToken ct)
    {
        if (queued && student.PasswordHash is null)
        {
            await _db.Students.Where(s => s.Id == student.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastInvitedAt, DateTimeOffset.UtcNow), ct);
        }
        return queued;
    }

    private string? BaseUrl()
    {
        if (_links.BaseUrl is { } url) return url;
        _logger.LogWarning("PublicApp:BaseUrl is not set, so student emails (which need links) are not sent.");
        return null;
    }

    private Task<string> InstitutionName(CancellationToken ct) =>
        _platformDb.Tenants.AsNoTracking().Where(t => t.Id == _tenant.TenantId).Select(t => t.Name).FirstAsync(ct);
}
