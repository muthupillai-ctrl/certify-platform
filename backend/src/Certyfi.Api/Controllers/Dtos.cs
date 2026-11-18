namespace Certyfi.Api.Controllers;

public record TenantLoginRequest(string Email, string Password);
public record PlatformLoginRequest(string Email, string Password);
// PasswordChangeRequired: signed in with a temporary password — the token
// only works for POST /auth/change-password until a new one is chosen.
public record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, bool PasswordChangeRequired = false);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// Two Institution Admins, both required: final approval and rule changes
// need two different admins.
public record CreateTenantRequest(string Name, string Slug, string InstitutionAdminEmail, string InstitutionAdminInitialPassword,
    string? InstitutionAdminFullName = null,
    string? SecondAdminEmail = null, string? SecondAdminFullName = null, string? SecondAdminInitialPassword = null);
// Ahamsys adds an Institution Admin to an institution.
public record AddInstitutionAdminRequest(string Email, string FullName, string InitialPassword);
public record TenantResponse(Guid Id, string Name, string Slug, string Status, DateTimeOffset CreatedAt);

public record CreateUserRequest(string Email, string FullName, string InitialPassword, string Role);
public record UserResponse(Guid Id, string Email, string FullName, bool IsActive, IEnumerable<string> Roles);
public record UpdateUserRequest(string FullName, string Email, IReadOnlyList<string> Roles);
// TemporaryPassword is shown to the admin once, to hand to the user.
public record ResetPasswordResponse(Certyfi.Infrastructure.Services.TenantUserDetail User, string TemporaryPassword);

public record CreateAchievementRequest(string Title, string? Description, string? CriteriaNarrative, int? ValidityPeriodMonths, string? FieldSchemaJson, Guid? TemplateId);
public record UpdateAchievementRequest(string Title, string? Description, string? CriteriaNarrative, int? ValidityPeriodMonths, string? FieldSchemaJson);
public record AchievementResponse(Guid Id, string Title, string? Description, string? CriteriaNarrative, int? ValidityPeriodMonths, bool IsActive, string FieldSchemaJson, string TemplateKind);

public record CreateCertificateTemplateRequest(
    string Name, string? Description, string Kind, string? FieldSchemaJson,
    string? TitleText = null, string? SubtitleText = null, string? AccentColor = null, string? FontFamily = null, string? LayoutJson = null,
    string? BackgroundImageUrl = null, IReadOnlyList<Guid>? SignatoryIds = null,
    // Null leaves it as it is. Turning it off asks a second admin (ControlRequest).
    bool? RequiresFinalApproval = null);
public record UpdateCertificateTemplateRequest(
    string Name, string? Description, string Kind, string? FieldSchemaJson,
    string? TitleText = null, string? SubtitleText = null, string? AccentColor = null, string? FontFamily = null, string? LayoutJson = null,
    string? BackgroundImageUrl = null, IReadOnlyList<Guid>? SignatoryIds = null,
    // Null leaves it as it is. Turning it off asks a second admin (ControlRequest).
    bool? RequiresFinalApproval = null);
public record CertificateTemplateResponse(
    Guid Id, string Name, string? Description, string Kind, string FieldSchemaJson, bool IsActive,
    string TitleText, string? SubtitleText, string? AccentColor, string? FontFamily, string LayoutJson, string? BackgroundImageUrl,
    IReadOnlyList<Guid> SignatoryIds, IReadOnlyList<SignatoryName> Signatories,
    bool RequiresFinalApproval = false,
    // Switching final approval off was asked for and waits for a second admin.
    bool FinalApprovalOffPending = false);

// ---------- Signatories ----------
public record SignatoryResponse(Guid Id, string Name, string Designation, IReadOnlyList<string> UsedByTemplates);
public record SaveSignatoryRequest(string Name, string Designation);
// As printed on a certificate (and kept on it: Credential.SignatoriesJson).
public record SignatoryName(string Name, string Designation)
{
    public static IReadOnlyList<SignatoryName>? FromSnapshot(string? json) =>
        Certyfi.Infrastructure.Services.SignatorySnapshot.Read(json)?.Select(e => new SignatoryName(e.Name, e.Designation)).ToList();
}

