// Paper certificate import (ImportsController).
export type DateFormat = 'DMY' | 'MDY' | 'YMD';

export interface ImportRow {
  recipientName: string | null;
  recipientEmail: string | null;
  achievementTitle: string | null;
  originalIssueDate: string | null;
  legacyNumber: string | null;
  expiresOn: string | null;
  fieldValues: Record<string, string | null>;
}

export interface ImportParseResult {
  fileName: string;
  headers: string[];
  rows: string[][];
  truncated: boolean;
}

// row: 0-based index into the file's data rows (the file row is row + 2).
export interface ImportRowResult {
  row: number;
  errors: string[];
  warnings: string[];
}

export interface ImportCheckResult {
  ready: number;
  withErrors: number;
  likelyDuplicates: number;
  problems: ImportRowResult[];
}

export interface ImportRowsResult {
  imported: number;
  skipped: number;
  problems: ImportRowResult[];
}

export type ImportBatchStatus = 'Importing' | 'PendingApproval' | 'Approving' | 'PendingFinalApproval' | 'FinalApproving' | 'Approved' | 'Rejecting' | 'Rejected' | 'Withdrawn';

export interface ImportBatch {
  id: string;
  fileName: string;
  templateName: string;
  expectedRows: number;
  status: ImportBatchStatus;
  createdByName: string;
  createdAt: string;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  pending: number;
  issued: number;
  rejected: number;
  earliestPaperDate: string | null;
  latestPaperDate: string | null;
  canApprove: boolean;
  canWithdraw: boolean;
  canContinueImport: boolean;
  // Waiting for final approval, and this institution admin may give it.
  canFinalApprove?: boolean;
}

export interface ImportBatchDetail {
  batch: ImportBatch;
  sample: {
    id: string; recipientName: string; recipientEmail: string; achievementTitle: string;
    originalIssueDate: string | null; legacyCertificateNumber: string | null;
  }[];
  byAchievement: { name: string; count: number }[];
}

export interface ImportProgress {
  status: ImportBatchStatus;
  done: number;
  remaining: number;
  failed: number;
}
