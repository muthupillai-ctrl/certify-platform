import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subject, debounceTime } from 'rxjs';
import { StudentsService } from '../../../core/services/students.service';
import { StudentDetail, StudentInvitationPreview, StudentInvitationResult, StudentListItem, StudentSignInLink } from '../../../core/models/student.models';
import { AuthService } from '../../../core/services/auth.service';
import { ModalComponent } from '../../../shared/modal/modal.component';

// The institution's students: everyone with a certificate (matched by
// email) plus students a partner app sent. Staff can open one to see their
// certificates and get the link to give them for the student portal.
@Component({
  selector: 'app-students',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ModalComponent],
  templateUrl: './students.component.html',
})
export class StudentsComponent implements OnInit {
  readonly pageSize = 25;

  readonly items = signal<StudentListItem[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  q = '';
  private readonly search$ = new Subject<void>();

  readonly selected = signal<StudentDetail | null>(null);
  readonly link = signal<StudentSignInLink | null>(null);
  readonly linkBusy = signal(false);
  readonly linkError = signal<string | null>(null);
  readonly confirmingReset = signal(false);
  readonly copied = signal(false);
  readonly emailBusy = signal(false);
  readonly emailNote = signal<string | null>(null);

  // Edit form.
  readonly editing = signal(false);
  readonly saving = signal(false);
  readonly editError = signal<string | null>(null);
  readonly savedNote = signal<string | null>(null);
  readonly showHistory = signal(false);
  editName = '';
  editEmail = '';
  editPhone = '';
  editNumber = '';

  // Bulk invitation window.
  readonly inviteOpen = signal(false);
  readonly invitePreview = signal<StudentInvitationPreview | null>(null);
  readonly inviteResult = signal<StudentInvitationResult | null>(null);
  readonly inviteBusy = signal(false);
  readonly inviteError = signal<string | null>(null);

  constructor(private students: StudentsService, readonly auth: AuthService) {}

  openInvite(): void {
    this.inviteOpen.set(true);
    this.invitePreview.set(null);
    this.inviteResult.set(null);
    this.inviteError.set(null);
    this.students.invitationPreview().subscribe({
      next: (p) => this.invitePreview.set(p),
      error: (err) => this.inviteError.set(err.error?.error ?? 'Could not count the students.'),
    });
  }

  sendInvites(): void {
    this.inviteBusy.set(true);
    this.inviteError.set(null);
    this.students.inviteAll().subscribe({
      next: (r) => {
        this.inviteBusy.set(false);
        this.inviteResult.set(r);
        this.load();
      },
      error: (err) => {
        this.inviteBusy.set(false);
        this.inviteError.set(err.error?.error ?? 'Could not send the invitations.');
      },
    });
  }

  ngOnInit(): void {
    this.search$.pipe(debounceTime(300)).subscribe(() => {
      this.page.set(1);
      this.load();
    });
    this.load();
  }

  onSearch(): void {
    this.search$.next();
  }

  goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  lastPage(): number {
    return Math.max(1, Math.ceil(this.total() / this.pageSize));
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.students.list(this.q, this.page(), this.pageSize).subscribe({
      next: (list) => {
        this.items.set(list.items);
        this.total.set(list.total);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err.error?.error ?? 'Could not load students.');
        this.loading.set(false);
      },
    });
  }

  open(item: StudentListItem): void {
    this.editing.set(false);
    this.savedNote.set(null);
    this.showHistory.set(false);
    this.emailNote.set(null);
    this.link.set(null);
    this.linkError.set(null);
    this.confirmingReset.set(false);
    this.students.get(item.id).subscribe({
      next: (detail) => this.selected.set(detail),
      error: (err) => this.error.set(err.error?.error ?? 'Could not open the student.'),
    });
  }

  close(): void {
    this.selected.set(null);
  }

  startEdit(): void {
    const d = this.selected();
    if (!d) return;
    this.editName = d.student.fullName;
    this.editEmail = d.student.email;
    this.editPhone = d.phone ?? '';
    this.editNumber = d.student.studentNumber ?? '';
    this.editError.set(null);
    this.savedNote.set(null);
    this.link.set(null);
    this.editing.set(true);
  }

  emailChanging(): boolean {
    const d = this.selected();
    return !!d && this.editEmail.trim().toLowerCase() !== d.student.email;
  }

  saveEdit(): void {
    const d = this.selected();
    if (!d) return;
    const emailChanged = this.emailChanging();
    this.saving.set(true);
    this.editError.set(null);
    this.students
      .update(d.student.id, {
        fullName: this.editName,
        email: this.editEmail,
        phone: this.editPhone.trim() || null,
        studentNumber: this.editNumber.trim() || null,
      })
      .subscribe({
        next: (updated) => {
          this.selected.set(updated);
          this.saving.set(false);
          this.editing.set(false);
          this.savedNote.set(emailChanged
            ? `Saved. ${updated.student.fullName} now signs in with ${updated.student.email}; both the old and new addresses were emailed.`
            : 'Saved.');
          this.load();
        },
        error: (err) => {
          this.saving.set(false);
          this.editError.set(err.error?.error ?? 'Could not save.');
        },
      });
  }

  // reset: forget their password (they forgot it) and stop older links working.
  getLink(reset: boolean): void {
    const s = this.selected();
    if (!s) return;
    this.linkBusy.set(true);
    this.linkError.set(null);
    this.copied.set(false);
    this.students.signInLink(s.student.id, reset).subscribe({
      next: (link) => {
        this.link.set(link);
        this.linkBusy.set(false);
        this.confirmingReset.set(false);
        if (reset) {
          this.selected.set({ ...s, student: { ...s.student, signInSetUp: false } });
          this.load();
        }
      },
      error: (err) => {
        this.linkError.set(err.error?.error ?? 'Could not get a link.');
        this.linkBusy.set(false);
      },
    });
  }

  emailLink(): void {
    const s = this.selected();
    if (!s) return;
    this.emailBusy.set(true);
    this.emailNote.set(null);
    this.linkError.set(null);
    this.students.emailLink(s.student.id).subscribe({
      next: (r) => {
        this.emailBusy.set(false);
        this.emailNote.set(r.queued
          ? `Email sent to ${s.student.email}.`
          : "Couldn't send: the server's public address isn't set up for email links.");
      },
      error: (err) => {
        this.emailBusy.set(false);
        this.linkError.set(err.error?.error ?? 'Could not send the email.');
      },
    });
  }

  copyLink(): void {
    const url = this.link()?.url;
    if (!url) return;
    navigator.clipboard?.writeText(url).then(() => this.copied.set(true));
  }

  statusLabel(status: string): string {
    return status === 'PendingApproval' ? 'Pending approval' : status === 'AwaitingPayment' ? 'Awaiting payment' : status;
  }

  statusClasses(status: string): string {
    switch (status) {
      case 'Active':
        return 'bg-green-100 text-green-800';
      case 'AwaitingPayment':
        return 'bg-sky-100 text-sky-800';
      case 'PendingApproval':
        return 'bg-amber-100 text-amber-800';
      case 'Suspended':
        return 'bg-orange-100 text-orange-800';
      case 'Revoked':
        return 'bg-red-100 text-red-800';
      default:
        return 'bg-gray-100 text-gray-600';
    }
  }
}
