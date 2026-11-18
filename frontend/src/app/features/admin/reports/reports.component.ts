import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { ReportsService } from '../../../core/services/reports.service';
import { IssuanceReport, ReportKind, StaffActivityReport, StatusChangesReport } from '../../../core/models/reports.models';
import { PERIOD_LABELS, PeriodPreset, describePeriod, periodOf } from '../../../core/date-periods';
import { blobErrorMessage, saveBlob } from '../../../core/download';

// Staff-activity columns: ledger event type → heading.
const ACTIVITY_COLUMNS: { type: string; label: string }[] = [
  { type: 'Requested', label: 'Requested' },
  { type: 'FirstApproved', label: 'First approval' },
  { type: 'Approved', label: 'Approved (issued)' },
  { type: 'Rejected', label: 'Rejected' },
  { type: 'Paid', label: 'Paid (student)' },
  { type: 'Cancelled', label: 'Cancelled' },
  { type: 'Suspended', label: 'Suspended' },
  { type: 'Reactivated', label: 'Reactivated' },
  { type: 'RevocationRequested', label: 'Asked to revoke' },
  { type: 'Revoked', label: 'Revoked (approved)' },
  { type: 'RevocationRequestRejected', label: 'Revocation rejected' },
];

// Audit and accreditation reports for a chosen period: a summary to read
// (or print), and a CSV with every row.
@Component({
  selector: 'app-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './reports.component.html',
})
export class ReportsComponent {
  readonly kinds: { id: ReportKind; title: string; description: string }[] = [
    { id: 'issuance', title: 'Issuance', description: 'Certificates issued, by achievement, requester, approver and source.' },
    { id: 'status-changes', title: 'Status changes', description: 'Payments, cancellations, suspensions, reactivations and revocations, with who did it and why.' },
    { id: 'staff-activity', title: 'Staff activity', description: 'What each person did, and a check that two-person approval was always followed.' },
  ];
  readonly activityColumns = ACTIVITY_COLUMNS;
  readonly periodLabels = PERIOD_LABELS;
  readonly periodOptions: PeriodPreset[] = ['thisMonth', 'lastMonth', 'thisYear', 'lastYear', 'custom'];

  readonly kind = signal<ReportKind>('issuance');
  preset: PeriodPreset = 'thisMonth';
  fromDate = '';
  toDate = '';

  readonly loading = signal(false);
  readonly exporting = signal(false);
  readonly error = signal<string | null>(null);
  readonly issuance = signal<IssuanceReport | null>(null);
  readonly statusChanges = signal<StatusChangesReport | null>(null);
  readonly staffActivity = signal<StaffActivityReport | null>(null);
  // What the report on screen covers (fixed when it was run).
  readonly shownKind = signal<ReportKind | null>(null);
  readonly shownPeriod = signal('');
  readonly generatedAt = signal<Date | null>(null);

  readonly institution = computed(() => this.auth.currentUser()?.tenant_slug ?? '');

  constructor(
    private reports: ReportsService,
    private auth: AuthService,
  ) {}

  private period() {
    return periodOf(this.preset, this.fromDate, this.toDate);
  }

  periodReady(): boolean {
    const p = this.period();
    return !!p.from && !!p.before && p.from < p.before;
  }

  chooseKind(kind: ReportKind): void {
    this.kind.set(kind);
    this.shownKind.set(null);
  }

  run(): void {
    if (!this.periodReady()) return;
    const kind = this.kind();
    const period = this.period();
    this.loading.set(true);
    this.error.set(null);
    const done = () => {
      this.loading.set(false);
      this.shownKind.set(kind);
      this.shownPeriod.set(describePeriod(period));
      this.generatedAt.set(new Date());
    };
    const fail = (err: { error?: { error?: string } }) => {
      this.loading.set(false);
      this.error.set(err.error?.error ?? 'The report could not be produced.');
    };
    if (kind === 'issuance') this.reports.issuance(period).subscribe({ next: (r) => { this.issuance.set(r); done(); }, error: fail });
    else if (kind === 'status-changes') this.reports.statusChanges(period).subscribe({ next: (r) => { this.statusChanges.set(r); done(); }, error: fail });
    else this.reports.staffActivity(period).subscribe({ next: (r) => { this.staffActivity.set(r); done(); }, error: fail });
  }

  downloadCsv(): void {
    if (!this.periodReady()) return;
    const kind = this.kind();
    const period = this.period();
    this.exporting.set(true);
    this.reports.csv(kind, period).subscribe({
      next: (blob) => {
        this.exporting.set(false);
        // Named by the period's local dates (the instants are UTC, which
        // would put India's 1 September at 31 August).
        const day = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
        const last = new Date(new Date(period.before!).getTime() - 1);
        saveBlob(blob, `${kind}-${this.institution()}-${day(new Date(period.from!))}-to-${day(last)}.csv`);
      },
      error: async (err) => {
        this.exporting.set(false);
        this.error.set(await blobErrorMessage(err, "Couldn't download the report."));
      },
    });
  }

  print(): void {
    window.print();
  }

  kindTitle(kind: ReportKind | null): string {
    return this.kinds.find((k) => k.id === kind)?.title ?? '';
  }

  personTotal(counts: Record<string, number>): number {
    return Object.values(counts).reduce((s, n) => s + n, 0);
  }
}
