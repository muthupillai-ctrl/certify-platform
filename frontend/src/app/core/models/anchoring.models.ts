// Mirrors AnchoringController / AnchoringDiagnosticsService on the API side.

export interface AnchoringRun {
  trigger: 'Scheduled' | 'Manual' | string;
  startedAt: string;
  finishedAt: string;
  tenantsProcessed: number;
  count: number;
  tenantErrors: string[];
}

export interface AnchoringCounts {
  pending: number;
  confirmed: number;
  failed: number;
  needsAttention: number;
}

export interface AnchoringStatus {
  processStartedAt: string;
  backgroundJobStartedAt: string | null;
  checkpointIntervalMinutes: number;
  upgradeIntervalMinutes: number;
  lastCheckpointRun: AnchoringRun | null;
  lastUpgradeRun: AnchoringRun | null;
  counts: AnchoringCounts;
}

export interface EndpointProbe {
  url: string;
  ok: boolean;
  httpStatus: number | null;
  error: string | null;
  elapsedMs: number;
}

export interface ConnectivityReport {
  calendars: EndpointProbe[];
  blockExplorer: EndpointProbe;
  bitcoinTipHeight: number | null;
}

export type CheckpointFilter = 'attention' | 'all' | 'pending' | 'confirmed' | 'failed';

export interface CheckpointRow {
  tenantSlug: string;
  tenantName: string;
  id: string;
  upToSequenceNumber: number;
  status: 'Pending' | 'Confirmed' | 'Failed' | string;
  createdAt: string;
  confirmedAt: string | null;
  bitcoinBlockHeight: number | null;
  merkleRootVerified: boolean;
  lastCheckedAt: string | null;
  checkAttempts: number;
  lastCheckResult: string | null;
  attentionReason: string | null;
}

export interface DiagnosticStep {
  name: string;
  status: 'ok' | 'info' | 'warn' | 'fail';
  detail: string;
}

export interface CalendarOutcome {
  calendarUrl: string;
  commitmentHex: string;
  status: 'Upgraded' | 'Pending' | 'Error' | 'Skipped';
  httpStatus: number | null;
  error: string | null;
  elapsedMs: number;
  hasBitcoinAttestation: boolean;
}

export interface BitcoinBlockCheck {
  verified: boolean;
  blockHash: string | null;
  expectedMerkleRootHex: string;
  explorerMerkleRootHex: string | null;
  error: string | null;
}

export interface CheckpointDiagnosis {
  tenantSlug: string;
  checkpointId: string;
  status: string;
  upToSequenceNumber: number;
  createdAt: string;
  pendingAttestations: number;
  summary: string;
  steps: DiagnosticStep[];
  calendars: CalendarOutcome[];
  bitcoinBlock: BitcoinBlockCheck | null;
  bitcoinTipHeight: number | null;
}