public record CreateCredentialRequest(Guid AchievementId, string RecipientName, string RecipientEmail, string? FieldValuesJson);
public record CreateCredentialFromTemplateRequest(Guid TemplateId, string Title, string RecipientName, string RecipientEmail, string? FieldValuesJson);
public record RejectCredentialRequest(string Reason);
// ConfirmCode: the certificate's code, typed by the user to confirm.
public record SuspendCredentialRequest(string Reason, string? ConfirmCode);
public record ReactivateCredentialRequest(string Note, string? ConfirmCode);

public record CredentialResponse(
    Guid Id, Guid AchievementId, string AchievementTitle, string RecipientName, string RecipientEmail,
    string Status, Guid RequestedByUserId, string RequestedByName,
    Guid? DecidedByUserId, string? DecidedByName, DateTimeOffset? DecidedAt, string? RejectionReason,
    DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresOn,
    string? SuspensionReason, DateTimeOffset? SuspendedAt,
    string? RevocationReason, DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt, string FieldValuesJson,
    string? AchievementDescription, string FieldSchemaJson, string TemplateKind,
    string TitleText, string? SubtitleText, string? AccentColor, string? FontFamily, string LayoutJson, string? BackgroundImageUrl,
    string? CanonicalHash, string? Signature, string? SignatureKeyId,
    string VerificationCode, string? OfflineProof,
    PendingRevocationResponse? PendingRevocation = null,
    DateOnly? OriginalIssueDate = null, string? LegacyCertificateNumber = null, Guid? ImportBatchId = null,
    DateTimeOffset? PersonalDataErasedAt = null,
    Guid? StudentId = null, DateTimeOffset? PaidAt = null,
    DateTimeOffset? CancelledAt = null, string? CancellationReason = null,
    DateTimeOffset? PaymentDueAt = null, bool CancelledForNonPayment = false,
    IReadOnlyList<SignatoryName>? Signatories = null);

public record ErasePersonalDataRequest(string? ConfirmCode);

// A revocation someone has asked for, waiting for a second person.
public record PendingRevocationResponse(Guid RequestId, string ReasonLabel, string Note, string RequestedByName, DateTimeOffset RequestedAt);

// A certificate's public verification history: counts by channel plus the
// most recent events (see VerificationEvent — no verifier identity).
public record CredentialVerificationHistoryResponse(
    int Total, int VerifyPage, int CodeLookup, int CredentialDownload, int Api,
    DateTimeOffset? LastVerifiedAt, IReadOnlyList<VerificationEventResponse> Recent);

// CanonicalHash is what Signature actually signs — without it here, real
// independent verification (the whole point of signing) would be
// impossible: a verifier has no other way to learn the exact bytes the
// tenant's key signed. SignatureAlgorithm is looked up from the signing key
// itself (not stored on Credential) — see VerificationController — so a
// verifier always knows which algorithm to use even if a tenant's key
// algorithm changes in the future for keys issued after that point.
// AnchorStatus mirrors the tenant's most recent LedgerCheckpoint covering
// this credential's ledger entry: "NotYetCheckpointed" (no checkpoint has
// reached this entry's sequence number yet — checkpoints run hourly, see
// LedgerAnchorBackgroundService), "Pending" (submitted to OpenTimestamps,
// awaiting Bitcoin confirmation), or "Confirmed" (a real Bitcoin block's
// merkle root was independently fetched and verified to match — see
// AnchorMerkleRootVerified). AnchorBlockHeight/AnchorMerkleRootVerified are
// only meaningful once AnchorStatus is "Confirmed".
public record VerifyCredentialResponse(
    Guid Id, string RecipientName, string AchievementTitle, string? AchievementDescription,
    string InstitutionName, string? InstitutionLogoUrl, string? InstitutionPrimaryColor,
    string Status, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresOn,
    string FieldSchemaJson, string FieldValuesJson, string TemplateKind,
    string TitleText, string? SubtitleText, string? AccentColor, string? FontFamily, string LayoutJson, string? BackgroundImageUrl,
    string? CanonicalHash, string? Signature, string? SignatureKeyId, string? SignatureAlgorithm,
    string AnchorStatus, int? AnchorBlockHeight, bool AnchorMerkleRootVerified,
    string VerificationCode, string? OfflineProof, PublicInstitutionProfile? InstitutionProfile,
    // A digitised paper certificate: the paper's own date and number
    // (IssuedAt is when it was digitised and signed).
    DateOnly? OriginalIssueDate = null, string? LegacyCertificateNumber = null,
    IReadOnlyList<SignatoryName>? Signatories = null);

