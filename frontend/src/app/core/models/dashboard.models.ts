// GET /dashboard — see DashboardController. Months are "yyyy-MM" in the
// time zone the page asked for; rangeStart (yyyy-MM-dd) is the first day of
// the 12 months the charts cover.
export interface DashboardMonthCount {
  month: string;
  count: number;
}

export interface Dashboard {
  generatedAt: string;
  timeZone: string;
  rangeStart: string;
  attention: {
    pendingApproval: number;
    oldestPendingAt: string | null;
    rejectedLast30Days: number;
    expiringIn30Days: number;
    anchoringFailed: number;
    anchoringDelayed: number;
    revocationRequestsPending: number;
    importsPending: number;
  // Students yet to pay (paymentDueSoon of them within 3 days), cancelled at
  // the deadline in the last 30 days, and API keys whose webhook is failing.
  awaitingPayment: number;
  paymentDueSoon: number;
  cancelledUnpaidLast30Days: number;
  webhooksFailing: number;
  // Waiting for an institution admin's final approval; safeguard changes waiting for a second admin.
  pendingFinalApproval?: number;
  controlRequestsPending?: number;
  };
  totals: {
    active: number;
    issuedThisMonth: number;
    issuedLastMonth: number;
    verificationsThisMonth: number;
  students: number;
  studentsSignedUp: number;
  };
  issuedByMonth: DashboardMonthCount[];
  issuedByAchievement: { id: string; name: string; count: number }[];
  issuedBySource: { source: 'Staff' | 'Api'; apiClientId: string | null; name: string; count: number }[];
  verificationsByMonth: DashboardMonthCount[];
  verificationsByChannel: { channel: string; count: number }[];
  mostVerified: { credentialId: string; recipientName: string; achievementTitle: string; count: number }[];
  unmatchedLookups: number;
}
