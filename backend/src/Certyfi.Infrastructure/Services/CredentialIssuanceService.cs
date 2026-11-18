using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Email;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Signing;
using Certyfi.Infrastructure.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Certyfi.Infrastructure.Services;

// The create/approve/revoke steps shared by CredentialsController (staff,
// JWT) and PartnerController (partner systems, API key), so both paths
// write identical credential rows, ledger entries and signatures. Callers
// still own authorization, validation of request shape and the maker-checker
// self-approval check — this class only performs the state change.
public class CredentialIssuanceService
{
    private readonly TenantDbContext _db;
    private readonly ILedgerService _ledger;
    private readonly ITenantSigningKeyProvider _signingKeys;
    private readonly ITenantContext _tenantContext;
    private readonly OfflineProofSigner _offlineProofs;
    private readonly StudentDirectory _students;
    private readonly StudentNotifications _notifications;
    private readonly EmailOutbox _outbox;
    private readonly WebhookQueue _webhooks;
    // Certificates:EnforceMandatoryContent (default on): a new certificate
    // needs signatories (see Signatory); digitised paper ones are exempt.
    private readonly bool _enforceMandatory;

    public CredentialIssuanceService(
        TenantDbContext db, ILedgerService ledger, ITenantSigningKeyProvider signingKeys, ITenantContext tenantContext,
        OfflineProofSigner offlineProofs, StudentDirectory students, StudentNotifications notifications, EmailOutbox outbox,
        WebhookQueue webhooks, IConfiguration configuration)
    {
        _enforceMandatory = configuration.GetValue("Certificates:EnforceMandatoryContent", true);
        _webhooks = webhooks;
        _db = db;
        _students = students;
        _notifications = notifications;
        _outbox = outbox;
        _ledger = ledger;
        _signingKeys = signingKeys;
        _tenantContext = tenantContext;
        _offlineProofs = offlineProofs;
    }

    // Reuse is exact-match on (TemplateId, Title) — see
    // CredentialsController.CreateFromTemplate for why.
    public async Task<Achievement> GetOrCreateAchievementForTemplateAsync(CertificateTemplate template, string title, CancellationToken ct)
    {
        var achievement = await _db.Achievements.FirstOrDefaultAsync(
            a => a.TemplateId == template.Id && a.Title == title && a.IsActive, ct);
        if (achievement is not null) return achievement;

        achievement = new Achievement
        {
            Title = title,
            TemplateId = template.Id,
            TemplateKind = template.Kind,
            FieldSchemaJson = template.FieldSchemaJson,
            TemplateTitleText = template.TitleText,
            TemplateSubtitleText = template.SubtitleText,
            TemplateAccentColor = template.AccentColor,
            TemplateFontFamily = template.FontFamily,
            TemplateLayoutJson = template.LayoutJson,
            TemplateBackgroundImageUrl = template.BackgroundImageUrl,
        };
        _db.Achievements.Add(achievement);
        await _db.SaveChangesAsync(ct);
        return achievement;
    }

    // A paper certificate being digitised (see ImportBatch). ExpiresOn, when
    // the paper had one, is kept as given at approval.
    public record PaperDetails(Guid ImportBatchId, DateOnly OriginalIssueDate, string? LegacyNumber, string? LegacyNumberKey, DateTimeOffset? ExpiresOn);

