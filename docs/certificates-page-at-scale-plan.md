# Certificates page at scale: plan

**Status:** agreed 2026-09-29; building one phase at a time.
**Goal:** keep the Certificates page fast and useful when an institution has
hundreds or thousands of certificates, by designing around what each role
comes to the page to do.

| Who | Comes to | Needs first |
|---|---|---|
| Approver | clear the queue | pending approvals, oldest first; approve several at once |
| Registrar | check their requests | *my* requests; rejected ones with the reason |
| Front desk / support | answer "is this certificate real / can you resend it?" | find one certificate in seconds by name, email, code or roll number |
| Institution Admin | oversight and records | exceptions (suspended, revoked, expiring), totals, export |

## Current state (why this is needed)

- `GET /api/v1/credentials` returns **every** certificate, with template
  layout and field data, and the browser filters them. Slow at a few
  thousand; unworkable beyond.
- **Privacy gap:** that endpoint is open to the Learner role, so a learner
  can list every recipient's name and email. Fixed in phase 1.
- The list is a card column beside a permanent preview; filters were just
  compacted to one line (status, achievement, requester, header search).
- No bulk actions, no export, no date filters; the Dashboard is a stub.

---

## Phase 1: Server-side search and paging (point 1)

**Status:** built and tested locally 2026-09-29 (12 tests in
`CredentialListTests`, including 5,000 certificates); deployed 2026-09-29.

**Backend**
- `GET /api/v1/credentials` gains query parameters: `q`, `status`,
  `achievementId`, `requestedBy`, `sort` (`created`, `issued`, `recipient`,
  `expires`; `-` prefix for descending), `page`, `pageSize` (default 25,
  max 100). Returns `{ items, total, page, pageSize, statusCounts }`, where
  `statusCounts` counts by status with every filter except status applied
  (what the Status dropdown shows today, computed server-side).
- List items become a **summary** (id, recipient name/email, achievement,
  status, code, created/issued/expires, requester, source); the full record
  (layout, fields, signature) stays on `GET /credentials/{id}`, loaded when a
  row is opened.
- `q` matches recipient name and email, achievement title, requester name,
  certificate code (dashes/case ignored) and id, **and custom field values**
  (e.g. roll number) — case-insensitive `ILIKE` over the stored field JSON.
- Indexes: `pg_trgm` GIN indexes on recipient name, email and field values
  so `ILIKE '%…%'` stays fast; btree on `CreatedAt`, `IssuedAt`, `ExpiresOn`.
  (Tenant migration; `pg_trgm` ships with the Postgres images in use.)
