import type { SignatoryName } from './certificate-preview.models';
import { PublicInstitutionProfile } from './tenant.models';
import type { FieldDefinition, TemplateKind } from './achievement.models';
import type { TemplateLayout } from './certificate-preview.models';

// Matches Certyfi.Domain.Enums.CredentialStatus.ToString() exactly.
// 'Expired' is never returned by the backend as a stored status — it's
// computed live (both server-side in VerificationController and
// client-side here, the same way) from an Active credential's expiresOn.
// AwaitingPayment: requested by a partner app whose key requires the student
// to pay (student portal) before it goes to an approver.
// Cancelled: withdrawn by the partner app that requested it, before issue.
// PendingFinalApproval: approved once, waiting for an institution admin's final approval.
export type CredentialStatus = 'AwaitingPayment' | 'PendingApproval' | 'PendingFinalApproval' | 'Rejected' | 'Cancelled' | 'Active' | 'Suspended' | 'Revoked';
export type DisplayStatus = CredentialStatus | 'Expired';

// Every leaf value is a string (or null); a table-type key's value is an
// array of flat string-keyed rows — see FieldSchemaValidator for the exact
// shape rules this must satisfy.
export type FieldValues = Record<string, string | null | Array<Record<string, string | null>>>;

export interface Credential {
  id: string;
  achievementId: string;
  achievementTitle: string;
  recipientName: string;
  recipientEmail: string;
  status: CredentialStatus;
  requestedByUserId: string;
  requestedByName: string;
  decidedByUserId: string | null;
  decidedByName: string | null;
  // The first of two approvals, when the template needs final approval.
  firstApprovedByUserId?: string | null;
  firstApprovedAt?: string | null;
  decidedAt: string | null;
  rejectionReason: string | null;
  issuedAt: string | null;
  expiresOn: string | null;
  suspensionReason: string | null;
  suspendedAt: string | null;
  revocationReason: string | null;
  revokedAt: string | null;
  createdAt: string;
  fieldValues: FieldValues;
  // Present regardless of credential status — this is what makes a
  // preview possible for a PendingApproval/Rejected credential, which the
  // public verify endpoint never resolves. fieldSchema is a snapshot
  // (Credential.FieldSchemaJson, captured at draft time), so it always
  // reflects what was actually issued even if the achievement is amended
  // later; achievementDescription is the achievement's CURRENT text (not
  // snapshotted — cosmetic only, unlike the field schema).
  achievementDescription: string | null;
  fieldSchema: FieldDefinition[];
  templateKind: TemplateKind;
  // Snapshotted from the template this credential's achievement was
  // created from (Achievement.Template*) — see CertificatePreviewData for
  // why layout is nullable but titleText/layoutJson themselves never are
  // once an achievement has been created.
  titleText: string;
  subtitleText: string | null;
  accentColor: string | null;
  fontFamily: string | null;
  layout: TemplateLayout | null;
  backgroundImageUrl: string | null;
  signatories?: SignatoryName[] | null;
  // Set once this credential has been approved — see
  // CredentialsController.Approve and ITenantSigningKeyProvider on the
  // backend. canonicalHash is the exact payload signature actually signs;
  // signatureKeyId is looked up against the tenant's public signing-keys
  // endpoint (see VerificationService.getSigningPublicKey) to get the
  // algorithm and the key itself for real, independent verification.
  canonicalHash: string | null;
  signature: string | null;
  signatureKeyId: string | null;
  // Printed on the certificate (XXXX-XXXX-XXXX) for look-up without the link.
  verificationCode: string;
  // Signed summary for the QR code's URL fragment (offline checks); null
  // until approved. See core/offline-verification.ts.
  offlineProof: string | null;
  // A revocation someone asked for, waiting for a second approver.
  pendingRevocation: PendingRevocation | null;
  // A digitised paper certificate: the paper's date (yyyy-mm-dd) and number,
  // and its import (approved as a batch, not one by one).
  originalIssueDate: string | null;
  legacyCertificateNumber: string | null;
  importBatchId: string | null;
  // Set once the recipient's personal data was erased on request.
  personalDataErasedAt: string | null;
  // The student this certificate belongs to, and when they paid (if it
  // needed payment).
  studentId: string | null;
  paidAt: string | null;
  cancelledAt: string | null;
  cancellationReason: string | null;
  paymentDueAt: string | null;
  cancelledForNonPayment: boolean;
}

export interface PendingRevocation {
  requestId: string;
  reasonLabel: string;
  note: string;
  requestedByName: string;
  requestedAt: string;
}

// The New Certificate form no longer makes staff pick an Achievement
// directly — it picks a CertificateTemplate and types a Title; the backend
// resolves or auto-creates the Achievement underneath (see
// CredentialsController.CreateFromTemplate).
export interface CreateCredentialFromTemplateRequest {
  templateId: string;
  title: string;
  recipientName: string;
  recipientEmail: string;
  fieldValues: FieldValues;
}

export function displayStatus(credential: Pick<Credential, 'status' | 'expiresOn'>): DisplayStatus {
  if (credential.status === 'Active' && credential.expiresOn && new Date(credential.expiresOn) < new Date()) {
    return 'Expired';
  }
  return credential.status;
}

