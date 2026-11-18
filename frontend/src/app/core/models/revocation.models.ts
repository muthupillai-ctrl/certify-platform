// Two-person revocation (RevocationRequestsController).
export type RevocationReasonCategory =
  | 'IssuedInError'
  | 'IncorrectDetails'
  | 'Misconduct'
  | 'HolderRequest'
  | 'PartnerSystem'
  | 'Other';

// What staff can choose ("PartnerSystem" is set by the Partner API only).
export const REVOCATION_REASONS: { id: RevocationReasonCategory; label: string }[] = [
  { id: 'IssuedInError', label: 'Issued in error' },
  { id: 'IncorrectDetails', label: 'Incorrect details' },
  { id: 'Misconduct', label: 'Fraud or misconduct' },
  { id: 'HolderRequest', label: 'Requested by the holder' },
  { id: 'Other', label: 'Other' },
];

export type RevocationRequestStatus = 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn';

export interface RevocationRequest {
  id: string;
  credentialId: string;
  recipientName: string;
  achievementTitle: string;
  verificationCode: string;
  credentialStatus: string;
  reasonCategory: RevocationReasonCategory;
  reasonLabel: string;
  note: string;
  requestedByName: string;
  requestedByPartnerApi: boolean;
  requestedAt: string;
  status: RevocationRequestStatus;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  // I may approve/reject (pending and not mine) / withdraw (pending and mine).
  canDecide: boolean;
  canWithdraw: boolean;
}
