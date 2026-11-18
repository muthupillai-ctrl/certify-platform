export interface LedgerEntry {
  sequenceNumber: number;
  id: string;
  eventType: string;
  actorUserId: string;
  actorUserFullName: string;
  occurredAt: string;
  eventDataJson: string;
  previousEntryHash: string;
  entryHash: string;
}

export interface LedgerIntegrityResponse {
  intact: boolean;
  brokenAtSequenceNumber: number | null;
  // One line for the banner; `problem` has the details.
  reason: string | null;
  totalEntries: number;
  // Entries written before timestamps were rounded to microseconds, verified
  // by restoring the one digit the database dropped (see the API's
  // LedgerIntegrityService). Content checked as usual.
  precisionAdjustedEntries: number;
  checkedAt: string;
  problem: LedgerIntegrityProblem | null;
}

export interface LedgerIntegrityProblem {
  kind: 'ContentChanged' | 'ChainBroken' | string;
  sequenceNumber: number;
  eventType: string;
  credentialId: string;
  recipientName: string | null;
  achievementTitle: string | null;
  certificateCode: string | null;
  occurredAt: string;
  actorName: string | null;
  explanation: string;
  whatToDo: string;
  storedEntryHash: string;
  recomputedEntryHash: string;
  expectedPreviousHash: string;
  storedPreviousHash: string;
}
