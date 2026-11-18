import { Component, OnInit, signal } from '@angular/core';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiClientsService } from '../../../core/services/api-clients.service';
import { ApiClient, WebhookDelivery, WebhookTestResult, PARTNER_SCOPES } from '../../../core/models/api-client.models';
import { environment } from '../../../../environments/environment';

// Partner API keys. A key is shown in full exactly once, straight after it
// is created — the server only stores its hash. Keys can't be edited:
// changing what a partner may do means issuing a new key and revoking the old.
@Component({
  selector: 'app-api-keys',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './api-keys.component.html',
})
export class ApiKeysComponent implements OnInit {
  readonly clients = signal<ApiClient[]>([]);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly showForm = signal(false);
  readonly submitting = signal(false);
  readonly newKey = signal<{ name: string; apiKey: string } | null>(null);
  readonly copied = signal(false);

  // Webhook window for one key.
  readonly webhookClient = signal<ApiClient | null>(null);
  readonly deliveries = signal<WebhookDelivery[]>([]);
  readonly webhookBusy = signal(false);
  readonly webhookError = signal<string | null>(null);
  readonly webhookNote = signal<string | null>(null);
  readonly testResult = signal<WebhookTestResult | null>(null);
  webhookUrl = '';
  webhookSecret = '';
  readonly scopeOptions = PARTNER_SCOPES;
  readonly docsUrl = `${environment.apiUrl}/docs`;

  name = '';
  selectedScopes: Record<string, boolean> = {};
  autoIssue = false;
  requiresPayment = true;

  constructor(private apiClientsService: ApiClientsService) {
    this.resetForm();
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.apiClientsService.list().subscribe({
      next: (clients) => {
        this.clients.set(clients);
        this.loading.set(false);
      },
      error: () => {
        this.errorMessage.set('Failed to load API keys.');
        this.loading.set(false);
      },
    });
  }

  toggleForm(): void {
    this.errorMessage.set(null);
    this.showForm.set(!this.showForm());
  }

  get chosenScopes(): string[] {
    return this.scopeOptions.map((s) => s.value).filter((v) => this.selectedScopes[v]);
  }

  // Auto-issue only makes sense for a key that can issue.
  get canAutoIssue(): boolean {
    return !!this.selectedScopes['credentials:write'];
  }

  submit(): void {
    this.errorMessage.set(null);
    this.submitting.set(true);

    this.apiClientsService
      .create({
        name: this.name, scopes: this.chosenScopes,
        autoIssue: this.canAutoIssue && this.autoIssue, requiresPayment: this.canAutoIssue && this.requiresPayment,
      })
      .subscribe({
        next: (response) => {
          this.submitting.set(false);
          this.showForm.set(false);
          this.copied.set(false);
          this.newKey.set({ name: response.client.name, apiKey: response.apiKey });
          this.resetForm();
          this.load();
        },
        error: (err) => {
          this.submitting.set(false);
          this.errorMessage.set(err.error?.error ?? 'Failed to create API key.');
        },
      });
  }

  copyKey(): void {
    const key = this.newKey()?.apiKey;
    if (!key) return;
    navigator.clipboard.writeText(key).then(() => this.copied.set(true));
  }

  dismissKey(): void {
    this.newKey.set(null);
  }

  openWebhook(client: ApiClient): void {
    this.webhookClient.set(client);
    this.webhookUrl = client.webhookUrl ?? '';
    this.webhookSecret = '';
    this.webhookError.set(null);
    this.webhookNote.set(null);
    this.testResult.set(null);
    this.loadDeliveries(client.id);
  }

  closeWebhook(): void {
    this.webhookClient.set(null);
  }

  loadDeliveries(id: string): void {
    this.apiClientsService.webhookDeliveries(id).subscribe({ next: (d) => this.deliveries.set(d) });
  }

  saveWebhook(): void {
    const client = this.webhookClient();
    if (!client) return;
    this.webhookBusy.set(true);
    this.webhookError.set(null);
    this.webhookNote.set(null);
    this.apiClientsService.setWebhook(client.id, this.webhookUrl.trim(), this.webhookSecret.trim() || null).subscribe({
      next: (updated) => this.webhookSaved(updated, 'Saved. Use "Send test" to check the partner accepts it.'),
      error: (err) => this.webhookFailed(err),
    });
  }

  removeWebhook(): void {
    const client = this.webhookClient();
    if (!client) return;
    this.webhookBusy.set(true);
    this.apiClientsService.removeWebhook(client.id).subscribe({
      next: (updated) => {
        this.webhookUrl = '';
        this.webhookSaved(updated, 'Webhook removed. The partner will need to check for changes itself.');
      },
      error: (err) => this.webhookFailed(err),
    });
  }

  testWebhook(): void {
    const client = this.webhookClient();
    if (!client) return;
    this.webhookBusy.set(true);
    this.testResult.set(null);
    this.apiClientsService.testWebhook(client.id).subscribe({
      next: (r) => {
        this.webhookBusy.set(false);
        this.testResult.set(r);
      },
      error: (err) => this.webhookFailed(err),
    });
  }

  deliveryClasses(state: WebhookDelivery['state']): string {
    switch (state) {
      case 'Delivered': return 'bg-green-100 text-green-800';
      case 'Pending':
      case 'Sending': return 'bg-amber-100 text-amber-800';
      default: return 'bg-red-100 text-red-800';
    }
  }

  private webhookSaved(updated: ApiClient, note: string): void {
    this.webhookBusy.set(false);
    this.webhookSecret = '';
    this.webhookClient.set(updated);
    this.clients.update((list) => list.map((c) => (c.id === updated.id ? updated : c)));
    this.webhookNote.set(note);
  }

  private webhookFailed(err: { error?: { error?: string } }): void {
    this.webhookBusy.set(false);
    this.webhookError.set(err.error?.error ?? 'Something went wrong.');
  }

  revoke(client: ApiClient): void {
    if (!confirm(`Revoke "${client.name}"? Any system using this key will stop working immediately. This cannot be undone.`)) return;
    this.apiClientsService.revoke(client.id).subscribe({
      next: () => this.load(),
      error: (err) => this.errorMessage.set(err.error?.error ?? 'Failed to revoke API key.'),
    });
  }

  private resetForm(): void {
    this.name = '';
    this.selectedScopes = { 'templates:read': true, 'credentials:read': true, 'credentials:write': true, 'credentials:revoke': false };
    this.autoIssue = false;
    this.requiresPayment = true;
  }
}