export interface VerifyCredentialResponse {
  id: string;
  recipientName: string;
  achievementTitle: string;
  achievementDescription: string | null;
  institutionName: string;
  institutionLogoUrl: string | null;
  institutionPrimaryColor: string | null;
  status: DisplayStatus;
  issuedAt: string | null;
  expiresOn: string | null;
  fieldSchema: FieldDefinition[];
  fieldValues: FieldValues;
  templateKind: TemplateKind;
  titleText: string;
  subtitleText: string | null;
  accentColor: string | null;
  fontFamily: string | null;
  layout: TemplateLayout | null;
  backgroundImageUrl: string | null;
  signatories?: SignatoryName[] | null;
  canonicalHash: string | null;
  signature: string | null;
  signatureKeyId: string | null;
  // Looked up from the signing key itself, not stored on the credential —
  // see VerifyCredentialResponse's own comment on the backend.
  signatureAlgorithm: string | null;
  // Latest LedgerCheckpoint covering this credential's ledger entries.
  anchorStatus: AnchorStatus;
  anchorBlockHeight: number | null;
  anchorMerkleRootVerified: boolean;
  verificationCode: string;
  offlineProof: string | null;
  // Public part of the issuer's profile; null when the institution hasn't filled it in.
  institutionProfile: PublicInstitutionProfile | null;
  // A digitised paper certificate (issuedAt is when it was digitised).
  originalIssueDate: string | null;
  legacyCertificateNumber: string | null;
}

// ---------- Certificates list (server-side paging; CredentialsController.List) ----------

// One row of the list. The full record (Credential) is loaded when a row is opened.
export interface CredentialSummary {
  id: string;
  recipientName: string;
  recipientEmail: string;
  achievementId: string;
  achievementTitle: string;
  status: DisplayStatus;
  verificationCode: string;
  createdAt: string;
  issuedAt: string | null;
  expiresOn: string | null;
  requestedByUserId: string;
  requestedByName: string;
  source: 'Staff' | 'Api' | 'Import';
  originalIssueDate: string | null;
  legacyCertificateNumber: string | null;
}

export interface CredentialPage {
  items: CredentialSummary[];
  total: number;
  page: number;
  pageSize: number;
  // By status (including "Expired") with every filter except status applied.
  statusCounts: Record<string, number>;
}

// sort: created | issued | recipient | expires, "-" prefix = descending.
export interface CredentialListQuery {
  q?: string;
  status?: DisplayStatus | null;
  achievementId?: string | null;
  requestedBy?: string | null;
  // Requested by me / by someone else (with PendingApproval: "needs my approval").
  mine?: boolean | null;
  excludeMine?: boolean | null;
  // "Needs my approval": what this user may decide now (first or final approval).
  awaitingMe?: boolean | null;
  // One paper import's certificates / leave imported ones out ("needs my approval").
  importBatchId?: string | null;
  excludeImports?: boolean | null;
  // Issued in [issuedFrom, issuedBefore), ISO instants (the browser's local midnights).
  issuedFrom?: string | null;
  issuedBefore?: string | null;
  // Active certificates expiring within N days.
  expiringWithinDays?: number | null;
  source?: 'Staff' | 'Api' | null;
  apiClientId?: string | null;
  sort?: string;
  page?: number;
  pageSize?: number;
}

// GET /credentials/summary — sidebar badge and quick-view counts.
// pendingForMe: pending certificates I may approve (not my own requests).
export interface CredentialSummaryCounts {
  canApprove: boolean;
  pendingForMe: number;
  myRequests: number;
  myRequestsPending: number;
  myRequestsRejected: number;
  // Revocation requests someone else made that I may approve or reject.
  revocationsForMe: number;
  // Paper imports someone else made that I may approve.
  importsForMe: number;
}

// ---------- Bulk actions (POST /credentials/bulk/{operation}) ----------
// Suspend / revoke are not bulk: one at a time on the Revoke & suspend page.
export type BulkOperation = 'approve' | 'reject';

// Either ids, or filter = "everything matching" (up to 500).
export interface BulkActionRequest {
  ids?: string[];
  filter?: CredentialListQuery;
  reason?: string;
}

export interface BulkItemResult {
  id: string;
  recipientName: string | null;
  outcome: 'Done' | 'Skipped';
  reason: string | null;
}

export interface BulkActionResponse {
  action: BulkOperation;
  done: number;
  skipped: number;
  results: BulkItemResult[];
}

export interface FilterOption {
  id: string;
  name: string;
}

export interface CredentialFilterOptions {
  achievements: FilterOption[];
  requesters: FilterOption[];
  // Partner API keys that have certificates.
  apiClients: FilterOption[];
}

export interface VerificationLookupResponse {
  tenantSlug: string;
  credentialId: string;
}

export interface VerificationEvent {
  channel: 'VerifyPage' | 'CodeLookup' | 'CredentialDownload' | 'Api' | string;
  result: string;
  occurredAt: string;
}

export interface CredentialVerificationHistory {
  total: number;
  verifyPage: number;
  codeLookup: number;
  credentialDownload: number;
  api: number;
  lastVerifiedAt: string | null;
  recent: VerificationEvent[];
}

export type AnchorStatus = 'NotYetCheckpointed' | 'Pending' | 'Confirmed' | 'Failed';

export interface TenantSigningPublicKey {
  keyId: string;
  algorithm: string;
  publicKeyPem: string;
}
