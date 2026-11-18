// Verification activity (GET /verification-activity) and reports (GET /reports/*).
export type ActivityResultGroup = 'Valid' | 'CodeMatched' | 'Suspended' | 'Revoked' | 'Expired' | 'NotFound' | 'Attention';

export interface VerificationActivityItem {
  id: string;
  occurredAt: string;
  channel: string;
  // Active | Found | Suspended | Revoked | Expired | NotFound
  result: string;
  credentialId: string | null;
  recipientName: string | null;
  achievementTitle: string | null;
  verificationCode: string | null;
}

export interface VerificationActivityPage {
  items: VerificationActivityItem[];
  total: number;
  page: number;
  pageSize: number;
  allResults: number;
  resultCounts: Record<ActivityResultGroup, number>;
}

export interface VerificationActivityQuery {
  from?: string | null;
  before?: string | null;
  channel?: string | null;
  result?: ActivityResultGroup | null;
  page?: number;
  pageSize?: number;
}

export interface ReportCount {
  name: string;
  count: number;
}

export interface IssuanceReport {
  total: number;
  byAchievement: ReportCount[];
  byRequester: ReportCount[];
  byApprover: ReportCount[];
  bySource: ReportCount[];
  rows: {
    issuedAt: string; recipientName: string; recipientEmail: string; achievementTitle: string;
    verificationCode: string; status: string; requestedBy: string; source: string; approvedBy: string | null;
  }[];
}

export interface StatusChangesReport {
  total: number;
  byChange: ReportCount[];
  rows: {
    occurredAt: string; change: string; recipientName: string; achievementTitle: string;
    verificationCode: string; by: string; requestedBy: string | null; reason: string | null;
  }[];
}

export interface TwoPersonViolation {
  credentialId: string;
  recipientName: string;
  person: string;
  problem: string;
}

export interface StaffActivityReport {
  people: { name: string; isPartnerApi: boolean; counts: Record<string, number> }[];
  decisionsChecked: number;
  decisionViolations: TwoPersonViolation[];
  revocationsChecked: number;
  revocationViolations: TwoPersonViolation[];
}

export type ReportKind = 'issuance' | 'status-changes' | 'staff-activity';
