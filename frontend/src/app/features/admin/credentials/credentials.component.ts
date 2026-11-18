import { Component, DestroyRef, HostListener, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/services/auth.service';
import { CertificateSearchService } from '../../../core/services/certificate-search.service';
import { CertificateActivityService } from '../../../core/services/certificate-activity.service';
import { CredentialsService } from '../../../core/services/credentials.service';
import { CertificateTemplatesService } from '../../../core/services/certificate-templates.service';
import { LedgerService } from '../../../core/services/ledger.service';
import { TenantSettingsService } from '../../../core/services/tenant-settings.service';
import {
  BulkActionRequest,
  BulkActionResponse,
  BulkOperation,
  Credential,
  CredentialFilterOptions,
  CredentialListQuery,
  CredentialSummary,
  CredentialVerificationHistory,
  displayStatus,
  DisplayStatus,
  FieldValues,
} from '../../../core/models/credential.models';
import { FieldDefinition } from '../../../core/models/achievement.models';
import { CertificateTemplate } from '../../../core/models/certificate-template.models';
import { LedgerEntry, LedgerIntegrityResponse } from '../../../core/models/ledger.models';
import { TenantBranding } from '../../../core/models/tenant.models';
import { CertificatePreviewData } from '../../../core/models/certificate-preview.models';
import { QrCodeComponent } from '../../../shared/qr-code/qr-code.component';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { CertificatePreviewComponent } from '../../../shared/certificate-preview/certificate-preview.component';
import { AnchorPanelComponent } from '../../../shared/anchor-panel/anchor-panel.component';
import { SignaturePanelComponent } from '../../../shared/signature-panel/signature-panel.component';

// Suspend / revoke / reactivate are on the Revoke & suspend page.
type ReasonAction = 'reject';
type DetailTab = 'overview' | 'audit' | 'verification';
type QuickView = 'approvals' | 'mine' | 'issued' | 'all' | 'custom';

const DENSITY_KEY = 'certyfi_certificates_density';

function readDensity(): 'comfortable' | 'compact' {
  try {
    return localStorage.getItem(DENSITY_KEY) === 'compact' ? 'compact' : 'comfortable';
  } catch {
    return 'comfortable';
  }
}

type IssuedPreset = 'thisMonth' | 'last30' | 'thisYear' | 'lastYear' | 'custom';

interface MoreFilters {
  issued: IssuedPreset | null;
  // yyyy-mm-dd from the date inputs, both inclusive (custom range only).
  issuedFromDate: string;
  issuedToDate: string;
  expiringWithinDays: number | null;
  source: 'Staff' | 'Api' | null;
  apiClientId: string | null;
}

const NO_MORE_FILTERS: MoreFilters = {
  issued: null, issuedFromDate: '', issuedToDate: '', expiringWithinDays: null, source: null, apiClientId: null,
};

// The browser's local midnights as instants, so "this month" is the user's
// month rather than UTC's. The upper bound is exclusive (the day after the
// last day chosen).
function issuedRange(m: MoreFilters): { from: string | null; before: string | null } {
  const today = new Date();
  const y = today.getFullYear();
  const day = (yy: number, mm: number, dd: number) => new Date(yy, mm, dd).toISOString();
  const parse = (value: string, plusDays = 0) => {
    const [yy, mm, dd] = value.split('-').map(Number);
    return yy && mm && dd ? day(yy, mm - 1, dd + plusDays) : null;
  };
  switch (m.issued) {
    case 'thisMonth': return { from: day(y, today.getMonth(), 1), before: null };
    case 'last30': return { from: day(y, today.getMonth(), today.getDate() - 29), before: null };
    case 'thisYear': return { from: day(y, 0, 1), before: null };
    case 'lastYear': return { from: day(y - 1, 0, 1), before: day(y, 0, 1) };
    case 'custom': return { from: parse(m.issuedFromDate), before: parse(m.issuedToDate, 1) };
    default: return { from: null, before: null };
  }
}

// ---------- URL state (plan phase 6) ----------
// Every filter, the search, sort, page and the open certificate live in the
// query string, so a view can be bookmarked, shared, refreshed and navigated
// with Back. Values that don't parse are dropped (and the URL tidied).
const STATUSES: DisplayStatus[] = ['Active', 'AwaitingPayment', 'PendingApproval', 'PendingFinalApproval', 'Suspended', 'Revoked', 'Rejected', 'Cancelled', 'Expired'];
const VIEWS: QuickView[] = ['approvals', 'mine', 'issued', 'all'];
const SORTS = ['created', 'issued', 'recipient', 'expires'];
const ISSUED_PRESETS: IssuedPreset[] = ['thisMonth', 'last30', 'thisYear', 'lastYear', 'custom'];
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
// Changing only these replaces the history entry instead of adding one, so
// typing a search or stepping through certificates doesn't flood Back.
const QUIET_PARAMS = ['q', 'open'];

function sameParams(a: Params, b: Params): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  return [...keys].every((k) => a[k] === b[k]);
}

function onlyQuietChanges(a: Params, b: Params): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  return [...keys].every((k) => QUIET_PARAMS.includes(k) || a[k] === b[k]);
}

function paramsOf(map: ParamMap): Params {
  const params: Params = {};
  for (const key of map.keys) params[key] = map.get(key);
  return params;
}

