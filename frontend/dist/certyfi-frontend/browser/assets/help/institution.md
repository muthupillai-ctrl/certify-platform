# Institution guide

For staff at an institution that issues certificates with Certyfi:
administrators, registrars and approvers. Each section is one task.

## Getting started

### Sign in

1. Open Certyfi and choose **Institution login**.
2. Enter your **institution code** (your administrator gives it to you), your
   email and your password.
3. The first time, Certyfi asks you to choose a new password.

Forgot your password? Ask your institution's administrator to reset it (see
[Add staff and reset passwords](#add-staff-and-reset-passwords)).

![The institution sign-in page](/assets/help/img/login.png)

### Who can do what

Every staff member has a role. A request always needs a second person to
approve it: **you can't approve a certificate you asked for yourself**.

| Role | Can |
|---|---|
| **Institution admin** | Settings, staff, templates, signatories, API keys, banners, reports, and the **final approval** of certificates whose template needs it. Admins don't request or first-approve certificates |
| **Registrar** | Request certificates, import paper certificates, manage students |
| **Approver** | Approve or reject requests, suspend and revoke, approve revocation requests |
| **Learner** | See their own certificates |

Every institution has **two institution admins**, set up by Ahamsys when the
institution joins. Only Ahamsys can add, change or reset an admin, and
there are always at least two. Safeguards are only switched off when both
admins agree (see [Approvals and audit](#approvals-and-audit)).

### Find your way around

The menu on the left opens each part of Certyfi. The **Dashboard** is the
starting point: what needs your attention (requests waiting for approval,
certificates awaiting payment, imports waiting, revocation requests), how
many certificates were issued and how often they were checked. Every number
opens the matching list.

![The dashboard](/assets/help/img/dashboard.png)

## Setting up your institution

Do these once, before the first certificate.

### Add your profile and logo

Open **Settings**.

- **Institution profile**: name, registration number, accreditation,
  address, website. Shown to anyone who verifies one of your certificates.
- **Institution branding**: your logo and main colour, used on certificates
  and the student portal.
- **Student payments**: how many days a student has to pay before an unpaid
  certificate is cancelled (default 14; 0 means no deadline).

### Add staff and reset passwords

Open **Users** (institution admins only).

1. Choose **New user**, enter their name, email and a first password, and
   pick their role (Registrar, Approver or Learner). They choose their own
   password when they first sign in.
2. To reset someone's password, choose **Reset password** on their row.
3. Someone leaving? **Deactivate** them: they can't sign in, and everything
   they did stays on record. **Reactivate** brings them back.

Admin accounts show "managed by Ahamsys": to add or replace an admin, or
reset an admin's password, contact Ahamsys.

### Add signatories

Every certificate names who signs it, for example "Dr. A. Kumar, Principal".
Certyfi prints the name and designation (no signature images).

1. Open **Signatories** and choose **Add signatory**.
2. Enter the name and designation and save.

Changing a signatory later only affects certificates made from then on;
certificates already made keep the names they were made with. A signatory
used by a template can't be deleted until the template uses someone else.

### Design a certificate template

A template is the design of a certificate: its look, the details it shows
(the fields) and who signs it. Open **Templates** and choose **New
template**.

1. **Pick a starting design** from the gallery (Classic, Elegant, Modern,
   Minimal, Training, Award, Participation, Academic transcript), or a blank
   canvas. Everything stays editable.
2. Give it a **Name** (only staff see it) and check the **Title** printed on
   the certificate.
3. Under **Signatories**, choose one to three people, in printing order.
4. Under **Fields**, add the details each certificate carries: roll number,
   grade, course, or a table of subjects for a marks card. Tick
   **Required** for anything that must always be filled in.
5. Arrange the design on the canvas (below), then **Create template**.

![Choosing a starting design](/assets/help/img/gallery.png)

Every certificate must show the institution (name or logo), the recipient's
name, the title, the issue date, the signatories and its QR code or
certificate code. If the design is missing any of them, the editor lists
them with **+ Add** buttons and won't save until they're there.

### Edit the design

![The template editor](/assets/help/img/editor.png)

- **Move** anything by dragging; it snaps to the centre and to other
  elements. With an element selected, the arrow keys nudge it (Shift: 10
  pixels) and Delete removes it.
- **Change an element**: click it and use the panel on the right: size,
  font, size of text, colour, alignment, bold, italic, capitals, letter
  spacing, line height.
- **Add your own** text ("This is to certify that"), a line, or an image
  such as a seal or stamp, with **+ Text**, **+ Line** and **+ Image**.
- **Frame and background**: choose a frame (single or double line, classic,
  ornate corners, corner brackets, ribbons, side band), its colour,
  thickness and margin, and the background colour. Or upload a complete
  background image designed elsewhere.
- **Layers**: Front, Up, Down and Back decide what's drawn on top.
  **Lock** stops an element moving; **Duplicate** copies it.
- **Undo and redo** with the buttons or Ctrl/⌘ Z and Ctrl/⌘ Shift Z.
- **Zoom** with Fit, 100% and + / −.
- **Change design** applies another gallery design; your fields and
  signatories stay.

Changes to a template apply to certificates made from then on. Certificates
already issued never change.

### Achievements

An achievement is what a certificate is awarded for, such as "Fire Safety
Level 1". Certyfi creates one automatically the first time you issue a
certificate with a new title. Open **Achievements** to give one a fuller
description, criteria or a validity period (for certificates that expire).

## Issuing certificates

### Request a certificate

1. Open **Certificates** and choose **New certificate**.
2. Enter the **Recipient name** and **Recipient email**.
3. Pick the **Certificate template** and type the **Title** (what the
   certificate is for, e.g. "Advanced Data Analytics").
4. Fill in the template's fields. Required ones are marked with a red
   asterisk. Under the template you can also see who signs it.
5. **Preview** shows the certificate as it will look; then choose **Draft
   certificate**.

The certificate now waits for approval. If the template has no signatory
yet, Certyfi says so and an admin must choose one on the template first.

![The New certificate form](/assets/help/img/new-certificate.png)

### Approve or reject

1. Open **Certificates** and choose the quick view **Needs my approval**
   (the dashboard shows how many are waiting).
2. Open a request, check the details and the preview, then choose
   **Approve** or **Reject**. Rejecting asks for a reason.
3. Several at once: tick them and use **Bulk actions**.

### Final approval

For important certificates, tick **Needs final approval by an institution
admin** in the template's **Approval** section. Its certificates then need
three people:

1. A registrar requests it.
2. An approver approves it. It now shows **Final approval** and isn't
   issued yet.
3. An institution admin opens it under **Needs my approval** and chooses
   **Final approval**. Only now is it issued.

The admin can't be the person who requested or first approved it. The
admin can also reject it, with a reason. This applies to certificates from
partner apps (even on keys that issue without approval) and to paper
imports too.

Switching final approval **on** is immediate. Switching it **off** waits
until the other admin confirms on **Approvals & audit**.

An approved certificate is signed with your institution's own key, recorded
in a tamper-evident audit trail, and later timestamped on Bitcoin. From then
on anyone can verify it.

### Find a certificate

On **Certificates**:

- **Quick views** across the top: Needs my approval, My requests,
  Recently issued, All.
- **Search** by name, email, title or certificate code.
- **Filters**: status, template, and under **More filters** the issue
  dates and the source (staff, import or a partner app, and which API key).
- **Export CSV** downloads the list you're looking at.

Opening a certificate shows its details, a preview and its full history:
who requested, approved, suspended or revoked it, and when.

### Share an issued certificate

Each issued certificate has a public page and a QR code that lead to it.
From the certificate, copy its verification link, or download the PDF.
The certificate code (like `K7QM-3XPD-9TWA`) printed on it can also be
typed into Certyfi's verify page.

Certificates requested by a partner app (such as your LMS) are emailed to
the student automatically. For certificates staff request, send the link
yourself, or invite the student to the student portal from **Students**.

## Paper certificates

Bring certificates you issued on paper into Certyfi, so they can be checked
online too.

1. Open **Import paper** and choose **New import**.
2. **Download a sample with the right columns**, fill it in (Excel or
   CSV), and upload it.
3. Pick the **Certificate template for the digital versions** and check
   how the columns were read.
4. Certyfi checks every row. **Rows with problems** can be downloaded,
   fixed and uploaded again.
5. An approver then approves the import, typing the number of certificates
   to confirm.
6. If the template needs final approval, an institution admin then gives
   it for the whole import, again typing the number.

Imported certificates keep their original issue date and paper number, and
show "Originally issued on paper" when verified.

## After a certificate is issued

### Suspend, reactivate or revoke

Open **Revoke & suspend** and find the certificate by its code, the
recipient's name or email.

- **Suspend**: temporarily not valid (for example while a complaint is
  looked into). Anyone verifying it sees it's suspended. **Reactivate**
  undoes it.
- **Revoke**: permanently withdrawn. This can't be undone. It needs a
  second person: one asks, an approver approves under **Revocation
  requests waiting for approval**.

Every step asks for a reason and is kept in the certificate's history.

### Fix a mistake on an issued certificate

An issued certificate can't be edited: that's what makes it trustworthy.
Revoke it (reason: "Issued with an error") and issue a corrected one.

### Erase someone's personal data

When a person asks for their data to be removed, open their certificate and
choose **Erase personal data**, typing its certificate code to confirm.
Their name and email are removed for good; the certificate no longer
verifies, and its history records that it was erased.

## Students and the student portal

Certyfi keeps one student record per email address. Students sign in to the
student portal to see all their certificates from your institution, and to
pay when payment is required.

### Find and edit students

Open **Students** to search them and see each one's certificates. **Edit
details** changes their name, email (their sign-in), phone or student ID;
if the email changes, the old address is told. Certificates already issued
keep the details they were issued with.

### Invite students to the portal

- One student: open them and email their set-up link.
- Everyone not signed up yet: **Email set-up links**, check the preview,
  then send. Students invited in the last 7 days are skipped.

A student who forgets their password uses "Forgot your password?" on the
sign-in page.

### Payments

Certificates requested through an API key with **Students pay before
issue** start as **Awaiting payment**. Certyfi emails the student ("Action
needed: pay for your certificate …"), reminds them 3 days before the
deadline, and cancels the request if it isn't paid in time (see
[Add your profile and logo](#add-your-profile-and-logo) for the number of
days). Payment is a test step for now: no money is taken.

### Banners in the student portal

Open **Portal banners** to show your own images beside the student portal:
admissions, courses, events. Upload a tall image (about 480 × 960 pixels),
add a link if you like, choose left or right, and set the dates it runs.
Each banner shows how many times it was clicked.

## Connecting your LMS or other systems

Partner apps such as your learning platform can request certificates
automatically.

1. Open **API keys** and create a key for the app. Choose its
   permissions, and whether its certificates are issued without approval
   and whether students pay first. A key that issues without approval only
   starts doing so once the other admin confirms it on **Approvals &
   audit**.
2. The key is shown **once**: copy it straight to the app's administrator.
3. Optionally add a **webhook** so the app hears about changes at once.
4. Send the app's developers the **Developer guide** (linked from the API
   keys page).

Each key shows when it was last used and its recent calls. Certificates it
requested can be listed on **Certificates** under **More filters** →
**Source** → API.

## Approvals and audit

**Approvals & audit** (institution admins) lists what is waiting for the
second admin: switching off a template's final approval, or letting an API
key issue without approval. The other admin chooses **Confirm** or
**Refuse** (with a reason); whoever asked can **Withdraw** it.

Below is the **audit log**: who added staff or changed their roles,
deactivated or reset someone, created or revoked API keys, and changed
approval rules, with when and why. It can't be edited or deleted.
**Download CSV** saves a copy.

## Reports and verification activity

- **Reports**: certificates issued, or every status change, over a period
  you choose; print them or download a CSV.
- **Verification**: who checked your certificates and how (QR code, code
  look-up, file); you can also check a certificate yourself.

## Questions and problems

**The template won't save: "The design must show…"**
Use the **+ Add** buttons above the canvas for each missing item, then
drag them into place.

**A request is refused: "the template has no signatory"**
An institution admin opens the template, chooses a signatory under
**Signatories** and saves.

**"'Roll Number' is required"**
The template marks that field as required. Fill it in, or untick
**Required** on the template.

**A student says they didn't get Certyfi's email**
Ask them to check their spam folder. You can send their link again from
**Students**.

**I can't approve a request**
You asked for it yourself, or your role can't approve. Another approver or
administrator needs to.