    // Field values must already be validated against
    // achievement.FieldSchemaJson by the caller. The certificate is linked to
    // `student` when given (a Partner API request), otherwise to the student
    // with the recipient's email, created if new. awaitingPayment starts it
    // as AwaitingPayment instead of PendingApproval (see MarkPaidAsync), to
    // be paid by paymentDueAt if given (see PaymentDeadlineRunner).
    public async Task<Credential> CreatePendingAsync(
        Achievement achievement, string recipientName, string recipientEmail, string fieldValuesJson,
        Guid requestedByUserId, string? externalReference, CancellationToken ct, PaperDetails? paper = null,
        Student? student = null, bool awaitingPayment = false, DateTimeOffset? paymentDueAt = null)
    {
        // Before anything is written (the student included).
        var signatoriesJson = paper is null ? await SignatoriesJsonAsync(achievement, ct) : null;
        student ??= await _students.ForRecipientAsync(recipientEmail, recipientName, ct);
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        var credential = new Credential
        {
            SignatoriesJson = signatoriesJson,
            StudentId = student?.Id,
            Status = awaitingPayment ? CredentialStatus.AwaitingPayment : CredentialStatus.PendingApproval,
            PaymentDueAt = awaitingPayment ? paymentDueAt : null,
            AchievementId = achievement.Id,
            RecipientName = recipientName,
            RecipientEmail = recipientEmail.Trim().ToLowerInvariant(),
            RequestedByUserId = requestedByUserId,
            CreatedAt = now,
            FieldValuesJson = fieldValuesJson,
            FieldSchemaJson = achievement.FieldSchemaJson,
            ExternalReference = externalReference,
            VerificationCode = VerificationCodes.Generate(),
            ImportBatchId = paper?.ImportBatchId,
            OriginalIssueDate = paper?.OriginalIssueDate,
            LegacyCertificateNumber = paper?.LegacyNumber,
            LegacyNumberKey = paper?.LegacyNumberKey,
            ExpiresOn = paper?.ExpiresOn,
        };
        PersonalDataFingerprint.EnsureSalt(credential);
        _db.Credentials.Add(credential);

        // Who it's for stays off the chain (erasable); see LedgerEntryDetail.
        object eventData = paper is not null
            ? new { AchievementId = achievement.Id, credential.ImportBatchId, credential.OriginalIssueDate }
            : awaitingPayment
                ? new { AchievementId = achievement.Id, status = credential.Status.ToString(), credential.PaymentDueAt }
                : new { AchievementId = achievement.Id };
        object personalData = paper is null
            ? new { credential.RecipientName, credential.RecipientEmail }
            : new { credential.RecipientName, credential.RecipientEmail, credential.LegacyCertificateNumber };
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await _ledger.AppendAsync(credential, "Requested", requestedByUserId, now, eventData, personalData, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return credential;
    }

    // The signatories a new certificate from this achievement carries: its
    // template's, as they are now. Throws MissingSignatoryException when
    // there are none and they're required. Callers that create other rows
    // first (the Partner API's student) call it up front.
    public async Task<string?> SignatoriesJsonAsync(Achievement achievement, CancellationToken ct)
    {
        var ids = achievement.TemplateId is { } templateId
            ? await _db.CertificateTemplates.AsNoTracking().Where(t => t.Id == templateId).Select(t => t.SignatoryIds).FirstOrDefaultAsync(ct) ?? []
            : [];
        var signatories = await SignatorySnapshot.ForTemplateAsync(_db, ids, ct);
        if (signatories.Count == 0)
        {
            if (_enforceMandatory) throw new MissingSignatoryException(achievement.Title);
            return null;
        }
        return SignatorySnapshot.Serialize(signatories.Select(s => new SignatorySnapshot.Entry(s.Name, s.Designation)));
    }

    // Fields the achievement's template marks required now. A reused
    // achievement keeps the field list it was created with, so a field made
    // required on the template later is still enforced (by key).
    public async Task<IReadOnlySet<string>> RequiredFieldKeysAsync(Achievement achievement, CancellationToken ct)
    {
        if (achievement.TemplateId is not { } templateId) return new HashSet<string>();
        var schema = await _db.CertificateTemplates.AsNoTracking().Where(t => t.Id == templateId).Select(t => t.FieldSchemaJson).FirstOrDefaultAsync(ct);
        return schema is null ? new HashSet<string>() : FieldSchemaValidator.RequiredKeys(schema);
    }

    // Dummy payment by the student (student portal): AwaitingPayment →
    // PendingApproval. The audit trail's actor is the account that requested
    // the certificate (students aren't staff users); the entry says the
    // student paid. `credential` must be tracked and AwaitingPayment.
    public async Task MarkPaidAsync(Credential credential, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        credential.Status = CredentialStatus.PendingApproval;
        credential.PaidAt = now;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockStatusAsync(credential.Id, CredentialStatus.AwaitingPayment, ct);
        await _ledger.AppendAsync(credential.Id, "Paid", credential.RequestedByUserId, now,
            JsonSerializer.Serialize(new { status = credential.Status.ToString(), paidBy = "Student", credential.StudentId }), ct);
        await _webhooks.QueueAsync(credential, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // `credential` must be tracked, with Achievement loaded, and still
    // PendingApproval. deciderUserId is recorded as the checker;
    // ledgerActorUserId is who performed the action. They're the same person
    // for a human approval; for a Partner API auto-issue the decider is the
    // InstitutionAdmin who granted the key auto-issue and the actor is the
    // key's service account. autoIssueApiClientId marks the ledger entry as
    // an auto-issue so the audit trail says so explicitly.
    //
    // Returns true when it was issued; false when the template needs final
    // approval, so it's now PendingFinalApproval (FirstApproved in the
    // ledger) and an institution admin finishes it with FinalApproveAsync.
    // That holds for auto-issue too: final approval can't be bypassed.
    public async Task<bool> ApproveAsync(Credential credential, Guid deciderUserId, Guid ledgerActorUserId, Guid? autoIssueApiClientId, CancellationToken ct)
    {
        if (credential.Status != CredentialStatus.PendingApproval)
            throw new InvalidOperationException($"Credential is {credential.Status}, not pending approval.");
        if (await NeedsFinalApprovalAsync(credential, ct))
        {
            await FirstApproveAsync(credential, deciderUserId, ledgerActorUserId, autoIssueApiClientId, ct);
            return false;
        }
        await IssueAsync(credential, deciderUserId, ledgerActorUserId, autoIssueApiClientId, CredentialStatus.PendingApproval, ct);
        return true;
    }

    // The second approval: an institution admin issues a PendingFinalApproval
    // certificate. The caller has checked they're neither the requester nor
    // the first approver.
    public Task FinalApproveAsync(Credential credential, Guid adminUserId, CancellationToken ct)
    {
        if (credential.Status != CredentialStatus.PendingFinalApproval)
            throw new InvalidOperationException($"Credential is {credential.Status}, not waiting for final approval.");
        return IssueAsync(credential, adminUserId, adminUserId, null, CredentialStatus.PendingFinalApproval, ct);
    }

    // Does the certificate's template (as it is now) need final approval?
    public async Task<bool> NeedsFinalApprovalAsync(Credential credential, CancellationToken ct)
    {
        var achievement = credential.Achievement ?? await _db.Achievements.AsNoTracking().FirstAsync(a => a.Id == credential.AchievementId, ct);
        return achievement.TemplateId is { } templateId
            && await _db.CertificateTemplates.AsNoTracking().Where(t => t.Id == templateId).Select(t => t.RequiresFinalApproval).FirstOrDefaultAsync(ct);
    }

    private async Task FirstApproveAsync(Credential credential, Guid deciderUserId, Guid ledgerActorUserId, Guid? autoIssueApiClientId, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        credential.Status = CredentialStatus.PendingFinalApproval;
        credential.FirstApprovedByUserId = deciderUserId;
        credential.FirstApprovedAt = now;
        var eventData = autoIssueApiClientId is { } apiClientId
            ? JsonSerializer.Serialize(new { status = credential.Status.ToString(), mode = "AutoIssue", apiClientId, policyApprovedByUserId = deciderUserId })
            : JsonSerializer.Serialize(new { status = credential.Status.ToString() });
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockStatusAsync(credential.Id, CredentialStatus.PendingApproval, ct);
        await _ledger.AppendAsync(credential.Id, "FirstApproved", ledgerActorUserId, now, eventData, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Signs and issues: from PendingApproval (one approval) or
    // PendingFinalApproval (the final one).
    private async Task IssueAsync(Credential credential, Guid deciderUserId, Guid ledgerActorUserId, Guid? autoIssueApiClientId, CredentialStatus from, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        credential.Status = CredentialStatus.Active;
        credential.DecidedByUserId = deciderUserId;
        credential.DecidedAt = now;
        credential.IssuedAt = now;
        if (credential.OriginalIssueDate is { } original)
        {
            // A paper certificate's validity runs from its paper date, unless
            // the paper gave its own expiry.
            credential.ExpiresOn ??= credential.Achievement.ValidityPeriodMonths is { } paperMonths
                ? new DateTimeOffset(original.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMonths(paperMonths)
                : null;
        }
        else
        {
            credential.ExpiresOn = credential.Achievement.ValidityPeriodMonths is { } months
                ? now.AddMonths(months)
                : null;
        }
        credential.CanonicalHash = ComputeCanonicalHash(credential);

        // Real signature, under the issuing tenant's own key — not just
        // another internal hash. GetOrCreateActiveKeyAsync is safe to call
        // on every approval (cheap no-op read once a tenant's first key
        // exists); signing happens OUTSIDE the transaction below since it's
        // a call to ITenantSigningKeyProvider (local today, a real KMS
        // later) rather than a _db mutation of its own.
        var signingKey = await _signingKeys.GetOrCreateActiveKeyAsync(_tenantContext.TenantId, ct);
        var signatureBytes = await _signingKeys.SignAsync(
            _tenantContext.TenantId, signingKey.KeyId, Encoding.UTF8.GetBytes(credential.CanonicalHash), ct);
        credential.Signature = Convert.ToBase64String(signatureBytes);
        credential.SignatureKeyId = signingKey.KeyId;
        credential.OfflineProof = await _offlineProofs.CreateAsync(
            _tenantContext.TenantId, _tenantContext.TenantSlug, credential, credential.Achievement.Title, signingKey.KeyId, ct);

        var eventData = autoIssueApiClientId is { } apiClientId
            ? JsonSerializer.Serialize(new
            {
                status = credential.Status.ToString(), credential.IssuedAt, credential.ExpiresOn, credential.CanonicalHash, credential.SignatureKeyId,
                mode = "AutoIssue", apiClientId, policyApprovedByUserId = deciderUserId,
            })
            : from == CredentialStatus.PendingFinalApproval
                ? JsonSerializer.Serialize(new
                {
                    status = credential.Status.ToString(), credential.IssuedAt, credential.ExpiresOn, credential.CanonicalHash, credential.SignatureKeyId,
                    stage = "Final", firstApprovedByUserId = credential.FirstApprovedByUserId,
                })
                : JsonSerializer.Serialize(new { status = credential.Status.ToString(), credential.IssuedAt, credential.ExpiresOn, credential.CanonicalHash, credential.SignatureKeyId });

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockStatusAsync(credential.Id, from, ct);
            await _ledger.AppendAsync(credential.Id, "Approved", ledgerActorUserId, now, eventData, ct);
            await _webhooks.QueueAsync(credential, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // "Your certificate is ready", for certificates a partner app asked for.
        await _notifications.CertificateIssuedAsync(credential, ct);
    }

    // `credential` must be tracked and PendingApproval or PendingFinalApproval
    // (rejected at the first or the final approval); the caller has already
    // checked the decider may decide it (maker–checker).
    public async Task RejectAsync(Credential credential, Guid deciderUserId, string reason, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        var from = credential.Status;
        if (from is not (CredentialStatus.PendingApproval or CredentialStatus.PendingFinalApproval))
            throw new InvalidOperationException($"Credential is {from}, not waiting for approval.");
        credential.Status = CredentialStatus.Rejected;
        credential.DecidedByUserId = deciderUserId;
        credential.DecidedAt = now;
        credential.RejectionReason = reason;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockStatusAsync(credential.Id, from, ct);
        await _ledger.AppendAsync(credential, "Rejected", deciderUserId, now,
            new { status = credential.Status.ToString(), stage = from == CredentialStatus.PendingFinalApproval ? "Final" : "First" }, new { credential.RejectionReason }, ct);
        await _webhooks.QueueAsync(credential, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _notifications.CertificateRejectedAsync(credential, ct);
    }

    // The requester withdraws a certificate that isn't issued yet
    // (AwaitingPayment or PendingApproval → Cancelled): it can no longer be
    // paid for or approved. forNonPayment: the payment deadline did it (the
    // actor is then the requester's account). `credential` must be tracked
    // and in one of those states.
    public async Task CancelAsync(Credential credential, Guid actorUserId, string reason, CancellationToken ct, bool forNonPayment = false)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        var from = credential.Status;
        credential.Status = CredentialStatus.Cancelled;
        credential.CancelledAt = now;
        credential.CancellationReason = reason;
        credential.CancelledForNonPayment = forNonPayment;

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockStatusAsync(credential.Id, from, ct);
            await _ledger.AppendAsync(credential, "Cancelled", actorUserId, now,
                new { status = credential.Status.ToString(), from = from.ToString(), paymentDeadline = forNonPayment }, new { credential.CancellationReason }, ct);
            await _webhooks.QueueAsync(credential, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await _notifications.CertificateCancelledAsync(credential, ct);
    }

    // Records that the payment reminder went out (PaymentDeadlineRunner).
    public async Task MarkPaymentReminderSentAsync(Credential credential, CancellationToken ct)
    {
        credential.PaymentReminderSentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _notifications.PaymentReminderAsync(credential, ct);
    }

    // `credential` must be tracked and Active.
    public async Task SuspendAsync(Credential credential, Guid actorUserId, string reason, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        credential.Status = CredentialStatus.Suspended;
        credential.SuspendedByUserId = actorUserId;
        credential.SuspendedAt = now;
        credential.SuspensionReason = reason;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _ledger.AppendAsync(credential, "Suspended", actorUserId, now,
            new { status = credential.Status.ToString() }, new { credential.SuspensionReason }, ct);
        await _webhooks.QueueAsync(credential, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _notifications.CertificateSuspendedAsync(credential, ct);
    }

    // `credential` must be tracked and Suspended. The earlier suspension
    // fields stay as history (the ledger has the full story).
    public async Task ReactivateAsync(Credential credential, Guid actorUserId, string note, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        credential.Status = CredentialStatus.Active;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _ledger.AppendAsync(credential, "Reactivated", actorUserId, now,
            new { status = credential.Status.ToString() }, new { note }, ct);
        await _webhooks.QueueAsync(credential, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _notifications.CertificateReactivatedAsync(credential, ct);
    }

    // Data-protection erasure ("right to be forgotten"). Only for a certificate
    // that is no longer valid (revoked or rejected): the recipient's details,
    // custom fields, reasons and notes are wiped, the off-chain audit details
    // and the salt are deleted, and the hash chain stays intact; what remains
    // in it (salted fingerprints) can no longer be linked to anyone. The
    // certificate stops resolving publicly. `credential` must be tracked.
    public async Task ErasePersonalDataAsync(Credential credential, Guid actorUserId, CancellationToken ct)
    {
        var now = LedgerService.ToStoragePrecision(DateTimeOffset.UtcNow);
        var details = await _db.LedgerEntryDetails.Where(d => d.CredentialId == credential.Id).ToListAsync(ct);
        _db.LedgerEntryDetails.RemoveRange(details);
        foreach (var request in await _db.RevocationRequests.Where(r => r.CredentialId == credential.Id).ToListAsync(ct))
        {
            request.Note = ErasedText;
            request.DecisionNote = null;
        }

        credential.RecipientName = ErasedText;
        credential.RecipientEmail = "";
        credential.FieldValuesJson = "{}";
        credential.RejectionReason = null;
        credential.CancellationReason = null;
        credential.SuspensionReason = null;
        credential.RevocationReason = null;
        credential.LegacyCertificateNumber = null;
        credential.LegacyNumberKey = null;
        credential.ExternalReference = null;
        credential.OfflineProof = null;
        credential.CanonicalHash = null;
        credential.Signature = null;
        credential.PersonalDataSalt = null;
        credential.PersonalDataErasedAt = now;
        // Off the student's record too; a student left with no certificates
        // is deleted (their name, email and sign-in go with them).
        Guid? orphanedStudentId = null;
        if (credential.StudentId is { } studentId)
        {
            credential.StudentId = null;
            if (!await _db.Credentials.AnyAsync(c => c.StudentId == studentId && c.Id != credential.Id, ct))
            {
                orphanedStudentId = studentId;
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _ledger.AppendAsync(credential.Id, "PersonalDataErased", actorUserId, now,
            JsonSerializer.Serialize(new { erasedDetails = details.Count }), ct);
        await _db.SaveChangesAsync(ct);
        string? orphanEmail = null;
        if (orphanedStudentId is { } orphan)
        {
            orphanEmail = await _db.Students.Where(st => st.Id == orphan).Select(st => st.Email).FirstOrDefaultAsync(ct);
            await _db.Students.Where(st => st.Id == orphan).ExecuteDeleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        await _outbox.ForgetAsync(credential.Id, orphanedStudentId, ct);
        if (orphanEmail is not null) await _students.ForgetSignInAsync(orphanEmail, ct);
    }

    public const string ErasedText = "[erased]";

    // Inside a step's transaction, before it saves: locks the certificate's
    // row and checks it's still in the state the step was decided on. A step
    // racing another on the same certificate (the student paying while the
    // partner cancels, an approver approving while it's cancelled) waits for
    // the other to commit, then finds the status changed and stops here.
    private async Task LockStatusAsync(Guid credentialId, CredentialStatus expected, CancellationToken ct)
    {
        var locked = await _db.Credentials
            .Where(c => c.Id == credentialId && c.Status == expected)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, expected), ct);
        if (locked == 0) throw new CredentialStateChangedException();
    }

    // NOT a real signature — see Credential.CanonicalHash. SHA-256 hex of a
    // fixed-shape JSON payload; System.Text.Json serializes an anonymous
    // type's properties in declaration order, which is deterministic here
    // but is NOT the real JCS/RFC 8785 canonicalization the capability note
    // calls for. Placeholder for the real Trust-Ledger/KMS signing step.
    private static string ComputeCanonicalHash(Credential credential)
    {
        // A digitised paper certificate also signs its paper date and number
        // (so neither can be changed afterwards). The salt ("nonce") makes the
        // hash impossible to match by guessing a name and email, and
        // unlinkable once erased. Certificates from before salts existed keep
        // the original shape.
        var fields = new Dictionary<string, object?>
        {
            ["achievementId"] = credential.AchievementId,
            ["recipientName"] = credential.RecipientName,
            ["recipientEmail"] = credential.RecipientEmail,
            ["issuedAt"] = credential.IssuedAt,
        };
        if (credential.OriginalIssueDate is { } original)
        {
            fields["originalIssueDate"] = original.ToString("yyyy-MM-dd");
            fields["legacyCertificateNumber"] = credential.LegacyCertificateNumber;
        }
        if (credential.PersonalDataSalt is { } salt) fields["nonce"] = salt;
        // Who signs it, when it names signatories (older ones keep their shape).
        if (credential.SignatoriesJson is { } signatories) fields["signatories"] = signatories;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// The certificate's template names no signatory, and certificates must
// have one (Certificates:EnforceMandatoryContent). Callers answer 400.
public class MissingSignatoryException : InvalidOperationException
{
    public MissingSignatoryException(string title)
        : base($"The certificate template for '{title}' has no signatory. An institution admin must choose at least one on the template (Templates) before certificates can be made from it.") { }
}

// The certificate changed state (another request got there first) between
// being read and the step that was about to change it.
public class CredentialStateChangedException : InvalidOperationException
{
    public CredentialStateChangedException() : base("This certificate was changed by someone else just now. Reload it and try again.") { }
}
