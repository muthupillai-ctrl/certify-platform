import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { AuditEntry, ControlRequest, ControlsService } from '../../../core/services/controls.service';
import { ModalComponent } from '../../../shared/modal/modal.component';

// Institution admins: safeguard changes waiting for a second admin, and the
// institution audit log (who changed users, roles, approval rules and API
// keys, and when).
@Component({
  selector: 'app-audit',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './audit.component.html',
})
export class AuditComponent implements OnInit {
  readonly requests = signal<ControlRequest[]>([]);
  readonly pending = computed(() => this.requests().filter((r) => r.status === 'Pending'));
  readonly decided = computed(() => this.requests().filter((r) => r.status !== 'Pending').slice(0, 10));
  readonly entries = signal<AuditEntry[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = 50;
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize)));
  readonly error = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly refusing = signal<ControlRequest | null>(null);
  refuseNote = '';

  constructor(private controls: ControlsService) {}

  ngOnInit(): void {
    this.loadRequests();
    this.loadLog(1);
  }

  loadRequests(): void {
    this.controls.requests().subscribe({ next: (r) => this.requests.set(r), error: (e: HttpErrorResponse) => this.fail(e) });
  }

  loadLog(page: number): void {
    this.controls.auditLog(page, this.pageSize).subscribe({
      next: (p) => {
        this.entries.set(p.items);
        this.total.set(p.total);
        this.page.set(p.page);
      },
      error: (e: HttpErrorResponse) => this.fail(e),
    });
  }

  confirm(r: ControlRequest): void {
    this.act(r, this.controls.confirm(r.id));
  }

  withdraw(r: ControlRequest): void {
    this.act(r, this.controls.withdraw(r.id));
  }

  askRefuse(r: ControlRequest): void {
    this.refuseNote = '';
    this.refusing.set(r);
  }

  refuse(): void {
    const r = this.refusing();
    if (!r) return;
    this.refusing.set(null);
    this.act(r, this.controls.refuse(r.id, this.refuseNote.trim() || null));
  }

  downloadCsv(): void {
    this.controls.auditCsv().subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'audit-log.csv';
        a.click();
        URL.revokeObjectURL(url);
      },
      error: (e: HttpErrorResponse) => this.fail(e),
    });
  }

  kindLabel(r: ControlRequest): string {
    return r.kind === 'DisableFinalApproval' ? 'Switch off final approval' : 'Issue without approval (API key)';
  }

  // Readable names for the logged actions.
  actionLabel(action: string): string {
    const labels: Record<string, string> = {
      UserAdded: 'User added', AdminAdded: 'Institution admin added', RolesChanged: 'Roles changed',
      UserDeactivated: 'User deactivated', UserReactivated: 'User reactivated', PasswordReset: 'Password reset',
      FinalApprovalTurnedOn: 'Final approval switched on',
      DisableFinalApprovalRequested: 'Asked to switch off final approval', DisableFinalApprovalConfirmed: 'Final approval switched off (confirmed)',
      DisableFinalApprovalRefused: 'Switching off final approval refused', DisableFinalApprovalWithdrawn: 'Request to switch off final approval withdrawn',
      EnableAutoIssueRequested: 'Asked to issue without approval', EnableAutoIssueConfirmed: 'Issue without approval confirmed',
      EnableAutoIssueRefused: 'Issue without approval refused', EnableAutoIssueWithdrawn: 'Issue without approval request withdrawn',
      ApiKeyCreated: 'API key created', ApiKeyRevoked: 'API key revoked',
    };
    return labels[action] ?? action;
  }

  private act(r: ControlRequest, call: ReturnType<ControlsService['confirm']>): void {
    this.busy.set(r.id);
    this.error.set(null);
    call.subscribe({
      next: () => {
        this.busy.set(null);
        this.loadRequests();
        this.loadLog(1);
      },
      error: (e: HttpErrorResponse) => {
        this.busy.set(null);
        this.fail(e);
        this.loadRequests();
      },
    });
  }

  private fail(e: HttpErrorResponse): void {
    this.error.set(e.error?.error ?? 'Something went wrong. Please try again.');
  }
}
