import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { CredentialsService } from '../../../core/services/credentials.service';
import { RevocationsService } from '../../../core/services/revocations.service';
import { CertificateActivityService } from '../../../core/services/certificate-activity.service';
import { Credential, CredentialSummary, displayStatus } from '../../../core/models/credential.models';
import { REVOCATION_REASONS, RevocationReasonCategory, RevocationRequest } from '../../../core/models/revocation.models';
import { ModalComponent } from '../../../shared/modal/modal.component';

// What the confirm dialog is doing.
type Action = 'suspend' | 'reactivate' | 'request' | 'approve';

// Codes are compared ignoring dashes, spaces and case (like the server).
function normalizeCode(value: string): string {
  return value.replace(/[^0-9a-z]/gi, '').toUpperCase();
}

// Suspend, reactivate and revoke, kept apart from everyday certificate work
// because they change what the public sees. Suspend/reactivate take effect
// at once; revoking is permanent, so one person asks and a different
// approver approves (same maker–checker rule as issuing). Every action asks
// for the certificate's code to be typed, so it's never a slip of the mouse.
@Component({
  selector: 'app-revoke-suspend',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ModalComponent],
  templateUrl: './revoke-suspend.component.html',
})
export class RevokeSuspendComponent implements OnInit, OnDestroy {
  readonly reasons = REVOCATION_REASONS;

  readonly pending = signal<RevocationRequest[]>([]);
  readonly decided = signal<RevocationRequest[]>([]);
  readonly loadingQueue = signal(true);

  // ---- Find a certificate ----
  searchText = '';
  readonly results = signal<CredentialSummary[]>([]);
  readonly searching = signal(false);
  readonly searched = signal(false);
  readonly selected = signal<Credential | null>(null);
  private searchTimer: ReturnType<typeof setTimeout> | undefined;
  private searchSub: Subscription | undefined;

  // ---- Confirm dialog ----
  readonly action = signal<Action | null>(null);
  readonly actionRequest = signal<RevocationRequest | null>(null);
  reasonCategory: RevocationReasonCategory | '' = '';
  reasonText = '';
  typedCode = '';
  readonly working = signal(false);
  readonly dialogError = signal<string | null>(null);

  // ---- Reject a request ----
  readonly rejecting = signal<RevocationRequest | null>(null);
  rejectNote = '';

  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor(
    private credentialsService: CredentialsService,
    private revocations: RevocationsService,
    private activity: CertificateActivityService,
    private route: ActivatedRoute,
    private router: Router,
  ) {}

  ngOnInit(): void {
    this.loadQueue();
    // Arriving from a certificate's "Revoke & suspend…" link.
    const id = this.route.snapshot.queryParamMap.get('certificate');
    if (id) this.select(id);
  }

  ngOnDestroy(): void {
    clearTimeout(this.searchTimer);
    this.searchSub?.unsubscribe();
  }

  loadQueue(): void {
    this.revocations.pending().subscribe({
      next: (rows) => {
        this.pending.set(rows);
        this.loadingQueue.set(false);
      },
      error: () => {
        this.loadingQueue.set(false);
        this.error.set('Could not load the revocation requests.');
      },
    });
    this.revocations.decided().subscribe({ next: (rows) => this.decided.set(rows) });
  }

  // ---------- Find ----------

  onSearch(): void {
    clearTimeout(this.searchTimer);
    const q = this.searchText.trim();
    if (q.length < 2) {
      this.results.set([]);
      this.searched.set(false);
      return;
    }
    this.searchTimer = setTimeout(() => {
      this.searching.set(true);
      this.searchSub?.unsubscribe();
      this.searchSub = this.credentialsService.list({ q, pageSize: 10, sort: 'recipient' }).subscribe({
        next: (page) => {
          this.results.set(page.items);
          this.searching.set(false);
          this.searched.set(true);
        },
        error: () => this.searching.set(false),
      });
    }, 300);
  }

  select(id: string): void {
    this.credentialsService.get(id).subscribe({
      next: (credential) => {
        this.selected.set(credential);
        this.results.set([]);
        this.searched.set(false);
        void this.router.navigate([], { relativeTo: this.route, queryParams: { certificate: id }, replaceUrl: true });
      },
      error: () => this.error.set('Certificate not found.'),
    });
  }