// Code look-up result: enough to open /verify/{TenantSlug}/{CredentialId}.
public record VerificationLookupResponse(string TenantSlug, Guid CredentialId);

public record VerificationEventResponse(string Channel, string Result, DateTimeOffset OccurredAt);

public record TenantSigningPublicKeyResponse(string KeyId, string Algorithm, string PublicKeyPem);

// Name: the institution's display name, for previews staff see (read-only here).
public record TenantBrandingResponse(string? LogoUrl, string? PrimaryColor, string? Name = null);
public record UpdateTenantBrandingRequest(string? LogoUrl, string? PrimaryColor);

public record UploadResponse(string Url);

public record LedgerEntryResponse(
    long SequenceNumber, Guid Id, string EventType, Guid ActorUserId, string ActorUserFullName,
    DateTimeOffset OccurredAt, string EventDataJson, string PreviousEntryHash, string EntryHash);

// Ledger integrity: see LedgerIntegrityReport (Certyfi.Infrastructure.Services).

// ---------- API keys (InstitutionAdmin, JWT) ----------
// RequiresPayment: see ApiClient.RequiresPayment.
public record CreateApiClientRequest(string Name, IReadOnlyList<string> Scopes, bool AutoIssue, bool RequiresPayment = false);
public record ApiClientResponse(
    Guid Id, string Name, string KeyPrefix, IReadOnlyList<string> Scopes, bool AutoIssue,
    string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt,
    int CredentialCount, bool RequiresPayment = false,
    // Webhook: where status changes are sent (null = none), and whether the
    // last call that finished was refused or gave up (shown as a warning).
    string? WebhookUrl = null, bool WebhookFailing = false,
    // "Issue without approval" was asked for and waits for a second
    // institution admin to confirm it (ControlRequest).
    bool AutoIssuePending = false);
// ApiKey is the only time the plaintext key is ever returned.
public record CreateApiClientResponse(ApiClientResponse Client, string ApiKey);

// ---------- Partner API (API key) ----------
// FieldValues is a JSON object matching the template's field schema — the
// partner sends real JSON, not the JSON-in-a-string the staff UI uses.
// StudentId is the partner's own id for the student (see Student.StudentNumber).
public record PartnerIssueCredentialRequest(
    Guid TemplateId, string Title, string RecipientName, string RecipientEmail,
    System.Text.Json.JsonElement? FieldValues, string? ExternalReference, string? StudentId = null);
public record PartnerRevokeCredentialRequest(string Reason);
public record PartnerCancelCredentialRequest(string Reason);
public record PartnerTemplateResponse(Guid Id, string Name, string? Description, string Kind, System.Text.Json.JsonElement FieldSchema);
// Url opens Certyfi's own drawing of the template with sample data; it
// stops working at ExpiresAt, so ask for a fresh one each time it's shown.
public record PartnerTemplatePreviewLinkResponse(string Url, DateTimeOffset ExpiresAt);
// What the public template-preview page draws: the template's design plus
// the institution's name and branding. Same JSON-in-a-string convention as
// CertificateTemplateResponse.
public record TemplatePreviewResponse(
    string TemplateName, string Kind, string FieldSchemaJson,
    string TitleText, string? SubtitleText, string? AccentColor, string? FontFamily, string LayoutJson, string? BackgroundImageUrl,
    string InstitutionName, string? InstitutionLogoUrl, string? InstitutionPrimaryColor, DateTimeOffset ExpiresAt,
    IReadOnlyList<SignatoryName>? Signatories = null);
// VerificationUrl is set only once the credential is publicly verifiable
// (Active/Suspended/Revoked) and PublicApp:BaseUrl is configured.
public record PartnerCredentialResponse(
    Guid Id, string Status, string Title, string RecipientName, string RecipientEmail, string? ExternalReference,
    DateTimeOffset CreatedAt, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresOn, DateTimeOffset? RevokedAt,
    string? VerificationUrl, string VerificationCode,
    // A revocation this key asked for is waiting for a person to approve it.
    bool RevocationRequested = false,
    // The partner's id for the student. Certyfi emails the student their
    // portal link itself (see StudentNotifications).
    string? StudentId = null, DateTimeOffset? PaidAt = null, DateTimeOffset? CancelledAt = null,
    // AwaitingPayment: pay by this time or it's cancelled (PaymentDays is the
    // institution's setting it came from). CancelledForNonPayment: Certyfi
    // cancelled it at that deadline (rather than you, via /cancel).
    DateTimeOffset? PaymentDueAt = null, int? PaymentDays = null, bool CancelledForNonPayment = false, string? CancellationReason = null,
    // What the student should do now, for the partner app to show them
    // (Certyfi has emailed them already). See PartnerNextStep.
    PartnerNextStep? NextStep = null);