- **Learners see only their own certificates** (matched on the signed-in
  user's email) — fixes the privacy gap.

**Frontend**
- The page asks the server for one page at a time; the filters and header
  search send parameters (search debounced ~300 ms).
- "Showing 26–50 of 1,240" with previous/next.

**Tests:** paging and totals; every filter and sort; search by name, email,
code with/without dashes, custom field value; status counts ignore the
status filter; Learner sees only own; performance smoke test with ~5,000
seeded certificates (response under ~300 ms).

**Size:** medium. **Done when** the page loads only one page of summaries and
all current filters behave the same as today.

## Phase 2: Role-based default view and a "waiting" badge (point 2)

**Status:** built and tested locally 2026-09-29; deployed 2026-09-29. Views are
tabs in place of the page subtitle; approvers land on their queue only when
something is waiting (otherwise Recently issued). "Recently issued" sorts
not-yet-issued certificates last.

- Landing view per role, as a preset of the phase-1 filters:
  - Approver (and admins who approve): **Pending approval**, oldest first.
  - Registrar: **My requests**, newest first (rejected ones flagged).
  - Learner: **My certificates**.
  - Others: latest issued.
- Quick views above the table: *Needs my approval*, *My requests*,
  *Recently issued*, *All*. Choosing any filter leaves the preset.
- Sidebar badge on Certificates: number pending approval that the current
  user is allowed to approve (excludes their own requests — maker–checker).
  New endpoint `GET /api/v1/credentials/summary` → `{ pendingForMe, … }`.

**Size:** small–medium. **Done when** an approver logs in and lands on their
queue with the count in the sidebar.

## Phase 3: Table layout with preview on demand (point 3)

**Status:** built 2026-09-29 (frontend only; checked in headless Chrome with
phases 4–7); deployed 2026-09-29. The row checkbox is left for phase 4,
where it's first used.

- Replace the card list + permanent preview with a full-width **table**:
  checkbox, recipient (name + email), achievement, status, issued, expires,
  code, requested by, source (staff / API key name).
- Click a column header to sort (server-side); comfortable/compact density.
- Clicking a row opens the certificate in a **side drawer** (the current
  detail panel: overview, audit trail, verification, actions), closed with
  Esc; previous/next row from inside the drawer.
- Empty and "no results" states with Clear filters.

**Size:** medium (mostly frontend; reuses the detail panel).
**Done when** 100+ rows are easy to scan and a certificate opens in one click.

## Phase 4: Bulk actions and export (point 4)

**Status:** built and tested locally 2026-09-29 (7 new backend tests; full
suite 138 passing; frontend builds); deployed 2026-09-29. No migration. Bulk
actions go through the same service methods as single actions, so the
maker–checker and status rules apply to each item; failures are skipped
and listed with a reason. The CSV export neutralises spreadsheet formulas.

- Select rows (or "all N matching the filters") →
  - **Approve** (skips any you requested yourself, and says how many),
  - **Reject / Suspend / Revoke** with one reason for all,
  - **Export CSV** (current filters, all pages): recipient, email,
    achievement, status, code, dates, requester, source, custom fields,
    verification link.
- Backend: `POST /api/v1/credentials/bulk/{action}` with ids or the filter;
  per-item result (done / skipped + why). Each item is its own ledger entry
  and signature exactly as today. `GET /api/v1/credentials/export.csv`
  streams the file.
- Limit per request (e.g. 500) to keep signing and ledger appends bounded.
- Later, not in this phase: a ZIP of PDFs (needs server-side PDF rendering).

**Size:** medium. **Done when** an approver approves a cohort of 50 in one go
and an admin exports the term's certificates.

## Phase 5: More filters (point 5)

**Status:** built and tested locally 2026-09-29 (3 new backend tests; full
suite 141 passing; frontend builds); deployed 2026-09-29. No migration. Two
changes from the outline: the date range is sent as `issuedFrom` /
`issuedBefore` instants (the browser's local midnights, so "this month" is
the user's month, not UTC's); and the quick picks are this month, last 30
days, this year, last year and a custom range — "this academic year" needs
a per-institution start month, left for later. Choosing "expiring within"
sorts soonest first.

- **Issued between** (date range) and quick picks (this month, last 30
  days, this academic year).
- **Expiring within** 30 / 60 / 90 days (active certificates only).
- **Source:** staff vs. Partner API, and which API key.
- Backend: `issuedFrom`, `issuedTo`, `expiringWithinDays`, `source`,
  `apiClientId` on the phase-1 endpoint. "Source" comes from the requester
  being an API service account (no new column).
- To keep the filter row on one line, date and source live in a
  **More filters** popover with a count badge ("More filters · 2").

**Size:** small. **Done when** "active certificates expiring in 30 days" is
two clicks.

## Phase 6: Shareable filters in the URL (point 6)

**Status:** built locally 2026-09-29 (frontend only; builds; checked in
headless Chrome: reload, Back/Forward, shared link, open certificate,
invalid values, sidebar link); deployed 2026-09-29. Parameters: `view`,
`status`, `achievement`, `requestedBy`, `mine`, `others`, `q`, `issued`,
`from`, `to`, `expiring`, `source`, `apiKey`, `sort`, `page`, `open`;
defaults are left out. A URL with no parameters opens the role's default
view. Typing a search or opening/stepping through certificates replaces
the history entry; other changes add one, so Back undoes a filter change.
Values that don't parse are dropped and the URL tidied.

- Every filter, search, sort, page and open certificate is reflected in the
  URL (`/admin/certificates?status=PendingApproval&achievement=…&q=…`), so
  views can be bookmarked, shared with a colleague, and survive refresh and
  Back.
- The URL is the source of truth; phase-2 presets are just links.

**Size:** small. **Done when** copying the address bar reproduces the view.

## Phase 7: Dashboard (point 7)

**Status:** built and tested locally 2026-09-29 (3 new backend tests; full
suite passing; checked in headless Chrome: numbers match the lists they
link to); deployed 2026-09-29. No migration. Staff roles only (learners are
sent to their certificates). Months are the browser's calendar months: the
browser sends its IANA zone and .NET works out each month's first instant,
so legacy names like `Asia/Calcutta` work whatever tz data Postgres has.
"Rejected" counts the last 30 days (there is no resubmission link to
track). Anchoring problems = failed checkpoints or pending over a day;
shown as a notice to contact support (the anchoring page is SuperAdmin's).
Cached a minute per tenant and zone; the page refreshes every minute while
visible.

Replaces the "coming soon" Dashboard page.

- **Needs attention:** pending approvals (oldest age), rejected awaiting
  resubmission, expiring in 30 days, anchoring problems (if any) — each a
  link into a filtered certificates view (phase 6 URLs).
- **Activity:** certificates issued per month (last 12), by achievement
  (top 10), by source (staff vs. each API key).
- **Verification:** verifications per month and by channel (page, code
  look-up, credential download, API), most-verified certificates.
- Backend: `GET /api/v1/dashboard` computing aggregates in SQL (grouped
  counts; no per-certificate data), cached ~1 minute.
- Charts drawn as simple accessible SVG/HTML (no heavy chart library).

**Size:** medium. **Done when** an admin sees the institution's state and
trends at a glance and can jump to the underlying list.

---

## Order and dependencies

1 → 2 → 3 → 4 → 5 → 6 → 7. Phase 1 is the foundation (every later phase
uses its filters and endpoint); 3 before 4 because bulk selection lives in
the table; 6 is cheap once 1–5 settle; 7 links into 6's URLs.

Each phase ships on its own: tests pass, verified locally, then deployed.

## Decisions (defaults; change any before its phase starts)

- Page size **25**, options 25 / 50 / 100.
- Learners: see **only certificates issued to their own email**.
- Bulk limit **500** per action; CSV export up to **10,000** rows.
- Search needs **2+ characters**; debounced **300 ms**.
- Dashboard numbers **refresh every minute**.