  clearSelection(): void {
    this.selected.set(null);
    void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  readonly selectedStatus = computed(() => {
    const c = this.selected();
    return c ? displayStatus(c) : null;
  });

  statusLabel(status: string): string {
    return status === 'PendingApproval' ? 'Pending approval' : status === 'AwaitingPayment' ? 'Awaiting payment' : status;
  }

  // ---------- Confirm dialog ----------

  // The certificate the dialog is about.
  readonly target = computed(() => {
    const request = this.actionRequest();
    if (request) return { name: request.recipientName, achievement: request.achievementTitle, code: request.verificationCode };
    const c = this.selected();
    return c ? { name: c.recipientName, achievement: c.achievementTitle, code: this.formatCode(c.verificationCode) } : null;
  });

  open(action: Action, request: RevocationRequest | null = null): void {
    this.action.set(action);
    this.actionRequest.set(request);
    this.reasonCategory = '';
    this.reasonText = '';
    this.typedCode = '';
    this.dialogError.set(null);
    this.message.set(null);
  }

  close(): void {
    this.action.set(null);
    this.actionRequest.set(null);
  }

  codeMatches(): boolean {
    const t = this.target();
    return !!t && normalizeCode(this.typedCode) === normalizeCode(t.code);
  }

  canSubmit(): boolean {
    if (this.working() || !this.codeMatches()) return false;
    switch (this.action()) {
      case 'request':
        return !!this.reasonCategory && !!this.reasonText.trim();
      case 'suspend':
      case 'reactivate':
        return !!this.reasonText.trim();
      default:
        return true;
    }
  }

  submit(): void {
    const action = this.action();
    if (!action || !this.canSubmit()) return;
    const credential = this.selected();
    const request = this.actionRequest();
    this.working.set(true);
    this.dialogError.set(null);

    const done = (message: string) => {
      this.working.set(false);
      this.close();
      this.message.set(message);
      this.loadQueue();
      this.activity.refresh().subscribe({ error: () => undefined });
      const id = request?.credentialId ?? credential?.id;
      if (id && this.selected()?.id === id) this.select(id);
    };
    const fail = (err: { error?: { error?: string } }) => {
      this.working.set(false);
      this.dialogError.set(err.error?.error ?? 'That didn’t work. Please try again.');
    };

    if (action === 'suspend' && credential) {
      this.credentialsService.suspend(credential.id, this.reasonText.trim(), this.typedCode).subscribe({
        next: () => done(`${credential.recipientName}'s certificate is suspended.`),
        error: fail,
      });
    } else if (action === 'reactivate' && credential) {
      this.credentialsService.reactivate(credential.id, this.reasonText.trim(), this.typedCode).subscribe({
        next: () => done(`${credential.recipientName}'s certificate is active again.`),
        error: fail,
      });
    } else if (action === 'request' && credential && this.reasonCategory) {
      this.revocations.request(credential.id, this.reasonCategory, this.reasonText.trim(), this.typedCode).subscribe({
        next: () => done('Revocation requested. Another approver must approve it before the certificate is revoked.'),
        error: fail,
      });
    } else if (action === 'approve' && request) {
      this.revocations.approve(request.id, this.typedCode).subscribe({
        next: () => done(`${request.recipientName}'s certificate is revoked.`),
        error: fail,
      });
    } else {
      this.working.set(false);
    }
  }

  // ---------- Reject / withdraw a request ----------

  openReject(request: RevocationRequest): void {
    this.rejecting.set(request);
    this.rejectNote = '';
    this.dialogError.set(null);
    this.message.set(null);
  }

  submitReject(): void {
    const request = this.rejecting();
    if (!request || !this.rejectNote.trim()) return;
    this.working.set(true);
    this.revocations.reject(request.id, this.rejectNote.trim()).subscribe({
      next: () => {
        this.working.set(false);
        this.rejecting.set(null);
        this.message.set(`The request to revoke ${request.recipientName}'s certificate was rejected; the certificate is unchanged.`);
        this.loadQueue();
        this.activity.refresh().subscribe({ error: () => undefined });
        if (this.selected()?.id === request.credentialId) this.select(request.credentialId);
      },
      error: (err) => {
        this.working.set(false);
        this.dialogError.set(err.error?.error ?? 'That didn’t work. Please try again.');
      },
    });
  }

  withdraw(request: RevocationRequest): void {
    if (!confirm(`Withdraw your request to revoke ${request.recipientName}'s certificate?`)) return;
    this.revocations.withdraw(request.id).subscribe({
      next: () => {
        this.message.set('Request withdrawn; the certificate is unchanged.');
        this.loadQueue();
        if (this.selected()?.id === request.credentialId) this.select(request.credentialId);
      },
      error: (err) => this.error.set(err.error?.error ?? 'Could not withdraw the request.'),
    });
  }

  formatCode(code: string): string {
    return code.length === 12 ? `${code.slice(0, 4)}-${code.slice(4, 8)}-${code.slice(8)}` : code;
  }

  outcomeLabel(r: RevocationRequest): string {
    return r.status === 'Approved' ? 'Revoked' : r.status;
  }
}