// Code: a fixed value to act on (check_email_to_pay, awaiting_approval,
// issued, rejected, cancelled, suspended, revoked, expired). Message: a
// sentence for the student, ready to show as is.
public record PartnerNextStep(string Code, string Message);

// ---------- Anchoring troubleshooting (SuperAdmin) ----------
// Intervals in minutes; 0 until the background job has started (it waits
// ~30 s after startup). Run records are this process's only — empty after
// a restart until the first run.
public record AnchoringStatusResponse(
    DateTimeOffset ProcessStartedAt, DateTimeOffset? BackgroundJobStartedAt,
    double CheckpointIntervalMinutes, double UpgradeIntervalMinutes,
    Certyfi.Infrastructure.Services.AnchoringRun? LastCheckpointRun, Certyfi.Infrastructure.Services.AnchoringRun? LastUpgradeRun,
    Certyfi.Infrastructure.Services.AnchoringCounts Counts);

// ---------- Institution profile & management ----------
public record SuspendTenantRequest(string Reason);

// The full profile — for the institution's admins and SuperAdmins only.
public record TenantProfileResponse(
    string Name, string? LegalName, string? WebsiteUrl, string? RegistrationNumber, string? AccreditationBody, string? About,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Country,
    string? ContactEmail, string? ContactPhone, DateTimeOffset? ProfileUpdatedAt)
{
    public static TenantProfileResponse From(Certyfi.Domain.Entities.Tenant t) => new(
        t.Name, t.LegalName, t.WebsiteUrl, t.RegistrationNumber, t.AccreditationBody, t.About,
        t.AddressLine1, t.AddressLine2, t.City, t.Region, t.PostalCode, t.Country, t.ContactEmail, t.ContactPhone, t.ProfileUpdatedAt);
}

public record TenantDetailResponse(
    Guid Id, string Name, string Slug, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt,
    DateTimeOffset? SuspendedAt, string? SuspensionReason, TenantProfileResponse Profile,
    int UserCount, int ActiveUserCount, int ActiveCertificateCount, int ActiveApiKeyCount);

// The public part of the profile, shown on verification pages. Contact
// email, phone and street address are deliberately left out.
public record PublicInstitutionProfile(
    string? LegalName, string? WebsiteUrl, string? RegistrationNumber, string? AccreditationBody, string? About,
    string? City, string? Country)
{
    public static PublicInstitutionProfile? From(Certyfi.Domain.Entities.Tenant t)
    {
        var p = new PublicInstitutionProfile(t.LegalName, t.WebsiteUrl, t.RegistrationNumber, t.AccreditationBody, t.About, t.City, t.Country);
        return p == new PublicInstitutionProfile(null, null, null, null, null, null, null) ? null : p;
    }
}

