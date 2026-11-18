// Periods for the Verification and Reports pages, as the browser's local
// midnights (the API takes [from, before) instants), so "this month" is the
// user's month, not UTC's.
export type PeriodPreset = 'thisMonth' | 'lastMonth' | 'last30' | 'thisYear' | 'lastYear' | 'all' | 'custom';

export const PERIOD_LABELS: Record<PeriodPreset, string> = {
  thisMonth: 'This month',
  lastMonth: 'Last month',
  last30: 'Last 30 days',
  thisYear: 'This year',
  lastYear: 'Last year',
  all: 'All time',
  custom: 'Custom…',
};

export interface Period {
  from: string | null;
  before: string | null;
}

// fromDate / toDate: yyyy-mm-dd, both inclusive (custom only).
export function periodOf(preset: PeriodPreset, fromDate = '', toDate = ''): Period {
  const now = new Date();
  const y = now.getFullYear();
  const m = now.getMonth();
  const at = (yy: number, mm: number, dd: number) => new Date(yy, mm, dd).toISOString();
  const parse = (value: string, plusDays = 0) => {
    const [yy, mm, dd] = value.split('-').map(Number);
    return yy && mm && dd ? at(yy, mm - 1, dd + plusDays) : null;
  };
  switch (preset) {
    case 'thisMonth': return { from: at(y, m, 1), before: at(y, m + 1, 1) };
    case 'lastMonth': return { from: at(y, m - 1, 1), before: at(y, m, 1) };
    case 'last30': return { from: at(y, m, now.getDate() - 29), before: at(y, m, now.getDate() + 1) };
    case 'thisYear': return { from: at(y, 0, 1), before: at(y + 1, 0, 1) };
    case 'lastYear': return { from: at(y - 1, 0, 1), before: at(y, 0, 1) };
    case 'custom': return { from: parse(fromDate), before: parse(toDate, 1) };
    default: return { from: null, before: null };
  }
}

// "1 Sep 2026 – 30 Sep 2026" for headings and print-outs.
export function describePeriod(period: Period): string {
  if (!period.from && !period.before) return 'All time';
  const fmt = (d: Date) => d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
  const from = period.from ? fmt(new Date(period.from)) : 'the start';
  const to = period.before ? fmt(new Date(new Date(period.before).getTime() - 1)) : 'now';
  return `${from} – ${to}`;
}
