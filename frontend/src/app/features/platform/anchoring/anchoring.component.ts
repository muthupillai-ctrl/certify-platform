import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AnchoringService } from '../../../core/services/anchoring.service';
import { TenantsService } from '../../../core/services/tenants.service';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { Tenant } from '../../../core/models/tenant.models';
import {
  AnchoringStatus,
  CheckpointDiagnosis,
  CheckpointFilter,
  CheckpointRow,
  ConnectivityReport,
} from '../../../core/models/anchoring.models';

type Action = 'checkpoint' | 'upgrade';

// SuperAdmin anchoring page: the two "run now" buttons, plus the tools to
// troubleshoot Bitcoin anchoring — job status, connectivity to the calendar
// servers and block explorer, every checkpoint with anything needing
// attention flagged, and per-checkpoint diagnose / check / re-verify /
// proof download. Diagnose is read-only; only "Check now" and "Re-verify"
// change a checkpoint, and only the way the background job would.
@Component({
  selector: 'app-anchoring',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './anchoring.component.html',
})
export class AnchoringComponent implements OnInit {
  readonly running = signal<Action | null>(null);
  readonly message = signal<string | null>(null);
  readonly isError = signal(false);

  readonly status = signal<AnchoringStatus | null>(null);
  readonly connectivity = signal<ConnectivityReport | null>(null);
  readonly probing = signal(false);

  readonly tenants = signal<Tenant[]>([]);
  readonly rows = signal<CheckpointRow[]>([]);
  readonly loadingRows = signal(true);
  readonly rowsError = signal<string | null>(null);
  readonly busyRowId = signal<string | null>(null);
  filter: CheckpointFilter = 'attention';
  tenantSlug = '';

  readonly diagnosing = signal<CheckpointRow | null>(null);
  readonly diagnosis = signal<CheckpointDiagnosis | null>(null);
  readonly diagnosisError = signal<string | null>(null);

  readonly jobStalled = computed(() => {
    const s = this.status();
    if (!s?.backgroundJobStartedAt || !s.lastUpgradeRun) return false;
    // Two missed upgrade runs in a row means the loop has stopped.
    const dueBy = new Date(s.lastUpgradeRun.finishedAt).getTime() + 2 * s.upgradeIntervalMinutes * 60_000;
    return Date.now() > dueBy;
  });

  constructor(
    private anchoring: AnchoringService,
    private tenantsService: TenantsService,
  ) {}

  ngOnInit(): void {
    this.loadStatus();
    this.loadRows();
    this.tenantsService.list().subscribe({ next: (t) => this.tenants.set(t) });
  }

  loadStatus(): void {
    this.anchoring.status().subscribe({ next: (s) => this.status.set(s) });
  }

  loadRows(): void {
    this.loadingRows.set(true);
    this.rowsError.set(null);
    this.anchoring.checkpoints(this.filter, this.tenantSlug || null).subscribe({
      next: (rows) => {
        this.rows.set(rows);
        this.loadingRows.set(false);
      },
      error: () => {
        this.rowsError.set('Failed to load checkpoints.');
        this.loadingRows.set(false);
      },
    });
  }

  showFilter(filter: CheckpointFilter): void {
    this.filter = filter;
    this.loadRows();
  }

  testConnections(): void {
    this.probing.set(true);
    this.anchoring.connectivity().subscribe({
      next: (r) => {
        this.connectivity.set(r);
        this.probing.set(false);
      },
      error: () => {
        this.connectivity.set(null);
        this.probing.set(false);
        this.fail('Connection test failed to run. Check the backend logs.');
      },
    });
  }

  checkpointNow(): void {
    this.start('checkpoint');
    this.anchoring.checkpointNow().subscribe({
      next: (r) =>
        this.finish(
          r.checkpointsCreated === 0
            ? 'No new checkpoints — every tenant is already checkpointed up to its latest ledger entry.'
            : `Created ${r.checkpointsCreated} new checkpoint(s) and submitted them to the calendar servers.`,
        ),
      error: () => this.fail('Checkpoint run failed. Check the backend logs.'),
    });
  }

  upgradeNow(): void {
    this.start('upgrade');
    this.anchoring.upgradeNow().subscribe({
      next: (r) =>
        this.finish(
          r.checkpointsConfirmed === 0
            ? 'No checkpoints confirmed yet — Bitcoin confirmation usually takes a few hours.'
            : `${r.checkpointsConfirmed} checkpoint(s) confirmed on Bitcoin.`,
        ),
      error: () => this.fail('Confirmation check failed. Check the backend logs.'),
    });
  }

  diagnose(row: CheckpointRow): void {
    this.diagnosing.set(row);
    this.diagnosis.set(null);
    this.diagnosisError.set(null);
    this.anchoring.diagnose(row).subscribe({
      next: (d) => this.diagnosis.set(d),
      error: (err) => this.diagnosisError.set(err.error?.error ?? 'Diagnosis failed to run. Check the backend logs.'),
    });
  }

  closeDiagnosis(): void {
    this.diagnosing.set(null);
    this.diagnosis.set(null);
  }

  checkOne(row: CheckpointRow): void {
    this.updateRow(row, this.anchoring.checkOne(row));
  }

  reverify(row: CheckpointRow): void {
    this.updateRow(row, this.anchoring.reverify(row));
  }

  downloadProof(row: CheckpointRow): void {
    this.anchoring.proof(row).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${row.tenantSlug}-checkpoint-${row.id}.ots`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => this.fail('Could not download the proof.'),
    });
  }

  age(iso: string): string {
    const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60_000));
    if (minutes < 60) return `${minutes} min ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 48) return `${hours} h ago`;
    return `${Math.round(hours / 24)} days ago`;
  }

  host(url: string): string {
    try {
      return new URL(url).host;
    } catch {
      return url;
    }
  }

  short(hex: string | null): string {
    return hex ? `${hex.slice(0, 10)}…${hex.slice(-6)}` : '—';
  }

  private updateRow(row: CheckpointRow, request: ReturnType<AnchoringService['checkOne']>): void {
    this.busyRowId.set(row.id);
    request.subscribe({
      next: (updated) => {
        this.busyRowId.set(null);
        this.rows.update((rows) => rows.map((r) => (r.id === updated.id ? updated : r)));
        if (this.diagnosing()?.id === updated.id) this.diagnose(updated);
        this.loadStatus();
      },
      error: (err) => {
        this.busyRowId.set(null);
        this.fail(err.error?.error ?? 'The action failed. Check the backend logs.');
      },
    });
  }

  private start(action: Action): void {
    this.running.set(action);
    this.message.set(null);
    this.isError.set(false);
  }

  private finish(message: string): void {
    this.running.set(null);
    this.message.set(message);
    this.loadStatus();
    this.loadRows();
  }

  private fail(message: string): void {
    this.running.set(null);
    this.isError.set(true);
    this.message.set(message);
  }
}