@Component({
  selector: 'app-credentials',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, QrCodeComponent, ModalComponent, CertificatePreviewComponent, SignaturePanelComponent, AnchorPanelComponent],
  templateUrl: './credentials.component.html',
})
export class CredentialsComponent implements OnInit {
  // One page of summaries from the server (see CredentialsController.List);
  // the full record for the open certificate is `selectedCredential`.
  readonly rows = signal<CredentialSummary[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = 25;
  readonly statusCounts = signal<Record<string, number>>({});
  readonly filterOptions = signal<CredentialFilterOptions>({ achievements: [], requesters: [], apiClients: [] });
  readonly templates = signal<CertificateTemplate[]>([]);
  // True until the first page arrives; later refreshes keep the list visible.
  readonly loading = signal(true);
  readonly refreshing = signal(false);
  readonly errorMessage = signal<string | null>(null);
  // What just happened after a single approval, since the drawer closes when
  // the certificate leaves "Needs my approval".
  readonly notice = signal<string | null>(null);
  private noticeTimer?: ReturnType<typeof setTimeout>;
  readonly showForm = signal(false);
  readonly submitting = signal(false);
  readonly branding = signal<TenantBranding | null>(null);

  // Split list/detail view — see the credentials.component.html header
  // comment for the overall shape. Only ONE credential is ever "acted on"
  // at a time (the selected one); the list itself is selection-only.
  readonly selectedCredential = signal<Credential | null>(null);
  readonly activeTab = signal<DetailTab>('overview');
  readonly ledgerEntries = signal<LedgerEntry[]>([]);
  readonly verificationHistory = signal<CredentialVerificationHistory | null>(null);
  readonly ledgerLoading = signal(false);

  // Filters, all applied on the server. Any change goes back to page 1.
  // The header search box (CertificateSearchService) is debounced 300 ms
  // into `searchText` so typing doesn't send a request per keystroke.
  readonly statusFilter = signal<DisplayStatus | null>(null);
  readonly achievementFilter = signal<string | null>(null);
  readonly requestedByFilter = signal<string | null>(null);
  private readonly searchText = signal('');
  // "More filters" popover (plan phase 5): issued between, expiring within,
  // source / API key. Kept in one object so the badge can count what's set.
  readonly more = signal<MoreFilters>(NO_MORE_FILTERS);
  readonly moreOpen = signal(false);
  private searchTimer: ReturnType<typeof setTimeout> | undefined;
  private requestSeq = 0;

  // Quick views (plan phase 2): presets of the filters above, shown as tabs.
  // The page opens on the one that fits the user's role (see defaultView);
  // changing any dropdown leaves the preset ("custom").
  readonly view = signal<QuickView>('all');
  private readonly mineOnly = signal(false);
  private readonly excludeMine = signal(false);
  private readonly awaitingMe = signal(false);
  // "Needs my approval" leaves out imports (approved as a batch); `batch`
  // lists one paper import's certificates (linked from the Import page).
  private readonly excludeImports = signal(false);
  readonly batchFilter = signal<string | null>(null);
  private readonly sort = signal('-created');
  // No request until the default view is chosen (avoids a wasted first fetch).
  private readonly ready = signal(false);

  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  // The query string last written or applied; lets the URL effect and the
  // route subscription each ignore the other's echo.
  private lastUrl: Params | null = null;
  private tidyUrlNext = false;
  // The default view needs the user's counts; with no URL state it waits
  // for them (at most once per visit).
  private countsLoaded = false;
  private defaultViewWaiting = false;

  private readonly isLearnerOnly = computed(
    () => this.auth.hasRole('Learner') && !['InstitutionAdmin', 'Registrar', 'Approver'].some((r) => this.auth.hasRole(r)),
  );

  readonly quickViews = computed(() => {
    const s = this.activity.summary();
    const views: { id: QuickView; label: string; count: number | null; note: string | null }[] = [];
    if (s?.canApprove) views.push({ id: 'approvals', label: 'Needs my approval', count: s.pendingForMe, note: null });
    if (['Registrar', 'Learner'].some((r) => this.auth.hasRole(r))) {
      views.push({
        id: 'mine', label: 'My requests', count: s?.myRequests ?? null,
        note: s?.myRequestsRejected ? `${s.myRequestsRejected} rejected` : null,
      });
    }
    if (!this.isLearnerOnly()) views.push({ id: 'issued', label: 'Recently issued', count: null, note: null });
    views.push({ id: 'all', label: this.isLearnerOnly() ? 'My certificates' : 'All', count: null, note: null });
    return views;
  });

  applyView(view: QuickView): void {
    this.view.set(view);
    this.achievementFilter.set(null);
    this.requestedByFilter.set(null);
    this.more.set(NO_MORE_FILTERS);
    // The server decides what's mine to approve: first approvals for
    // Approvers, final approvals for institution admins.
    this.statusFilter.set(null);
    this.awaitingMe.set(view === 'approvals');
    this.excludeMine.set(false);
    this.excludeImports.set(false);
    this.batchFilter.set(null);
    this.mineOnly.set(view === 'mine');
    this.sort.set(view === 'approvals' ? 'created' : view === 'issued' ? '-issued' : '-created');
    this.page.set(1);
  }

  // Approvers with something waiting land on their queue; registrars on
  // their requests; learners on their own certificates; others on the
  // latest issued.
  private defaultView(): QuickView {
    const s = this.activity.summary();
    if (s?.canApprove && s.pendingForMe > 0) return 'approvals';
    if (this.auth.hasRole('Registrar')) return 'mine';
    if (this.isLearnerOnly()) return 'all';
    return 'issued';
  }

  // A manual filter change leaves the preset: drop its hidden parts
  // (mine / not-mine, sort) and keep the visible filters as they are.
  private leaveView(): void {
    if (this.view() === 'custom') return;
    this.view.set('custom');
    this.mineOnly.set(false);
    this.excludeMine.set(false);
    this.excludeImports.set(false);
    this.sort.set('-created');
  }

  setStatus(value: DisplayStatus | null): void {
    this.leaveView();
    this.statusFilter.set(value);
    this.page.set(1);
  }

  setAchievement(value: string | null): void {
    this.leaveView();
    this.achievementFilter.set(value);
    this.page.set(1);
  }

  clearBatch(): void {
    this.leaveView();
    this.batchFilter.set(null);
    this.page.set(1);
  }

  setRequester(value: string | null): void {
    this.leaveView();
    this.requestedByFilter.set(value);
    this.page.set(1);
  }

  // "Expiring within" sorts soonest first, since that's the list to act on.
  setMore(change: Partial<MoreFilters>): void {
    this.leaveView();
    const next = { ...this.more(), ...change };
    if (next.issued !== 'custom') {
      next.issuedFromDate = '';
      next.issuedToDate = '';
    }
    if (next.source !== 'Api') next.apiClientId = null;
    if (change.expiringWithinDays) this.sort.set('expires');
    this.more.set(next);
    this.page.set(1);
  }

  readonly moreCount = computed(() => {
    const m = this.more();
    return [m.issued !== null, m.expiringWithinDays !== null, m.source !== null].filter(Boolean).length;
  });

  readonly issuedRange = computed(() => issuedRange(this.more()));

  readonly issuedPresets: { id: IssuedPreset; label: string }[] = [
    { id: 'thisMonth', label: 'This month' }, { id: 'last30', label: 'Last 30 days' },
    { id: 'thisYear', label: 'This year' }, { id: 'lastYear', label: 'Last year' }, { id: 'custom', label: 'Custom…' },
  ];
  readonly sourceOptions: { id: 'Staff' | 'Api' | null; label: string }[] = [
    { id: null, label: 'Any' }, { id: 'Staff', label: 'Staff' }, { id: 'Api', label: 'Partner API' },
  ];

  readonly showSourceFilter = computed(() => !this.isLearnerOnly());

  // A different filter means a different set of certificates: drop any
  // selection so bulk actions never apply to rows the user can't see.
  private readonly filterKey = computed(() => {
    const { page: _page, ...rest } = this.listQuery();
    return JSON.stringify(rest);
  });

  private readonly listQuery = computed<CredentialListQuery>(() => ({
    q: this.searchText(),
    status: this.statusFilter(),
    achievementId: this.achievementFilter(),
    requestedBy: this.requestedByFilter(),
    mine: this.mineOnly() || null,
    excludeMine: this.excludeMine() || null,
    awaitingMe: this.awaitingMe() || null,
    excludeImports: this.excludeImports() || null,
    importBatchId: this.batchFilter(),
    issuedFrom: this.issuedRange().from,
    issuedBefore: this.issuedRange().before,
    expiringWithinDays: this.more().expiringWithinDays,
    source: this.more().source,
    apiClientId: this.more().apiClientId,
    sort: this.sort(),
    page: this.page(),
    pageSize: this.pageSize,
  }));

  goToPage(page: number): void {
    const last = Math.max(1, Math.ceil(this.total() / this.pageSize));
    this.page.set(Math.min(Math.max(1, page), last));
  }

  readonly rangeStart = computed(() => (this.total() === 0 ? 0 : (this.page() - 1) * this.pageSize + 1));
  readonly rangeEnd = computed(() => Math.min(this.page() * this.pageSize, this.total()));
  readonly lastPage = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize)));

  // Options for the Status dropdown with the server's counts (every filter
  // except status applied), so each number is what choosing it will show.
  readonly statusOptions = computed(() => {
    const counts = this.statusCounts();
    const labels: [DisplayStatus, string][] = [
      ['Active', 'Active'], ['AwaitingPayment', 'Awaiting payment'], ['PendingApproval', 'Pending approval'], ['PendingFinalApproval', 'Final approval'], ['Suspended', 'Suspended'],
      ['Revoked', 'Revoked'], ['Rejected', 'Rejected'], ['Cancelled', 'Cancelled'], ['Expired', 'Expired'],
    ];
    return {
      total: Object.values(counts).reduce((sum, n) => sum + n, 0),
      options: labels.map(([status, label]) => ({ status, label, count: counts[status] ?? 0 })),
    };
  });

  readonly hasFilters = computed(
    () => !!(this.statusFilter() || this.achievementFilter() || this.requestedByFilter() || this.searchText() || this.moreCount() || this.batchFilter()),
  );

  clearFilters(): void {
    this.search.query.set('');
    this.searchText.set('');
    this.applyView('all');
  }

  // Data-protection erasure (admins; revoked or rejected certificates).
  readonly eraseCredential = signal<Credential | null>(null);
  readonly erasing = signal(false);
  readonly eraseError = signal<string | null>(null);
  eraseCode = '';

  openErase(credential: Credential): void {
    this.eraseCode = '';
    this.eraseError.set(null);
    this.eraseCredential.set(credential);
  }

  eraseCodeMatches(): boolean {
    const c = this.eraseCredential();
    const norm = (v: string) => v.replace(/[^0-9a-z]/gi, '').toUpperCase();
    return !!c && norm(this.eraseCode) === norm(c.verificationCode);
  }

  submitErase(): void {
    const c = this.eraseCredential();
    if (!c || !this.eraseCodeMatches()) return;
    this.erasing.set(true);
    this.credentialsService.erasePersonalData(c.id, this.eraseCode).subscribe({
      next: () => {
        this.erasing.set(false);
        this.eraseCredential.set(null);
        this.load();
      },
      error: (err) => {
        this.erasing.set(false);
        this.eraseError.set(err.error?.error ?? 'Could not erase the personal data.');
      },
    });
  }

  readonly reasonModalCredential = signal<Credential | null>(null);
  readonly reasonAction = signal<ReasonAction | null>(null);
  reasonText = '';

  readonly previewOpen = signal(false);

  // InstitutionAdmin-only chain-integrity check — see LedgerController.
  readonly integrityResult = signal<LedgerIntegrityResponse | null>(null);
  readonly checkingIntegrity = signal(false);
  readonly showIntegrityDetails = signal(false);
  readonly integrityReportCopied = signal(false);

  templateId = '';
  title = '';
  recipientName = '';
  recipientEmail = '';
  // Initialized purely from the selected template's fieldSchema keys
  // whenever it changes — the submitted object can therefore only ever
  // contain schema-defined keys, never caller-injected ones.
  fieldValues: FieldValues = {};

  constructor(
    protected auth: AuthService,
    private credentialsService: CredentialsService,
    private certificateTemplatesService: CertificateTemplatesService,
    private ledgerService: LedgerService,
    private tenantSettingsService: TenantSettingsService,
    readonly search: CertificateSearchService,
    readonly activity: CertificateActivityService,
  ) {
    // Header search → debounced searchText (and back to page 1).
    effect(() => {
      const text = this.search.query().trim();
      clearTimeout(this.searchTimer);
      this.searchTimer = setTimeout(() => {
        if (text !== this.searchText()) {
          this.searchText.set(text);
          this.page.set(1);
        }
      }, 300);
    });

    effect(() => {
      this.filterKey();
      untracked(() => this.clearSelection());
    });

    // Keep the address bar in step with the page state.
    effect(() => {
      const params = this.urlState();
      if (!this.ready()) return;
      untracked(() => this.writeUrl(params));
    });

    // Fetch whenever a filter, view or the page changes (once the default
    // view has been chosen).
    effect(() => {
      const query = this.listQuery();
      if (!this.ready()) return;
      untracked(() => this.fetch(query));
    });
  }

  ngOnInit(): void {
    // The address bar drives the page state (see applyUrl); counts for the
    // quick-view tabs load alongside.
    const countsDone = () => {
      this.countsLoaded = true;
      if (this.defaultViewWaiting) this.startDefaultView();
    };
    this.activity.refresh().subscribe({ next: countsDone, error: countsDone });
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((map) => this.applyUrl(paramsOf(map)));
    this.loadFilterOptions();
    this.certificateTemplatesService.list().subscribe({
      next: (templates) => this.templates.set(templates),
      error: () => {
        /* Template dropdown just stays empty; the create form's own hint covers it. */
      },
    });
    this.tenantSettingsService.getBranding().subscribe({
      next: (branding) => this.branding.set(branding),
      error: () => {
        /* Previews just render without a logo/color — cosmetic only. */
      },
    });
  }

  // The page state as query parameters; defaults are left out.
  private readonly urlState = computed<Params>(() => {
    const m = this.more();
    const entries: [string, string | number | null | undefined | false][] = [
      ['view', this.view() === 'custom' ? null : this.view()],
      ['status', this.statusFilter()],
      ['achievement', this.achievementFilter()],
      ['requestedBy', this.requestedByFilter()],
      ['mine', this.mineOnly() && '1'],
      ['others', this.excludeMine() && '1'],
      ['awaiting', this.view() !== 'approvals' && this.awaitingMe() && '1'],
      ['noImports', this.excludeImports() && '1'],
      ['batch', this.batchFilter()],
      ['q', this.searchText()],
      ['issued', m.issued],
      ['from', m.issued === 'custom' ? m.issuedFromDate : null],
      ['to', m.issued === 'custom' ? m.issuedToDate : null],
      ['expiring', m.expiringWithinDays],
      ['source', m.source],
      ['apiKey', m.apiClientId],
      ['sort', this.sort() === '-created' ? null : this.sort()],
      ['page', this.page() > 1 ? this.page() : null],
      ['open', this.drawerOpen() ? this.openId() : null],
    ];
    const params: Params = {};
    for (const [key, value] of entries) if (value) params[key] = String(value);
    return params;
  });

  private writeUrl(params: Params): void {
    if (this.lastUrl && sameParams(params, this.lastUrl)) return;
    const replace = this.tidyUrlNext || !this.lastUrl || onlyQuietChanges(params, this.lastUrl);
    this.tidyUrlNext = false;
    this.lastUrl = params;
    void this.router.navigate([], { relativeTo: this.route, queryParams: params, replaceUrl: replace });
  }

  // From the address bar (first visit, a shared link, Back/Forward, or the
  // sidebar link). No parameters at all → the role's default view.
  private applyUrl(params: Params): void {
    if (this.lastUrl && sameParams(params, this.lastUrl)) return;
    this.lastUrl = params;
    this.tidyUrlNext = true;

    if (Object.keys(params).length === 0) {
      if (this.countsLoaded) this.startDefaultView();
      else this.defaultViewWaiting = true;
      return;
    }
    this.defaultViewWaiting = false;

    const pick = <T extends string>(value: string | undefined, allowed: readonly T[]): T | null =>
      value && (allowed as readonly string[]).includes(value) ? (value as T) : null;
    const guid = (value: string | undefined) => (value && GUID.test(value) ? value : null);
    const sortKey = params['sort']?.replace(/^-/, '');

    this.view.set(pick(params['view'], VIEWS) ?? 'custom');
    this.statusFilter.set(pick(params['status'], STATUSES));
    this.achievementFilter.set(guid(params['achievement']));
    this.requestedByFilter.set(guid(params['requestedBy']));
    this.mineOnly.set(params['mine'] === '1');
    this.excludeMine.set(params['others'] === '1');
    this.awaitingMe.set(params['view'] === 'approvals' || params['awaiting'] === '1');
    this.excludeImports.set(params['noImports'] === '1');
    this.batchFilter.set(guid(params['batch']));
    const q = (params['q'] ?? '').trim();
    this.searchText.set(q);
    this.search.query.set(q);
    const issued = pick(params['issued'], ISSUED_PRESETS);
    const expiring = Number(params['expiring']);
    this.more.set({
      issued,
      issuedFromDate: issued === 'custom' && DATE.test(params['from'] ?? '') ? params['from'] : '',
      issuedToDate: issued === 'custom' && DATE.test(params['to'] ?? '') ? params['to'] : '',
      expiringWithinDays: Number.isInteger(expiring) && expiring > 0 && expiring <= 3650 ? expiring : null,
      source: pick(params['source'], ['Staff', 'Api'] as const),
      apiClientId: params['source'] === 'Api' ? guid(params['apiKey']) : null,
    });
    this.sort.set(sortKey && SORTS.includes(sortKey) ? params['sort'] : '-created');
    const page = Number(params['page']);
    this.page.set(Number.isInteger(page) && page > 1 ? page : 1);

    const open = guid(params['open']);
    if (open) {
      this.drawerOpen.set(true);
      if (open !== this.selectedCredential()?.id || open !== this.openId()) this.openCredential(open);
    } else {
      this.drawerOpen.set(false);
    }
    this.ready.set(true);
  }

  private startDefaultView(): void {
    this.defaultViewWaiting = false;
    this.applyView(this.defaultView());
    this.openId.set(null);
    this.drawerOpen.set(false);
    this.ready.set(true);
  }

  // Reloads the current page (after creating or acting on a certificate)
  // and the open certificate's full record.
  load(): void {
    this.fetch(this.listQuery());
    this.loadFilterOptions();
    this.activity.refresh().subscribe({ error: () => undefined });
    const selected = this.selectedCredential();
    if (selected) this.openCredential(selected.id, this.activeTab());
  }

  private fetch(query: CredentialListQuery): void {
    const seq = ++this.requestSeq;
    this.refreshing.set(true);
    this.credentialsService.list(query).subscribe({
      next: (page) => {
        if (seq !== this.requestSeq) return; // a newer request is in flight
        this.rows.set(page.items);
        this.total.set(page.total);
        this.statusCounts.set(page.statusCounts);
        this.loading.set(false);
        this.refreshing.set(false);
        // Stepping past the end of a page in the drawer loads the next page
        // and continues on its first (or previous page's last) row.
        const pending = this.openAfterFetch;
        this.openAfterFetch = null;
        if (pending && page.items.length) {
          this.openCredential(pending === 'first' ? page.items[0].id : page.items[page.items.length - 1].id);
        }
      },
      error: () => {
        if (seq !== this.requestSeq) return;
        this.errorMessage.set('Failed to load certificates.');
        this.loading.set(false);
        this.refreshing.set(false);
      },
    });
  }

  private loadFilterOptions(): void {
    this.credentialsService.filterOptions().subscribe({ next: (options) => this.filterOptions.set(options) });
  }

  displayStatus(credential: Credential): DisplayStatus {
    return displayStatus(credential);
  }

  // Small, fixed status→style lookups — kept here instead of a long
  // @switch repeated in three places in the template (stat cards use their
  // own fixed colors, but the list rows and detail header share this).
  statusBadgeClasses(status: DisplayStatus): string {
    switch (status) {
      case 'Active':
        return 'bg-green-100 text-green-800';
      case 'AwaitingPayment':
        return 'bg-sky-100 text-sky-800';
      case 'PendingApproval':
        return 'bg-amber-100 text-amber-800';
      case 'PendingFinalApproval':
        return 'bg-violet-100 text-violet-800';
      case 'Suspended':
        return 'bg-orange-100 text-orange-800';
      case 'Revoked':
        return 'bg-red-100 text-red-800';
      case 'Rejected':
      case 'Cancelled':
        return 'bg-gray-100 text-gray-600';
      case 'Expired':
        return 'bg-gray-200 text-gray-700';
    }
  }

  statusLabel(status: DisplayStatus): string {
    return status === 'PendingApproval' ? 'Pending approval' : status === 'PendingFinalApproval' ? 'Final approval' : status === 'AwaitingPayment' ? 'Awaiting payment' : status;
  }

  avatarClasses(status: DisplayStatus): string {
    switch (status) {
      case 'Active':
        return 'bg-green-100 text-green-700';
      case 'AwaitingPayment':
        return 'bg-sky-100 text-sky-700';
      case 'PendingApproval':
        return 'bg-[#f3d9dc] text-[#7a2331]';
      case 'PendingFinalApproval':
        return 'bg-violet-100 text-violet-700';
      case 'Suspended':
        return 'bg-orange-100 text-orange-700';
      case 'Revoked':
        return 'bg-red-100 text-red-700';
      case 'Rejected':
      case 'Cancelled':
        return 'bg-gray-100 text-gray-500';
      case 'Expired':
        return 'bg-gray-100 text-gray-500';
    }
  }

  // Ledger event types are PascalCase ("RevocationRequestRejected"); show them as words.
  eventLabel(type: string): string {
    const words = type.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
    return words.charAt(0).toUpperCase() + words.slice(1);
  }

  initial(name: string): string {
    return (name.trim()[0] ?? '?').toUpperCase();
  }

  // Client-side hint only — the server is the real enforcement (see
  // CredentialsController.Approve/Reject). Disables the button and shows a
  // tooltip rather than letting the user submit and hit a 403.
  isOwnDraft(credential: Credential): boolean {
    return credential.requestedByUserId === this.auth.currentUser()?.['sub'];
  }

  // This user's role fits the step it's at: first approval (Approver) or
  // final approval (Institution admin).
  canDecideStage(credential: Credential): boolean {
    if (credential.importBatchId) return false;
    if (credential.status === 'PendingApproval') return this.auth.hasRole('Approver');
    if (credential.status === 'PendingFinalApproval') return this.auth.hasRole('InstitutionAdmin');
    return false;
  }

  // Why this user can't decide it (button tooltip), or '' if they can.
  decisionBlock(credential: Credential): string {
    if (this.isOwnDraft(credential)) return 'You requested it; someone else must decide it.';
    if (credential.status === 'PendingFinalApproval' && credential.firstApprovedByUserId === this.auth.currentUser()?.['sub'])
      return 'You gave the first approval; a different institution admin gives the final one.';
    return '';
  }

  // ---------- Table and drawer (plan phase 3) ----------

  // The certificate opens in a side drawer over the table.
  readonly drawerOpen = signal(false);
  // The certificate the drawer shows (set before its record has loaded, so
  // the URL keeps ?open= while it loads).
  private readonly openId = signal<string | null>(null);
  private openAfterFetch: 'first' | 'last' | null = null;

  // Row padding, remembered on this device.
  readonly density = signal<'comfortable' | 'compact'>(readDensity());

  setDensity(value: 'comfortable' | 'compact'): void {
    this.density.set(value);
    try {
      localStorage.setItem(DENSITY_KEY, value);
    } catch {
      /* storage blocked: the choice just isn't remembered */
    }
  }

  selectCredential(row: CredentialSummary): void {
    this.drawerOpen.set(true);
    if (this.selectedCredential()?.id !== row.id) this.openCredential(row.id);
  }

  closeDrawer(): void {
    this.drawerOpen.set(false);
  }

  // Position of the open certificate in the current page (-1 if it isn't on it).
  readonly selectedIndex = computed(() => {
    const id = this.selectedCredential()?.id;
    return id ? this.rows().findIndex((r) => r.id === id) : -1;
  });

  readonly hasPrevious = computed(() => this.selectedIndex() > 0 || (this.selectedIndex() === 0 && this.page() > 1));
  readonly hasNext = computed(() => {
    const i = this.selectedIndex();
    return i >= 0 && (i < this.rows().length - 1 || this.page() < this.lastPage());
  });

  step(direction: 1 | -1): void {
    const i = this.selectedIndex();
    const target = this.rows()[i + direction];
    if (target) {
      this.openCredential(target.id);
    } else if (direction === 1 && this.page() < this.lastPage()) {
      this.openAfterFetch = 'first';
      this.page.set(this.page() + 1);
    } else if (direction === -1 && this.page() > 1) {
      this.openAfterFetch = 'last';
      this.page.set(this.page() - 1);
    }
  }

  // Column sorting. Dates start newest-first, names A–Z; clicking the
  // sorted column again reverses it. Doesn't leave the current view.
  sortBy(key: 'recipient' | 'created' | 'issued' | 'expires'): void {
    const current = this.sort();
    const next = current === key ? `-${key}` : current === `-${key}` ? key : key === 'recipient' ? key : `-${key}`;
    this.sort.set(next);
    this.page.set(1);
  }

  sortState(key: string): 'ascending' | 'descending' | null {
    const current = this.sort();
    return current === key ? 'ascending' : current === `-${key}` ? 'descending' : null;
  }

  // ---------- Bulk actions and export (plan phase 4) ----------

  // Selection: specific rows (kept across pages), or "all N matching the
  // filters" — then the server resolves the set (max 500). Cleared whenever
  // the filters change, so a selection never silently means something else.
  readonly selectedIds = signal<ReadonlySet<string>>(new Set());
  readonly allMatching = signal(false);
  readonly bulkMax = 500;
  readonly canBulk = computed(() => this.auth.hasRole('Approver') || this.auth.hasRole('InstitutionAdmin'));
  readonly selectionCount = computed(() => (this.allMatching() ? this.total() : this.selectedIds().size));
  readonly allOnPageSelected = computed(() => this.rows().length > 0 && this.rows().every((r) => this.selectedIds().has(r.id)));

  readonly bulkReasonAction = signal<Exclude<BulkOperation, 'approve'> | null>(null);
  bulkReason = '';
  readonly bulkRunning = signal(false);
  readonly bulkResult = signal<BulkActionResponse | null>(null);
  readonly bulkError = signal<string | null>(null);
  readonly exporting = signal(false);

  toggleRow(id: string): void {
    this.allMatching.set(false);
    const next = new Set(this.selectedIds());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.selectedIds.set(next);
  }

  toggleAllOnPage(): void {
    this.allMatching.set(false);
    const next = new Set(this.selectedIds());
    if (this.allOnPageSelected()) this.rows().forEach((r) => next.delete(r.id));
    else this.rows().forEach((r) => next.add(r.id));
    this.selectedIds.set(next);
  }

  selectAllMatching(): void {
    this.allMatching.set(true);
  }

  clearSelection(): void {
    this.selectedIds.set(new Set());
    this.allMatching.set(false);
  }

  // The list's filters without paging: what "all matching" and export use.
  private filterOnly(): CredentialListQuery {
    const { page: _page, pageSize: _size, ...filter } = this.listQuery();
    return filter;
  }

  private selectionPayload(): BulkActionRequest {
    return this.allMatching() ? { filter: this.filterOnly() } : { ids: [...this.selectedIds()] };
  }

  bulkApprove(): void {
    if (!confirm(`Approve ${this.selectionCount()} certificate(s)? Any you requested yourself, or that aren't pending, are skipped.`)) return;
    this.runBulk('approve');
  }

  openBulkReason(action: Exclude<BulkOperation, 'approve'>): void {
    this.bulkReason = '';
    this.bulkError.set(null);
    this.bulkReasonAction.set(action);
  }

  submitBulkReason(): void {
    const action = this.bulkReasonAction();
    if (!action || !this.bulkReason.trim()) return;
    this.runBulk(action, this.bulkReason.trim());
  }

  private runBulk(action: BulkOperation, reason?: string): void {
    this.bulkRunning.set(true);
    this.bulkError.set(null);
    this.credentialsService.bulk(action, { ...this.selectionPayload(), reason }).subscribe({
      next: (result) => {
        this.bulkRunning.set(false);
        this.bulkReasonAction.set(null);
        this.bulkResult.set(result);
        this.clearSelection();
        this.load();
      },
      error: (err) => {
        this.bulkRunning.set(false);
        this.bulkError.set(err.error?.error ?? `Couldn't ${action} the selected certificates.`);
        if (!this.bulkReasonAction()) this.errorMessage.set(this.bulkError());
      },
    });
  }

  exportCsv(): void {
    this.exporting.set(true);
    this.credentialsService.exportCsv(this.filterOnly()).subscribe({
      next: (blob) => {
        this.exporting.set(false);
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `certificates-${this.auth.currentUser()?.tenant_slug ?? 'export'}-${new Date().toISOString().slice(0, 10)}.csv`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: async (err) => {
        this.exporting.set(false);
        // Error bodies arrive as a Blob here; read the message out of it.
        let message = "Couldn't export the certificates.";
        try {
          message = JSON.parse(await (err.error as Blob).text()).error ?? message;
        } catch {
          /* keep the generic message */
        }
        this.errorMessage.set(message);
      },
    });
  }

  // Esc closes the drawer — unless a dialog is open on top of it, which
  // handles Esc itself.
  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.moreOpen()) {
      this.moreOpen.set(false);
      return;
    }
    if (this.showForm() || this.previewOpen() || this.reasonModalCredential() || this.eraseCredential() || this.showIntegrityDetails() || this.bulkReasonAction() || this.bulkResult()) return;
    this.closeDrawer();
  }

  // Loads the full record for the detail panel. Staying on the same
  // certificate keeps the tab (e.g. after an action); a different one opens
  // on Overview.
  openCredential(id: string, tab: DetailTab = 'overview'): void {
    this.openId.set(id);
    this.credentialsService.get(id).subscribe({
      next: (credential) => {
        if (this.openId() !== id) return; // another certificate was opened meanwhile
        this.selectedCredential.set(credential);
        this.setTab(tab);
      },
      error: () => {
        if (this.openId() !== id) return;
        this.errorMessage.set('Failed to load the certificate.');
        this.openId.set(null);
        this.drawerOpen.set(false);
      },
    });
  }

  setTab(tab: DetailTab): void {
    this.activeTab.set(tab);
    if (tab === 'audit') this.loadLedger();
    if (tab === 'verification') this.loadVerificationHistory();
  }

  private loadVerificationHistory(): void {
    const credential = this.selectedCredential();
    this.verificationHistory.set(null);
    if (!credential) return;
    this.credentialsService.verifications(credential.id).subscribe({
      next: (history) => this.verificationHistory.set(history),
    });
  }

  channelLabel(channel: string): string {
    return (
      { VerifyPage: 'Verification page', CodeLookup: 'Code look-up', CredentialDownload: 'Digital credential downloaded', Api: 'API' }[channel] ??
      channel
    );
  }

  private loadLedger(): void {
    const credential = this.selectedCredential();
    if (!credential) return;
    this.ledgerEntries.set([]);
    this.ledgerLoading.set(true);
    this.ledgerService.getLedger(credential.id).subscribe({
      next: (entries) => {
        this.ledgerEntries.set(entries);
        this.ledgerLoading.set(false);
      },
      error: () => {
        this.ledgerLoading.set(false);
      },
    });
  }

  toggleForm(): void {
    this.errorMessage.set(null);
    this.showForm.set(!this.showForm());
  }

  selectedTemplate(): CertificateTemplate | undefined {
    return this.templates().find((t) => t.id === this.templateId);
  }

  selectedFieldSchema(): FieldDefinition[] {
    return this.selectedTemplate()?.fieldSchema ?? [];
  }

  onTemplateChange(): void {
    const schema = this.selectedFieldSchema();
    const values: FieldValues = {};
    for (const field of schema) {
      values[field.key] = field.type === 'table' ? [this.emptyTableRow(field)] : '';
    }
    this.fieldValues = values;
  }

  private emptyTableRow(field: FieldDefinition): Record<string, string | null> {
    const row: Record<string, string | null> = {};
    for (const column of field.columns ?? []) {
      row[column.key] = '';
    }
    return row;
  }

  addTableRow(field: FieldDefinition): void {
    const rows = this.fieldValues[field.key];
    if (Array.isArray(rows)) rows.push(this.emptyTableRow(field));
  }

  removeTableRow(field: FieldDefinition, index: number): void {
    const rows = this.fieldValues[field.key];
    if (Array.isArray(rows)) rows.splice(index, 1);
  }

  tableRows(field: FieldDefinition): Array<Record<string, string | null>> {
    const rows = this.fieldValues[field.key];
    return Array.isArray(rows) ? rows : [];
  }

  signatoryNames(t: CertificateTemplate): string {
    return t.signatories.map((s) => `${s.name} (${s.designation})`).join(', ');
  }

  // Live preview of the in-progress draft — recomputed on every template
  // evaluation, so it always reflects whatever's currently in the form.
  previewData(): CertificatePreviewData {
    const template = this.selectedTemplate();
    return {
      recipientName: this.recipientName,
      achievementTitle: this.title,
      achievementDescription: template?.description ?? null,
      institutionName: this.branding()?.name ?? this.auth.currentUser()?.tenant_slug ?? '',
      institutionLogoUrl: this.branding()?.logoUrl ?? null,
      institutionPrimaryColor: this.branding()?.primaryColor ?? null,
      status: 'Draft',
      issuedAt: null,
      expiresOn: null,
      fieldSchema: template?.fieldSchema ?? [],
      fieldValues: this.fieldValues,
      templateKind: template?.kind ?? 'Certificate',
      layout: template?.layout ?? null,
      titleText: template?.titleText ?? null,
      subtitleText: template?.subtitleText ?? null,
      accentColor: template?.accentColor ?? null,
      fontFamily: template?.fontFamily ?? null,
      backgroundImageUrl: template?.backgroundImageUrl ?? null,
      signatories: template?.signatories ?? null,
    };
  }

  openPreview(): void {
    this.previewOpen.set(true);
  }

  closePreview(): void {
    this.previewOpen.set(false);
  }

  // The currently-selected credential's own CredentialResponse is self-
  // sufficient for this (FieldSchemaJson is a snapshot from draft time —
  // see Credential.FieldSchemaJson on the backend — so it stays
  // historically accurate even if the achievement is later amended).
  selectedCredentialPreviewData(): CertificatePreviewData | null {
    const credential = this.selectedCredential();
    if (!credential) return null;
    return {
      recipientName: credential.recipientName,
      achievementTitle: credential.achievementTitle,
      achievementDescription: credential.achievementDescription,
      institutionName: this.branding()?.name ?? this.auth.currentUser()?.tenant_slug ?? '',
      institutionLogoUrl: this.branding()?.logoUrl ?? null,
      institutionPrimaryColor: this.branding()?.primaryColor ?? null,
      status: displayStatus(credential),
      issuedAt: credential.issuedAt,
      expiresOn: credential.expiresOn,
      fieldSchema: credential.fieldSchema,
      fieldValues: credential.fieldValues,
      templateKind: credential.templateKind,
      layout: credential.layout,
      titleText: credential.titleText,
      subtitleText: credential.subtitleText,
      accentColor: credential.accentColor,
      fontFamily: credential.fontFamily,
      backgroundImageUrl: credential.backgroundImageUrl,
      originalIssueDate: credential.originalIssueDate,
      legacyCertificateNumber: credential.legacyCertificateNumber,
      signatories: credential.signatories ?? null,
    };
  }

  submit(): void {
    this.errorMessage.set(null);
    this.submitting.set(true);

    this.credentialsService
      .createFromTemplate({
        templateId: this.templateId,
        title: this.title,
        recipientName: this.recipientName,
        recipientEmail: this.recipientEmail,
        fieldValues: this.fieldValues,
      })
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.showForm.set(false);
          this.resetForm();
          this.load();
        },
        error: (err) => {
          this.submitting.set(false);
          this.errorMessage.set(err.error?.error ?? 'Failed to create certificate.');
        },
      });
  }

  approve(credential: Credential): void {
    this.errorMessage.set(null);
    this.notice.set(null);
    this.credentialsService.approve(credential.id).subscribe({
      next: (updated) => {
        this.showNotice(updated.status === 'PendingFinalApproval'
          ? `Approved. ${updated.recipientName}'s certificate now goes to an institution admin for final approval.`
          : `Issued ${updated.recipientName}'s certificate.`);
        this.load();
      },
      error: (err) => this.errorMessage.set(err.error?.error ?? 'Failed to approve credential.'),
    });
  }

  private showNotice(text: string): void {
    clearTimeout(this.noticeTimer);
    this.notice.set(text);
    this.noticeTimer = setTimeout(() => this.notice.set(null), 8000);
  }

  openReasonModal(credential: Credential, action: ReasonAction): void {
    this.errorMessage.set(null);
    this.reasonText = '';
    this.reasonModalCredential.set(credential);
    this.reasonAction.set(action);
  }

  closeReasonModal(): void {
    this.reasonModalCredential.set(null);
    this.reasonAction.set(null);
    this.reasonText = '';
  }

  submitReason(): void {
    const credential = this.reasonModalCredential();
    const action = this.reasonAction();
    if (!credential || !action || !this.reasonText.trim()) return;

    this.errorMessage.set(null);
    this.credentialsService.reject(credential.id, this.reasonText).subscribe({
      next: () => {
        this.closeReasonModal();
        this.load();
      },
      error: (err) => this.errorMessage.set(err.error?.error ?? `Failed to ${action} credential.`),
    });
  }

  verificationLink(credential: Credential): string {
    const slug = this.auth.currentUser()?.tenant_slug;
    return `${window.location.origin}/verify/${slug}/${credential.id}`;
  }

  // The QR code also carries the signed offline summary (#o=...), which
  // browsers never send to a server; the shared link stays short.
  qrData(credential: Credential): string {
    const link = this.verificationLink(credential);
    return credential.offlineProof ? `${link}#o=${credential.offlineProof}` : link;
  }

  copyVerificationLink(credential: Credential): void {
    navigator.clipboard.writeText(this.verificationLink(credential));
  }

  openFromIntegrity(credentialId: string): void {
    this.showIntegrityDetails.set(false);
    this.drawerOpen.set(true);
    this.openCredential(credentialId, 'audit');
  }

  // Everything support needs, as plain text for email or chat.
  copyIntegrityReport(): void {
    const r = this.integrityResult();
    const p = r?.problem;
    if (!r || !p) return;
    const tenant = this.auth.currentUser()?.tenant_slug ?? '';
    const report = [
      'Certyfi ledger integrity report',
      `Institution: ${tenant}`,
      `Checked: ${r.checkedAt} (${r.totalEntries} entries)`,
      `Result: ${r.reason}`,
      `Problem type: ${p.kind}`,
      `Audit entry: #${p.sequenceNumber} ${p.eventType} at ${p.occurredAt} by ${p.actorName ?? 'unknown'}`,
      `Certificate: ${p.achievementTitle ?? '?'} for ${p.recipientName ?? '?'} (code ${p.certificateCode ?? '?'}, id ${p.credentialId})`,
      `Stored hash: ${p.storedEntryHash}`,
      `Recalculated hash: ${p.recomputedEntryHash}`,
      `Previous hash expected: ${p.expectedPreviousHash}`,
      `Previous hash stored: ${p.storedPreviousHash}`,
    ].join('\n');
    navigator.clipboard.writeText(report).then(() => this.integrityReportCopied.set(true));
  }

  checkLedgerIntegrity(): void {
    this.integrityReportCopied.set(false);
    this.checkingIntegrity.set(true);
    this.integrityResult.set(null);
    this.ledgerService.checkIntegrity().subscribe({
      next: (result) => {
        this.integrityResult.set(result);
        this.checkingIntegrity.set(false);
      },
      error: () => {
        this.checkingIntegrity.set(false);
      },
    });
  }

  private resetForm(): void {
    this.templateId = '';
    this.title = '';
    this.recipientName = '';
    this.recipientEmail = '';
    this.fieldValues = {};
  }
}
