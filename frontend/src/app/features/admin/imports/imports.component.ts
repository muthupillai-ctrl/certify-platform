import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../../core/services/auth.service';
import { CertificateActivityService } from '../../../core/services/certificate-activity.service';
import { CertificateTemplatesService } from '../../../core/services/certificate-templates.service';
import { ImportsService } from '../../../core/services/imports.service';
import { CertificateTemplate } from '../../../core/models/certificate-template.models';
import {
  DateFormat,
  ImportBatch,
  ImportBatchDetail,
  ImportCheckResult,
  ImportParseResult,
  ImportRow,
  ImportRowResult,
} from '../../../core/models/import.models';
import { CsvWriter } from './csv-writer';
import { blobErrorMessage, saveBlob } from '../../../core/download';

type Step = 'upload' | 'map' | 'check' | 'importing' | 'done';

// A spreadsheet column (index) or nothing for each thing a certificate needs.
interface Mapping {
  name: number | null;
  email: number | null;
  achievement: number | null;
  date: number | null;
  number: number | null;
  expires: number | null;
  fields: Record<string, number | null>;
}

const CHUNK = 250;

// Header words that suggest each column, for the automatic first guess.
const GUESSES: Record<'name' | 'email' | 'achievement' | 'date' | 'number' | 'expires', RegExp> = {
  name: /^(recipient|student|candidate|holder|awardee)?\s*(full\s*)?name$|^name of/i,
  email: /e-?mail/i,
  achievement: /achievement|course|programme|program|award|qualification|title/i,
  date: /(original|issue|award|certificate|passing)\s*(issue\s*)?date|^date( of issue)?$|issued on/i,
  number: /(paper|certificate|cert|serial|reg\w*)\s*(no|number|#)|^(no|number|serial)\.?$/i,
  expires: /expir|valid (until|till)/i,
};

// Digitising paper certificates: upload a spreadsheet, match its columns,
// check every row, import, and have a second person approve the batch.
@Component({
  selector: 'app-imports',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './imports.component.html',
})
export class ImportsComponent implements OnInit {
  // Only registrars start imports; admins come here for final approval.
  readonly canImport = computed(() => this.auth.hasRole('Registrar'));
  readonly isAdminOnly = computed(() => this.auth.hasRole('InstitutionAdmin') && !this.canImport());

  // ---------- Batches ----------
  readonly batches = signal<ImportBatch[]>([]);
  readonly loadingBatches = signal(true);
  readonly detail = signal<ImportBatchDetail | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);

  // Approve / reject a batch.
  readonly deciding = signal<'approve' | 'reject' | 'withdraw' | null>(null);
  confirmCount = '';
  rejectNote = '';
  readonly working = signal(false);
  readonly progress = signal<{ done: number; total: number } | null>(null);
  readonly decideError = signal<string | null>(null);

  // ---------- Wizard ----------
  readonly wizardOpen = signal(false);
  readonly step = signal<Step>('upload');
  readonly templates = signal<CertificateTemplate[]>([]);
  templateId = '';
  readonly parsing = signal(false);
  readonly sheet = signal<ImportParseResult | null>(null);
  mapping: Mapping = { name: null, email: null, achievement: null, date: null, number: null, expires: null, fields: {} };
  achievementMode: 'fixed' | 'column' = 'fixed';
  fixedTitle = '';
  dateFormat: DateFormat = 'DMY';
  skipDuplicates = true;
  readonly checking = signal(false);
  readonly check = signal<ImportCheckResult | null>(null);
  readonly importProgress = signal({ done: 0, total: 0 });
  readonly importResult = signal<{ imported: number; skipped: number; problems: ImportRowResult[]; batch: ImportBatch | null } | null>(null);
  readonly wizardError = signal<string | null>(null);

  readonly selectedTemplate = computed(() => this.templates().find((t) => t.id === this.templateIdSignal()) ?? null);
  private readonly templateIdSignal = signal('');
  readonly templateFields = computed(() => (this.selectedTemplate()?.fieldSchema ?? []).filter((f) => f.type !== 'table'));

  constructor(
    private auth: AuthService,
    private imports: ImportsService,
    private templatesService: CertificateTemplatesService,
    private activity: CertificateActivityService,
  ) {}

  ngOnInit(): void {
    this.loadBatches();
    if (this.canImport()) {
      this.templatesService.list().subscribe({ next: (list) => this.templates.set(list.filter((t) => t.isActive)) });
    }
  }

  loadBatches(): void {
    this.imports.list().subscribe({
      next: (list) => {
        this.batches.set(list);
        this.loadingBatches.set(false);
        const open = this.detail();
        if (open) this.openDetail(open.batch.id);
      },
      error: () => {
        this.loadingBatches.set(false);
        this.error.set('Could not load the imports.');
      },
    });
  }

  openDetail(id: string): void {
    this.imports.get(id).subscribe({ next: (d) => this.detail.set(d) });
  }

  statusLabel(status: string): string {
    return (
      {
        Importing: 'Uploading (not finished)',
        PendingApproval: 'Waiting for approval',
        Approving: 'Being approved…',
        PendingFinalApproval: 'Waiting for final approval',
        FinalApproving: 'Being given final approval…',
        Approved: 'Approved (issued)',
        Rejecting: 'Being rejected…',
        Rejected: 'Rejected',
        Withdrawn: 'Withdrawn',
      }[status] ?? status
    );
  }

  statusClasses(status: string): string {
    switch (status) {
      case 'PendingApproval':
        return 'bg-amber-100 text-amber-800';
      case 'PendingFinalApproval':
      case 'FinalApproving':
        return 'bg-violet-100 text-violet-800';
      case 'Approved':
        return 'bg-green-100 text-green-800';
      case 'Rejected':
      case 'Withdrawn':
        return 'bg-gray-100 text-gray-600';
      default:
        return 'bg-slate-100 text-slate-700';
    }
  }

  // ---------- Decide ----------

  startDecision(kind: 'approve' | 'reject' | 'withdraw'): void {
    this.deciding.set(kind);
    this.confirmCount = '';
    this.rejectNote = '';
    this.decideError.set(null);
    this.message.set(null);
  }

  // Approve or reject in chunks until nothing is left; resumable.
  async decide(): Promise<void> {
    const d = this.detail();
    const kind = this.deciding();
    if (!d || !kind) return;
    this.working.set(true);
    this.decideError.set(null);
    const total = d.batch.pending;
    this.progress.set({ done: 0, total });
    try {
      let first = true;
      let doneSoFar = 0;
      let lastStatus = '';
      for (;;) {
        const final = d.batch.status === 'PendingFinalApproval' || d.batch.status === 'FinalApproving';
        const p = kind === 'approve'
          ? await firstValueFrom(final
            ? this.imports.finalApprove(d.batch.id, first ? Number(this.confirmCount) : null)
            : this.imports.approve(d.batch.id, first ? Number(this.confirmCount) : null))
          : await firstValueFrom(this.imports.reject(d.batch.id, first ? this.rejectNote.trim() || null : null));
        first = false;
        doneSoFar += p.done;
        this.progress.set({ done: doneSoFar, total });
        lastStatus = p.status;
        if (p.remaining === 0) break;
        if (p.done === 0) throw { error: { error: `${p.failed} certificate(s) couldn't be processed. Try again later; the rest are done.` } };
      }
      this.deciding.set(null);
      this.message.set(
        kind === 'approve' && lastStatus === 'PendingFinalApproval'
          ? `Approved. An institution admin now gives final approval before the ${doneSoFar} certificates are issued.`
          : kind === 'approve'
          ? `${doneSoFar} certificates are now issued and verifiable.`
          : kind === 'reject'
            ? 'The import was rejected; none of its certificates were issued.'
            : 'You withdrew the import; none of its certificates were issued.',
      );
      this.activity.refresh().subscribe({ error: () => undefined });
    } catch (err: any) {
      this.decideError.set(err?.error?.error ?? 'That didn’t work. Please try again.');
    } finally {
      this.working.set(false);
      this.progress.set(null);
      this.loadBatches();
    }
  }

  // An import whose upload was interrupted: send what arrived for approval.
  finishInterrupted(batch: ImportBatch): void {
    this.imports.finish(batch.id).subscribe({
      next: () => {
        this.message.set('Sent for approval.');
        this.loadBatches();
      },
      error: (err) => this.error.set(err.error?.error ?? 'Could not finish the import.'),
    });
  }

  // ---------- Wizard ----------

  openWizard(): void {
    this.wizardOpen.set(true);
    this.step.set('upload');
    this.sheet.set(null);
    this.check.set(null);
    this.importResult.set(null);
    this.wizardError.set(null);
    this.detail.set(null);
    this.message.set(null);
  }

  closeWizard(): void {
    this.wizardOpen.set(false);
    this.loadBatches();
  }

  setTemplate(id: string): void {
    this.templateId = id;
    this.templateIdSignal.set(id);
    if (this.sheet()) this.guessMapping();
  }

  downloadSample(): void {
    if (!this.templateId) return;
    this.imports.sampleCsv(this.templateId).subscribe({
      next: (blob) => saveBlob(blob, `paper-certificates-${(this.selectedTemplate()?.name ?? 'template').toLowerCase().replace(/[^a-z0-9]+/g, '-')}.csv`),
      error: async (err) => this.wizardError.set(await blobErrorMessage(err, 'Could not download the sample.')),
    });
  }

  onFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    (event.target as HTMLInputElement).value = '';
    if (!file) return;
    this.wizardError.set(null);
    this.parsing.set(true);
    this.imports.parse(file).subscribe({
      next: (sheet) => {
        this.parsing.set(false);
        this.sheet.set(sheet);
        this.guessMapping();
        this.step.set('map');
      },
      error: (err) => {
        this.parsing.set(false);
        this.wizardError.set(err.error?.error ?? 'The file could not be read.');
      },
    });
  }

  private guessMapping(): void {
    const headers = this.sheet()?.headers ?? [];
    const find = (re: RegExp, taken: Set<number>) => {
      const i = headers.findIndex((h, idx) => !taken.has(idx) && re.test(h.trim()));
      if (i >= 0) taken.add(i);
      return i >= 0 ? i : null;
    };
    const taken = new Set<number>();
    const email = find(GUESSES.email, taken);
    const number = find(GUESSES.number, taken);
    const expires = find(GUESSES.expires, taken);
    const date = find(GUESSES.date, taken);
    const name = find(GUESSES.name, taken);
    const achievement = find(GUESSES.achievement, taken);
    const fields: Record<string, number | null> = {};
    for (const f of this.templateFields()) {
      const label = f.label.trim().toLowerCase();
      const i = headers.findIndex((h, idx) => !taken.has(idx) && (h.trim().toLowerCase() === label || h.trim().toLowerCase() === f.key.toLowerCase()));
      if (i >= 0) taken.add(i);
      fields[f.key] = i >= 0 ? i : null;
    }
    this.mapping = { name, email, achievement, date, number, expires, fields };
    this.achievementMode = achievement !== null ? 'column' : 'fixed';
  }

  mappingReady(): boolean {
    return !!this.templateId && this.mapping.name !== null && this.mapping.date !== null &&
      (this.achievementMode === 'column' ? this.mapping.achievement !== null : !!this.fixedTitle.trim());
  }

  // The file's rows as the API expects them, per the mapping.
  private rows(): ImportRow[] {
    const m = this.mapping;
    const cell = (row: string[], i: number | null) => (i === null ? null : (row[i] ?? '').trim() || null);
    return (this.sheet()?.rows ?? []).map((r) => ({
      recipientName: cell(r, m.name),
      recipientEmail: cell(r, m.email),
      achievementTitle: this.achievementMode === 'column' ? cell(r, m.achievement) : null,
      originalIssueDate: cell(r, m.date),
      legacyNumber: cell(r, m.number),
      expiresOn: cell(r, m.expires),
      fieldValues: Object.fromEntries(Object.entries(m.fields).map(([k, i]) => [k, cell(r, i)])),
    }));
  }

  readonly previewRows = computed(() => (this.sheet()?.rows ?? []).slice(0, 5));

  cellOf(row: string[], index: number | null): string {
    return index === null ? '' : row[index] ?? '';
  }

  runCheck(): void {
    if (!this.mappingReady()) return;
    this.checking.set(true);
    this.wizardError.set(null);
    this.imports.check(this.templateId, this.title(), this.dateFormat, this.rows()).subscribe({
      next: (result) => {
        this.checking.set(false);
        this.check.set(result);
        this.step.set('check');
      },
      error: (err) => {
        this.checking.set(false);
        this.wizardError.set(err.error?.error ?? 'The check could not be completed.');
      },
    });
  }

  private title(): string | null {
    return this.achievementMode === 'fixed' ? this.fixedTitle.trim() : null;
  }

  readonly problemRows = computed(() => (this.check()?.problems ?? []).filter((p) => p.errors.length > 0));
  readonly warningRows = computed(() => (this.check()?.problems ?? []).filter((p) => p.errors.length === 0 && p.warnings.length > 0));

  rowName(index: number): string {
    return this.cellOf(this.sheet()?.rows[index] ?? [], this.mapping.name) || '(no name)';
  }

  toImportCount(): number {
    const c = this.check();
    return c ? c.ready - (this.skipDuplicates ? c.likelyDuplicates : 0) : 0;
  }

  // The rows with problems, with the reasons, to fix in Excel and re-upload.
  downloadProblems(): void {
    const sheet = this.sheet();
    const problems = this.problemRows();
    if (!sheet || problems.length === 0) return;
    const csv = new CsvWriter();
    csv.row(['File row', 'Problems', ...sheet.headers]);
    for (const p of problems) csv.row([String(p.row + 2), p.errors.join(' '), ...(sheet.rows[p.row] ?? [])]);
    saveBlob(csv.blob(), `problem-rows-${sheet.fileName.replace(/\.[^.]+$/, '')}.csv`);
  }

  // Creates the batch, sends the rows in chunks, then sends it for approval.
  async runImport(): Promise<void> {
    const sheet = this.sheet();
    if (!sheet) return;
    const bad = new Set(this.problemRows().map((p) => p.row));
    const all = this.rows();
    const rows = all.map((row, index) => ({ row, index })).filter((r) => !bad.has(r.index));
    this.step.set('importing');
    this.importProgress.set({ done: 0, total: rows.length });
    this.wizardError.set(null);
    let imported = 0;
    let skipped = 0;
    const problems: ImportRowResult[] = [];
    try {
      const batch = await firstValueFrom(this.imports.create(this.templateId, sheet.fileName, rows.length));
      for (let i = 0; i < rows.length; i += CHUNK) {
        const chunk = rows.slice(i, i + CHUNK);
        // Rows are sent in file order with their own file index, so every
        // problem the server reports points at the right spreadsheet row.
        for (const run of contiguousRuns(chunk)) {
          const result = await firstValueFrom(
            this.imports.addRows(batch.id, this.title(), this.dateFormat, this.skipDuplicates, run.map((r) => r.row), run[0].index),
          );
          imported += result.imported;
          skipped += result.skipped;
          problems.push(...result.problems.filter((p) => p.errors.length > 0));
        }
        this.importProgress.set({ done: Math.min(i + CHUNK, rows.length), total: rows.length });
      }
      const finished = await firstValueFrom(this.imports.finish(batch.id));
      this.importResult.set({ imported, skipped, problems, batch: finished });
      this.step.set('done');
      this.activity.refresh().subscribe({ error: () => undefined });
    } catch (err: any) {
      this.wizardError.set(
        (err?.error?.error ?? 'The import stopped.') +
          ' What was imported so far is kept: open it from the list to send it for approval or withdraw it.',
      );
      this.importResult.set({ imported, skipped, problems, batch: null });
      this.step.set('done');
    }
  }
}

// Consecutive file rows grouped together (rows with problems are left out,
// so a chunk may have gaps).
function contiguousRuns<T extends { index: number }>(items: T[]): T[][] {
  const runs: T[][] = [];
  for (const item of items) {
    const last = runs[runs.length - 1];
    if (last && last[last.length - 1].index === item.index - 1) last.push(item);
    else runs.push([item]);
  }
  return runs;
}
