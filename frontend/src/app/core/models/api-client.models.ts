export interface ApiClient {
  id: string;
  name: string;
  keyPrefix: string;
  scopes: string[];
  autoIssue: boolean;
  // "Issue without approval" waits for the other institution admin to confirm.
  autoIssuePending?: boolean;
  // Students pay (student portal) before certificates go to an approver.
  requiresPayment: boolean;
  // Where status changes are sent (null = none), and whether the last
  // finished call was refused or gave up.
  webhookUrl: string | null;
  webhookFailing: boolean;
  createdByName: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
  credentialCount: number;
}

export interface CreateApiClientRequest {
  name: string;
  scopes: string[];
  autoIssue: boolean;
  requiresPayment: boolean;
}

// apiKey is returned only by the create call and is never retrievable again.
export interface CreateApiClientResponse {
  client: ApiClient;
  apiKey: string;
}

// Must mirror backend PartnerScope.All. The server validates scopes; this
// list only drives the form.
export const PARTNER_SCOPES: { value: string; label: string; description: string }[] = [
  { value: 'templates:read', label: 'Read templates', description: 'List certificate templates and their fields' },
  { value: 'credentials:read', label: 'Read credentials', description: 'Look up credentials this key created' },
  { value: 'credentials:write', label: 'Issue credentials', description: 'Create credentials' },
  { value: 'credentials:revoke', label: 'Revoke credentials', description: 'Revoke credentials this key created' },
];

export interface WebhookDelivery {
  id: string;
  credentialId: string;
  credentialStatus: string;
  createdAt: string;
  state: 'Pending' | 'Sending' | 'Delivered' | 'Failed' | 'Stopped';
  attempts: number;
  lastAttemptAt: string | null;
  nextAttemptAt: string | null;
  lastStatusCode: number | null;
  lastError: string | null;
  deliveredAt: string | null;
}

export interface WebhookTestResult {
  ok: boolean;
  statusCode: number | null;
  error: string | null;
}
