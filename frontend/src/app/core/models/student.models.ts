// Staff view of the institution's students (StudentsController).
export interface StudentListItem {
  id: string;
  studentNumber: string | null;
  fullName: string;
  email: string;
  source: 'Certificate' | 'PartnerApi';
  createdAt: string;
  signInSetUp: boolean;
  lastLoginAt: string | null;
  certificateCount: number;
}

export interface StudentList {
  items: StudentListItem[];
  total: number;
}

export interface StudentCertificateSummary {
  id: string;
  title: string;
  status: string;
  createdAt: string;
  issuedAt: string | null;
  paidAt: string | null;
}

export interface StudentChange {
  changedAt: string;
  changedByName: string;
  field: string;
  oldValue: string | null;
  newValue: string | null;
}

export interface StudentDetail {
  student: StudentListItem;
  certificates: StudentCertificateSummary[];
  phone: string | null;
  changes: StudentChange[];
}

export interface UpdateStudentRequest {
  fullName: string;
  email: string;
  phone: string | null;
  studentNumber: string | null;
}

export interface StudentSignInLink {
  url: string;
  isWelcomeLink: boolean;
}

// ---------- Student portal (the student's own view) ----------
export interface StudentProfile {
  id: string;
  fullName: string;
  email: string;
  studentNumber: string | null;
  institutionName: string;
  institutionSlug: string;
}

export interface StudentLoginResponse {
  accessToken: string;
  expiresAtUtc: string;
  student: StudentProfile;
}

export interface StudentWelcome {
  fullName: string;
  email: string;
  institutionName: string;
  institutionSlug: string;
  alreadySetUp: boolean;
  // A forgot-password link rather than first set-up.
  isReset: boolean;
}

export interface StudentCertificate {
  id: string;
  title: string;
  status: 'AwaitingPayment' | 'PendingApproval' | 'Rejected' | 'Cancelled' | 'Active' | 'Suspended' | 'Revoked' | 'Expired';
  createdAt: string;
  paidAt: string | null;
  issuedAt: string | null;
  expiresOn: string | null;
  verificationPath: string | null;
  verificationCode: string | null;
  rejectionReason: string | null;
  // Awaiting payment: pay by then or it's cancelled.
  paymentDueAt: string | null;
  cancelledForNonPayment: boolean;
  // The certificate's own text fields (roll number, course, grade...).
  fields: { label: string; value: string }[] | null;
}

// Shown beside the student pages; set up by the institution.
export interface StudentPortalBanner {
  id: string;
  title: string;
  imageUrl: string;
  linkUrl: string | null;
  side: 'Left' | 'Right';
}

export interface StudentPortalInstitution {
  name: string;
  slug: string;
  logoUrl: string | null;
  primaryColor: string | null;
}

// Bulk set-up invitations.
export interface StudentInvitationPreview {
  notSignedUp: number;
  recentlyInvited: number;
  toInvite: number;
  recentDays: number;
  minutesToSend: number;
}

export interface StudentInvitationResult {
  queued: number;
  skippedRecentlyInvited: number;
  minutesToSend: number;
}

// Email-only sign-in (/student-sign-in): signed in, or the institutions to
// choose from when the password matches at more than one.
export interface StudentSignInResponse {
  signedIn: StudentLoginResponse | null;
  institutions: StudentPortalInstitution[] | null;
}
