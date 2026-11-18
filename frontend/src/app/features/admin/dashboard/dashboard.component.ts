import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { Params } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { DashboardService } from '../../../core/services/dashboard.service';
import { Dashboard, DashboardMonthCount } from '../../../core/models/dashboard.models';

const REFRESH_MS = 60_000;
const CERTIFICATES = '/admin/certificates';

interface Bar {
  label: string;
  // Full month and year, for screen readers and the tooltip.
  title: string;
  count: number;
  // Percentage of the tallest bar (at least a sliver when non-zero).
  height: number;
  link: Params | null;
}

// The institution at a glance (certificates plan phase 7). Every number
// that has a matching list links to it as a Certificates URL (phase 6), so
// a click shows exactly the certificates counted. Charts are plain HTML
// bars: no chart library, readable by screen readers.
@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard.component.html',
})
export class DashboardComponent implements OnInit, OnDestroy {
  readonly certificates = CERTIFICATES;
  readonly data = signal<Dashboard | null>(null);
  readonly error = signal<string | null>(null);
  private timer: ReturnType<typeof setInterval> | undefined;

  constructor(
    private dashboardService: DashboardService,
    readonly auth: AuthService,
    private router: Router,
  ) {}

  ngOnInit(): void {
    // Learners have no institution dashboard; their page is their certificates.
    if (!['InstitutionAdmin', 'Registrar', 'Approver'].some((r) => this.auth.hasRole(r))) {
      void this.router.navigate([CERTIFICATES], { replaceUrl: true });
      return;
    }
    this.load();
    this.timer = setInterval(() => {
      if (document.visibilityState === 'visible') this.load();
    }, REFRESH_MS);
  }

  ngOnDestroy(): void {
    clearInterval(this.timer);
  }

  load(): void {
    this.dashboardService.get().subscribe({
      next: (data) => {
        this.data.set(data);
        this.error.set(null);
      },
      error: () => this.error.set('The dashboard could not be loaded. It will try again in a minute.'),
    });
  }

  // ---------- Needs attention ----------

  readonly oldestPendingDays = computed(() => {
    const oldest = this.data()?.attention.oldestPendingAt;
    return oldest ? Math.floor((Date.now() - new Date(oldest).getTime()) / 86_400_000) : null;
  });

  readonly anchoringProblems = computed(() => {
    const a = this.data()?.attention;
    return a ? a.anchoringFailed + a.anchoringDelayed : 0;
  });

  readonly allClear = computed(() => {
    const a = this.data()?.attention;
    return !!a && a.pendingApproval + (a.pendingFinalApproval ?? 0) + (this.auth.hasRole('InstitutionAdmin') ? a.controlRequestsPending ?? 0 : 0) + a.rejectedLast30Days + a.expiringIn30Days + a.revocationRequestsPending + a.importsPending
      + a.awaitingPayment + a.cancelledUnpaidLast30Days + (this.auth.hasRole('InstitutionAdmin') ? a.webhooksFailing : 0)
      + this.anchoringProblems() === 0;
  });

  // ---------- Charts ----------

  readonly issuedBars = computed(() => this.bars(this.data()?.issuedByMonth ?? [], true));
  readonly verificationBars = computed(() => this.bars(this.data()?.verificationsByMonth ?? [], false));

  readonly issuedTotal = computed(() => (this.data()?.issuedByMonth ?? []).reduce((s, m) => s + m.count, 0));
  readonly verificationTotal = computed(() => (this.data()?.verificationsByMonth ?? []).reduce((s, m) => s + m.count, 0));

  readonly issuedChange = computed(() => {
    const t = this.data()?.totals;
    return t ? t.issuedThisMonth - t.issuedLastMonth : 0;
  });

  // Certificates issued in the 12 months shown (matches the charts' counts).
  sinceRangeStart(extra: Params): Params {
    return { ...extra, issued: 'custom', from: this.data()?.rangeStart ?? '' };
  }

  // Width of a horizontal bar, relative to the longest in its list.
  share(count: number, all: { count: number }[]): number {
    const max = Math.max(1, ...all.map((x) => x.count));
    return Math.max(count > 0 ? 2 : 0, Math.round((count / max) * 100));
  }

  channelLabel(channel: string): string {
    return (
      {
        VerifyPage: 'Verification page (link or QR)',
        CodeLookup: 'Code look-up',
        CredentialDownload: 'Digital credential downloaded',
        Api: 'API',
      }[channel] ?? channel
    );
  }

  private bars(months: DashboardMonthCount[], linked: boolean): Bar[] {
    const max = Math.max(1, ...months.map((m) => m.count));
    return months.map((m, i) => {
      const [year, month] = m.month.split('-').map(Number);
      const date = new Date(year, month - 1, 1);
      const short = date.toLocaleString(undefined, { month: 'short' });
      const lastDay = new Date(year, month, 0).getDate();
      return {
        // The year under January and the first bar, so the axis stays readable.
        label: i === 0 || month === 1 ? `${short} ${String(year).slice(2)}` : short,
        title: date.toLocaleString(undefined, { month: 'long', year: 'numeric' }),
        count: m.count,
        height: m.count === 0 ? 0 : Math.max(3, Math.round((m.count / max) * 100)),
        link: linked ? { issued: 'custom', from: `${m.month}-01`, to: `${m.month}-${String(lastDay).padStart(2, '0')}` } : null,
      };
    });
  }
}
