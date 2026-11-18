import { Component, computed, effect, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { CredentialsService } from '../../../core/services/credentials.service';
import { ReportsService } from '../../../core/services/reports.service';
import { VerificationService } from '../../../core/services/verification.service';
import { Credential, CredentialVerificationHistory, displayStatus } from '../../../core/models/credential.models';
import { ActivityResultGroup, VerificationActivityItem } from '../../../core/models/reports.models';
import { CredentialFileCheck, checkCredentialFile } from '../../../core/offline-verification';
import { PERIOD_LABELS, PeriodPreset, periodOf } from '../../../core/date-periods';
import { blobErrorMessage, saveBlob } from '../../../core/download';

const CODE_ALPHABET = /^[23456789ABCDEFGHJKMNPQRSTWXYZ]{12}$/;
const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

// What "Check a certificate" found.
type CheckResult =
  | { kind: 'ours'; credential: Credential; history: CredentialVerificationHistory | null; file: CredentialFileCheck | null }
  | { kind: 'other-institution'; slug: string; credentialId: string | null; institutionName: string | null; file: CredentialFileCheck | null }
  | { kind: 'not-found'; what: string }
  | { kind: 'bad-file'; problem: string };

// Staff side of verification. "Check a certificate" answers an employer's
// question from inside the portal (code, link or credential file) without
// counting as a public check. The activity log shows every public check of
// the institution's certificates, with anything worrying flagged.
@Component({
  selector: 'app-verification',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './verification.component.html',
})
export class VerificationComponent {
  // ---------- Check a certificate ----------
  checkInput = '';
  readonly checking = signal(false);
  readonly checkResult = signal<CheckResult | null>(null);
  readonly checkError = signal<string | null>(null);
  readonly dragging = signal(false);

  // ---------- Activity log ----------
  readonly periodLabels = PERIOD_LABELS;
  readonly periodOptions: PeriodPreset[] = ['last30', 'thisMonth', 'lastMonth', 'thisYear', 'all', 'custom'];
  readonly preset = signal<PeriodPreset>('last30');
  readonly fromDate = signal('');
  readonly toDate = signal('');
  readonly channel = signal<string | null>(null);
  readonly result = signal<ActivityResultGroup | null>(null);
  readonly page = signal(1);
  readonly pageSize = 50;

  readonly items = signal<VerificationActivityItem[]>([]);
  readonly total = signal(0);
  readonly allResults = signal(0);
  readonly counts = signal<Partial<Record<string, number>>>({});
  readonly loading = signal(true);
  readonly exporting = signal(false);
  readonly error = signal<string | null>(null);
  private seq = 0;

  readonly channels = [
    { id: 'VerifyPage', label: 'Verification page (link or QR)' },
    { id: 'CodeLookup', label: 'Code look-up' },
    { id: 'CredentialDownload', label: 'Digital credential downloaded' },
    { id: 'Api', label: 'API' },
  ];
  readonly results: { id: ActivityResultGroup; label: string }[] = [
    { id: 'Attention', label: 'Needs attention' },
    { id: 'Valid', label: 'Valid' },
    { id: 'CodeMatched', label: 'Code matched' },
    { id: 'Suspended', label: 'Suspended' },
    { id: 'Revoked', label: 'Revoked' },
    { id: 'Expired', label: 'Expired' },
    { id: 'NotFound', label: 'No such certificate' },
  ];

  private readonly query = computed(() => ({
    ...periodOf(this.preset(), this.fromDate(), this.toDate()),
    channel: this.channel(),
    result: this.result(),
    page: this.page(),
    pageSize: this.pageSize,
  }));

  readonly lastPage = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize)));

  constructor(
    private auth: AuthService,
    private credentials: CredentialsService,
    private reports: ReportsService,
    private verification: VerificationService,
  ) {
    effect(() => {
      const query = this.query();
      untracked(() => this.load(query));
    });
  }

  private load(query: ReturnType<typeof this.query>): void {
    const seq = ++this.seq;
    this.loading.set(true);
    this.reports.verificationActivity(query).subscribe({
      next: (page) => {
        if (seq !== this.seq) return;
        this.items.set(page.items);
        this.total.set(page.total);
        this.allResults.set(page.allResults);
        this.counts.set(page.resultCounts);
        this.loading.set(false);
        this.error.set(null);
      },
      error: () => {
        if (seq !== this.seq) return;
        this.loading.set(false);
        this.error.set('Could not load the verification activity.');
      },
    });
  }

  setPreset(value: PeriodPreset): void {
    this.preset.set(value);
    this.page.set(1);
  }

  setFilter(which: 'channel' | 'result', value: string | null): void {
    if (which === 'channel') this.channel.set(value);
    else this.result.set(value as ActivityResultGroup | null);
    this.page.set(1);
  }

  setDate(which: 'from' | 'to', value: string): void {
    (which === 'from' ? this.fromDate : this.toDate).set(value);
    this.page.set(1);
  }

  exportCsv(): void {
    this.exporting.set(true);
    const { page: _p, pageSize: _s, ...filters } = this.query();
    this.reports.verificationActivityCsv(filters).subscribe({
      next: (blob) => {
        this.exporting.set(false);
        saveBlob(blob, `verifications-${this.slug()}-${new Date().toISOString().slice(0, 10)}.csv`);
      },
      error: async (err) => {
        this.exporting.set(false);
        this.error.set(await blobErrorMessage(err, "Couldn't export the activity."));
      },
    });
  }

  channelLabel(channel: string): string {
    return this.channels.find((c) => c.id === channel)?.label ?? channel;
  }

  resultLabel(result: string): string {
    return { Active: 'Valid', Found: 'Code matched', NotFound: 'No such certificate' }[result] ?? result;
  }

  resultClasses(result: string): string {
    switch (result) {
      case 'Active':
        return 'bg-green-100 text-green-800';
      case 'Found':
        return 'bg-slate-100 text-slate-700';
      case 'Suspended':
        return 'bg-orange-100 text-orange-800';
      case 'Revoked':
      case 'NotFound':
        return 'bg-red-100 text-red-800';
      default:
        return 'bg-gray-100 text-gray-700';
    }
  }

  // ---------- Check a certificate ----------

  private slug(): string {
    return this.auth.currentUser()?.tenant_slug ?? '';
  }

  // A code (any spacing/case), a verification link, or a certificate id.
  check(): void {
    const input = this.checkInput.trim();
    this.checkResult.set(null);
    this.checkError.set(null);
    if (!input) return;

    const link = input.match(/\/verify\/([a-z0-9-]+)\/([0-9a-f-]{36})/i);
    if (link) {
      const [, slug, id] = link;
      if (slug.toLowerCase() !== this.slug()) {
        this.checkResult.set({ kind: 'other-institution', slug, credentialId: id, institutionName: null, file: null });
        return;
      }
      this.showOurs(id, null, 'link');
      return;
    }
    const id = input.match(GUID)?.[0];
    if (id && input.length <= 40) {
      this.showOurs(id, null, 'id');
      return;
    }
    const code = input.replace(/[^0-9a-z]/gi, '').toUpperCase();
    if (!CODE_ALPHABET.test(code)) {
      this.checkPaperNumber(input);
      return;
    }
    this.checking.set(true);
    this.credentials.list({ q: code, pageSize: 5 }).subscribe({
      next: (page) => {
        const match = page.items.find((i) => i.verificationCode.replace(/-/g, '') === code);
        if (match) this.showOurs(match.id, null, 'code');
        else {
          this.checking.set(false);
          this.checkResult.set({ kind: 'not-found', what: `code ${code.slice(0, 4)}-${code.slice(4, 8)}-${code.slice(8)}` });
        }
      },
      error: () => {
        this.checking.set(false);
        this.checkError.set('The check could not be completed. Please try again.');
      },
    });
  }

  // Not a Certyfi code: perhaps the number printed on a digitised paper certificate.
  private checkPaperNumber(input: string): void {
    const key = input.replace(/\s/g, '').toUpperCase();
    if (key.length < 2) {
      this.checkError.set('Enter a certificate code (like K7QM-3XPD-9TWA), a paper certificate number, paste a verification link, or choose a credential file.');
      return;
    }
    this.checking.set(true);
    this.credentials.list({ q: input, pageSize: 10 }).subscribe({
      next: (page) => {
        const match = page.items.find((i) => (i.legacyCertificateNumber ?? '').replace(/\s/g, '').toUpperCase() === key);
        if (match) this.showOurs(match.id, null, 'paper number');
        else {
          this.checking.set(false);
          this.checkResult.set({ kind: 'not-found', what: `code or paper number "${input}"` });
        }
      },
      error: () => {
        this.checking.set(false);
        this.checkError.set('The check could not be completed. Please try again.');
      },
    });
  }

  onFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) void this.checkFile(file);
    (event.target as HTMLInputElement).value = '';
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) void this.checkFile(file);
  }

  // A digital credential file (Open Badges, signed): the signature is
  // checked here in the browser against the issuer's published key.
  private async checkFile(file: File): Promise<void> {
    this.checkResult.set(null);
    this.checkError.set(null);
    if (file.size > 100_000) {
      this.checkResult.set({ kind: 'bad-file', problem: 'That file is too large to be a Certyfi digital credential.' });
      return;
    }
    this.checking.set(true);
    const result = await checkCredentialFile(await file.text(), (kid) => this.verification.fetchJwk(kid));
    if (!result.signatureValid) {
      this.checking.set(false);
      this.checkResult.set({ kind: 'bad-file', problem: result.problem ?? 'The signature does not match: this file is not a genuine Certyfi credential, or it was altered.' });
      return;
    }
    if (result.tenantSlug && result.tenantSlug !== this.slug()) {
      this.checking.set(false);
      this.checkResult.set({ kind: 'other-institution', slug: result.tenantSlug, credentialId: result.credentialId, institutionName: result.institutionName, file: result });
      return;
    }
    if (result.credentialId) this.showOurs(result.credentialId, result, 'file');
    else {
      this.checking.set(false);
      this.checkResult.set({ kind: 'bad-file', problem: 'The file is signed but does not say which certificate it is.' });
    }
  }

  private showOurs(id: string, file: CredentialFileCheck | null, what: string): void {
    this.checking.set(true);
    this.credentials.get(id).subscribe({
      next: (credential) => {
        this.checkResult.set({ kind: 'ours', credential, history: null, file });
        this.checking.set(false);
        this.credentials.verifications(id).subscribe({
          next: (history) => {
            const current = this.checkResult();
            if (current?.kind === 'ours' && current.credential.id === id) this.checkResult.set({ ...current, history });
          },
        });
      },
      error: () => {
        this.checking.set(false);
        this.checkResult.set({ kind: 'not-found', what });
      },
    });
  }

  status(credential: Credential): string {
    return displayStatus(credential);
  }

  clearCheck(): void {
    this.checkInput = '';
    this.checkResult.set(null);
    this.checkError.set(null);
  }
}