// ---------- Certificates list (paged) ----------
// Query string for GET /credentials. Status: a CredentialStatus name or
// "Expired". Sort: created | issued | recipient | expires ("-" = descending).
public class CredentialListQuery
{
    public string? Q { get; set; }
    public string? Status { get; set; }
    public Guid? AchievementId { get; set; }
    public Guid? RequestedBy { get; set; }
    // Mine: requested by the signed-in user. ExcludeMine: requested by
    // someone else (with status=PendingApproval: "needs my approval").
    public bool? Mine { get; set; }
    public bool? ExcludeMine { get; set; }
    // "Needs my approval": what this user may decide now: first approval
    // (Approver) or final approval (Institution admin), never their own.
    public bool? AwaitingMe { get; set; }
    // Certificates from one paper import; ExcludeImports leaves imported ones out.
    public Guid? ImportBatchId { get; set; }
    public bool? ExcludeImports { get; set; }
    // Issued in [IssuedFrom, IssuedBefore). Instants, not dates: the browser
    // sends its own local midnights, so "this month" means the user's month.
    public DateTimeOffset? IssuedFrom { get; set; }
    public DateTimeOffset? IssuedBefore { get; set; }
    // Active (not yet expired) certificates that expire within N days.
    public int? ExpiringWithinDays { get; set; }
    // "Staff" or "Api"; ApiClientId narrows to one Partner API key.
    public string? Source { get; set; }
    public Guid? ApiClientId { get; set; }
    public string? Sort { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

// Source: "Staff" or "Api" (requested through a Partner API key).
public record CredentialSummaryResponse(
    Guid Id, string RecipientName, string RecipientEmail, Guid AchievementId, string AchievementTitle,
    string Status, string VerificationCode, DateTimeOffset CreatedAt, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresOn,
    Guid RequestedByUserId, string RequestedByName, string Source,
    DateOnly? OriginalIssueDate = null, string? LegacyCertificateNumber = null);

public record CredentialPageResponse(
    IReadOnlyList<CredentialSummaryResponse> Items, int Total, int Page, int PageSize, IReadOnlyDictionary<string, int> StatusCounts);

public record FilterOption(Guid Id, string Name);
public record CredentialFilterOptionsResponse(IReadOnlyList<FilterOption> Achievements, IReadOnlyList<FilterOption> Requesters, IReadOnlyList<FilterOption> ApiClients);

// Sidebar badge and quick-view counts (GET /credentials/summary).
// RevocationsForMe: revocation requests waiting that this user may decide.
public record CredentialSummaryCountsResponse(
    bool CanApprove, int PendingForMe, int MyRequests, int MyRequestsPending, int MyRequestsRejected, int RevocationsForMe,
    int ImportsForMe = 0);

// ---------- Bulk actions (POST /credentials/bulk/{action}) ----------
// Either Ids, or Filter ("everything matching", same fields as the list);
// Reason required for reject / suspend / revoke.
public record BulkActionRequest(IReadOnlyList<Guid>? Ids, CredentialListQuery? Filter, string? Reason);
// Outcome: "Done" or "Skipped" (Reason says why).
public record BulkItemResult(Guid Id, string? RecipientName, string Outcome, string? Reason);
public record BulkActionResponse(string Action, int Done, int Skipped, IReadOnlyList<BulkItemResult> Results);

// ---------- Dashboard (GET /dashboard) ----------
// RangeStart: first day (yyyy-MM-dd, local) of the 12 months the charts cover.
public record DashboardResponse(
    DateTimeOffset GeneratedAt,
    string TimeZone,
    string RangeStart,
    DashboardAttention Attention,
    DashboardTotals Totals,
    IReadOnlyList<DashboardMonthCount> IssuedByMonth,
    IReadOnlyList<DashboardNamedCount> IssuedByAchievement,
    IReadOnlyList<DashboardSourceCount> IssuedBySource,
    IReadOnlyList<DashboardMonthCount> VerificationsByMonth,
    IReadOnlyList<DashboardChannelCount> VerificationsByChannel,
    IReadOnlyList<DashboardTopVerified> MostVerified,
    int UnmatchedLookups);
// AwaitingPayment: students yet to pay (PaymentDueSoon of them due within
// 3 days). CancelledUnpaidLast30Days: cancelled at the payment deadline.
// WebhooksFailing: API keys whose last webhook call was refused or gave up.
public record DashboardAttention(int PendingApproval, DateTimeOffset? OldestPendingAt, int RejectedLast30Days, int ExpiringIn30Days,
    int AnchoringFailed, int AnchoringDelayed, int RevocationRequestsPending, int ImportsPending = 0,
    int AwaitingPayment = 0, int PaymentDueSoon = 0, int CancelledUnpaidLast30Days = 0, int WebhooksFailing = 0,
    // Waiting for an institution admin's final approval; rule changes
    // waiting for a second admin (ControlRequest).
    int PendingFinalApproval = 0, int ControlRequestsPending = 0);
public record DashboardTotals(int Active, int IssuedThisMonth, int IssuedLastMonth, int VerificationsThisMonth,
    int Students = 0, int StudentsSignedUp = 0);
public record DashboardMonthCount(string Month, int Count);
public record DashboardNamedCount(Guid Id, string Name, int Count);
// Source: "Staff" or "Api" (one row per API key).
public record DashboardSourceCount(string Source, Guid? ApiClientId, string Name, int Count);
public record DashboardChannelCount(string Channel, int Count);
public record DashboardTopVerified(Guid CredentialId, string RecipientName, string AchievementTitle, int Count);

// ---------- Revocation requests (two-person revoke; RevocationRequestsController) ----------
public record CreateRevocationRequest(Guid CredentialId, string ReasonCategory, string Note, string? ConfirmCode);
public record ApproveRevocationRequest(string? ConfirmCode);
public record RejectRevocationRequest(string Note);
// CanDecide: this user may approve/reject (pending, and not their own).
// CanWithdraw: this user asked and it's still pending.
public record RevocationRequestResponse(
    Guid Id, Guid CredentialId, string RecipientName, string AchievementTitle, string VerificationCode, string CredentialStatus,
    string ReasonCategory, string ReasonLabel, string Note,
    string RequestedByName, bool RequestedByPartnerApi, DateTimeOffset RequestedAt,
    string Status, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote,
    bool CanDecide, bool CanWithdraw);

// ---------- Verification activity (GET /verification-activity) ----------
// Result: a group name from VerificationActivityController.ResultGroups.
public class VerificationActivityQuery
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? Before { get; set; }
    public string? Channel { get; set; }
    public string? Result { get; set; }
    public Guid? CredentialId { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
public record VerificationActivityItemResponse(
    Guid Id, DateTimeOffset OccurredAt, string Channel, string Result,
    Guid? CredentialId, string? RecipientName, string? AchievementTitle, string? VerificationCode);
// AllResults: every result with the other filters; ResultCounts: by result group.
public record VerificationActivityPageResponse(
    IReadOnlyList<VerificationActivityItemResponse> Items, int Total, int Page, int PageSize,
    int AllResults, IReadOnlyDictionary<string, int> ResultCounts);

// ---------- Reports (ReportsController) ----------
// [From, Before): instants, the browser's local midnights.
public class ReportPeriod
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? Before { get; set; }
}
public record ReportCount(string Name, int Count);
// Rows: the first 100 (the CSV has them all).
public record IssuanceReportResponse(int Total, IReadOnlyList<ReportCount> ByAchievement, IReadOnlyList<ReportCount> ByRequester,
    IReadOnlyList<ReportCount> ByApprover, IReadOnlyList<ReportCount> BySource, IReadOnlyList<IssuanceReportRow> Rows);
public record IssuanceReportRow(DateTimeOffset IssuedAt, string RecipientName, string RecipientEmail, string AchievementTitle,
    string VerificationCode, string Status, string RequestedBy, string Source, string? ApprovedBy);
public record StatusChangesReportResponse(int Total, IReadOnlyList<ReportCount> ByChange, IReadOnlyList<StatusChangeReportRow> Rows);
public record StatusChangeReportRow(DateTimeOffset OccurredAt, string Change, string RecipientName, string AchievementTitle,
    string VerificationCode, string By, string? RequestedBy, string? Reason);
// Counts: by ledger event type (Requested, Approved, ...).
public record StaffActivityPerson(string Name, bool IsPartnerApi, IReadOnlyDictionary<string, int> Counts);
public record TwoPersonViolation(Guid CredentialId, string RecipientName, string Person, string Problem);
public record StaffActivityReportResponse(IReadOnlyList<StaffActivityPerson> People,
    int DecisionsChecked, IReadOnlyList<TwoPersonViolation> DecisionViolations,
    int RevocationsChecked, IReadOnlyList<TwoPersonViolation> RevocationViolations);

// ---------- Paper certificate import (ImportsController) ----------
// One spreadsheet row after the page's column mapping. Dates are as typed
// (see ImportRowValidator.TryParseDate); FieldValues is keyed by template field key.
public record ImportRow(string? RecipientName, string? RecipientEmail, string? AchievementTitle, string? OriginalIssueDate,
    string? LegacyNumber, string? ExpiresOn, Dictionary<string, string?>? FieldValues);
// DateFormat: DMY (default), MDY or YMD. Title: the achievement for rows without one.
public record ImportCheckRequest(Guid TemplateId, string? Title, string? DateFormat, IReadOnlyList<ImportRow> Rows);
// Row: 0-based index in the rows sent.
public record ImportRowResult(int Row, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public record ImportCheckResponse(int Ready, int WithErrors, int LikelyDuplicates, IReadOnlyList<ImportRowResult> Problems);
public record ImportParseResponse(string FileName, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);
public record CreateImportBatchRequest(Guid TemplateId, string FileName, int ExpectedRows);
// SkipLikelyDuplicates: leave out rows that match a certificate already imported.
public record ImportRowsRequest(string? Title, string? DateFormat, bool SkipLikelyDuplicates, IReadOnlyList<ImportRow> Rows, int FirstRowNumber);
public record ImportRowsResponse(int Imported, int Skipped, IReadOnlyList<ImportRowResult> Problems);
public record ApproveImportRequest(int? ConfirmCount);
public record RejectImportRequest(string? Note);
// Remaining: certificates of the batch still pending; the page calls again until 0.
public record ImportProgressResponse(string Status, int Done, int Remaining, int Failed);
public record ImportBatchResponse(
    Guid Id, string FileName, string TemplateName, int ExpectedRows, string Status,
    string CreatedByName, DateTimeOffset CreatedAt, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote,
    int Pending, int Issued, int Rejected, DateOnly? EarliestPaperDate, DateOnly? LatestPaperDate,
    bool CanApprove, bool CanWithdraw, bool CanContinueImport,
    // Waiting for final approval, and this institution admin may give it.
    bool CanFinalApprove = false);
public record ImportSampleRow(Guid Id, string RecipientName, string RecipientEmail, string AchievementTitle, DateOnly? OriginalIssueDate, string? LegacyCertificateNumber);
public record ImportBatchDetailResponse(ImportBatchResponse Batch, IReadOnlyList<ImportSampleRow> Sample, IReadOnlyList<ReportCount> ByAchievement);

// ---------- Private home page access (AccessController, HomeAccessNumbersController) ----------
public record EnterHomeAccessRequest(string? Mobile);
public record AddHomeAccessNumberRequest(string? Number, string? Label);
public record HomeAccessNumberResponse(Guid Id, string Number, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

// ---------- Student portal ----------
public record StudentSetupRequest(string Token, string Password);
public record StudentLoginRequest(string Email, string Password);
public record StudentChangePasswordRequest(string CurrentPassword, string NewPassword);
public record StudentLoginResponse(string AccessToken, DateTime ExpiresAtUtc, StudentProfileResponse Student);
// Email-only sign-in: SignedIn when the password matches at one
// institution; Institutions (to choose from) when it matches at several.
public record StudentSignInResponse(StudentLoginResponse? SignedIn, IReadOnlyList<StudentPortalInstitution>? Institutions);
public record StudentProfileResponse(Guid Id, string FullName, string Email, string? StudentNumber, string InstitutionName, string InstitutionSlug);
// What the set-password page shows. IsReset: a forgot-password link (vs.
// first set-up). AlreadySetUp: a set-up link for a student who has since
// chosen a password.
public record StudentWelcomeResponse(string FullName, string Email, string InstitutionName, string InstitutionSlug, bool AlreadySetUp, bool IsReset = false);
public record StudentForgotPasswordRequest(string Email);
public record StudentCertificateResponse(
    Guid Id, string Title, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt,
    DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresOn, string? VerificationPath, string? VerificationCode, string? RejectionReason,
    DateTimeOffset? PaymentDueAt = null, bool CancelledForNonPayment = false,
    IReadOnlyList<StudentCertificateField>? Fields = null);

// ---------- Students (staff) ----------
public record StudentListItemResponse(
    Guid Id, string? StudentNumber, string FullName, string Email, string Source, DateTimeOffset CreatedAt,
    bool SignInSetUp, DateTimeOffset? LastLoginAt, int CertificateCount);
public record StudentListResponse(IReadOnlyList<StudentListItemResponse> Items, int Total);
public record StudentCertificateSummary(Guid Id, string Title, string Status, DateTimeOffset CreatedAt, DateTimeOffset? IssuedAt, DateTimeOffset? PaidAt);
public record StudentChangeResponse(DateTimeOffset ChangedAt, string ChangedByName, string Field, string? OldValue, string? NewValue);
public record StudentDetailResponse(StudentListItemResponse Student, IReadOnlyList<StudentCertificateSummary> Certificates,
    string? Phone = null, IReadOnlyList<StudentChangeResponse>? Changes = null);
// Staff edit (StudentsController.Update). An empty Phone or StudentNumber clears it.
public record UpdateStudentRequest(string FullName, string Email, string? Phone, string? StudentNumber);
// Reset: forget the student's password and invalidate older links.
public record StudentSignInLinkRequest(bool Reset);
public record StudentSignInLinkResponse(string Url, bool IsWelcomeLink);
// Email: also email the link to the student (needs an email provider).
public record StudentEmailLinkResponse(bool Queued);

// Institution setting: days a student has to pay before an unpaid
// certificate is cancelled (0 = never). See Tenant.PaymentDays.
public record TenantPaymentSettings(int PaymentDays);

// ---------- Webhooks (per API key) ----------
// Secret: required when setting a webhook up; leave empty to keep the
// current one when only the URL changes.
public record SetWebhookRequest(string Url, string? Secret);
public record WebhookTestResponse(bool Ok, int? StatusCode, string? Error);
public record WebhookDeliveryResponse(
    Guid Id, Guid CredentialId, string CredentialStatus, DateTimeOffset CreatedAt, string State, int Attempts,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError, DateTimeOffset? DeliveredAt);

// Bulk set-up invitations (Students page). ToInvite = NotSignedUp minus
// those emailed a set-up link in the last RecentDays.
public record StudentInvitationPreview(int NotSignedUp, int RecentlyInvited, int ToInvite, int RecentDays, int MinutesToSend);
public record StudentInvitationResult(int Queued, int SkippedRecentlyInvited, int MinutesToSend);

// ---------- Platform accounts ----------
public record PlatformAccountChangeResponse(DateTimeOffset ChangedAt, string ChangedByName, string Field, string? OldValue, string? NewValue, Guid ChangedByAdminId = default);
public record PlatformAccountResponse(Guid Id, string Email, string FullName, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    DateTimeOffset? PasswordChangedAt, bool MustChangePassword, IReadOnlyList<PlatformAccountChangeResponse> Changes);
public record UpdatePlatformNameRequest(string FullName);
public record PlatformChangePasswordRequest(string CurrentPassword, string NewPassword);
public record PlatformChangeEmailRequest(string NewEmail, string CurrentPassword);
public record PlatformChangeEmailResponse(string SentTo);
public record PlatformConfirmEmailRequest(string Token);
public record PlatformConfirmEmailResponse(string Email);

public record PlatformAdminResponse(Guid Id, string Email, string FullName, bool IsActive, bool MustChangePassword,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, bool IsYou);
public record CreatePlatformAdminRequest(string Email, string FullName);
// TemporaryPassword is shown once; they choose their own at first sign-in.
public record PlatformAdminWithPasswordResponse(PlatformAdminResponse Admin, string TemporaryPassword);
public record PlatformForgotPasswordRequest(string Email);
public record PlatformResetPasswordRequest(string Token, string NewPassword);

// ---------- Student portal banners ----------
public record PortalBannerResponse(Guid Id, string Title, string ImageUrl, string? LinkUrl, string Side, int SortOrder,
    DateOnly? StartsOn, DateOnly? EndsOn, bool IsActive, int ClickCount, DateTimeOffset CreatedAt);
public record SavePortalBannerRequest(string Title, string ImageUrl, string? LinkUrl, string Side, DateOnly? StartsOn, DateOnly? EndsOn, bool IsActive);
public record ReorderPortalBannersRequest(IReadOnlyList<Guid> Left, IReadOnlyList<Guid> Right);
// What a student page shows.
public record StudentPortalBanner(Guid Id, string Title, string ImageUrl, string? LinkUrl, string Side);
public record StudentPortalInstitution(string Name, string Slug, string? LogoUrl, string? PrimaryColor);
// A certificate's own fields, for display and search in the student portal.
public record StudentCertificateField(string Label, string Value);

// ---------- Dual control and the institution audit log ----------
// CanDecide: pending and asked by someone else (this admin may confirm or refuse).
public record ControlRequestResponse(Guid Id, string Kind, Guid TargetId, string TargetName, string RequestedByName, DateTimeOffset RequestedAt,
    string Status, string? DecidedByName, DateTimeOffset? DecidedAt, string? Note, bool CanDecide, bool IsMine);
public record RefuseControlRequest(string? Note);
public record AuditLogEntryResponse(Guid Id, DateTimeOffset At, string ActorKind, string ActorName, string Action, string Subject, string? Details);
public record AuditLogPageResponse(IReadOnlyList<AuditLogEntryResponse> Items, int Total, int Page, int PageSize);
