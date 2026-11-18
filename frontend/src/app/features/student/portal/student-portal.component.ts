import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { StudentSessionService } from '../../../core/services/student-session.service';
import { StudentCertificate } from '../../../core/models/student.models';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { StudentShellComponent } from '../../../shared/student-shell/student-shell.component';

// The signed-in student's certificates: pay (dummy) for any awaiting
// payment, follow approval, and open issued ones on the public verify page.
@Component({
  selector: 'app-student-portal',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ModalComponent, StudentShellComponent],
  templateUrl: './student-portal.component.html',
})
export class StudentPortalComponent implements OnInit {
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly certificates = signal<StudentCertificate[]>([]);

  readonly paying = signal<StudentCertificate | null>(null);
  readonly payBusy = signal(false);
  readonly payError = signal<string | null>(null);

  readonly changingPassword = signal(false);
  readonly passwordBusy = signal(false);
  readonly passwordError = signal<string | null>(null);
  readonly passwordChanged = signal(false);
  currentPassword = '';
  newPassword = '';

  slug = '';

  // Search: one box over title, code and every field; status and year.
  readonly query = signal('');
  readonly statusFilter = signal<'all' | 'issued' | 'waiting' | 'other'>('all');
  readonly yearFilter = signal<string>('all');

  readonly years = computed(() =>
    [...new Set(this.certificates().map((c) => this.yearOf(c)))].sort().reverse());

  readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    const status = this.statusFilter();
    const year = this.yearFilter();
    return this.certificates().filter((c) => {
      if (year !== 'all' && this.yearOf(c) !== year) return false;
      if (status === 'issued' && !['Active', 'Expired'].includes(c.status)) return false;
      if (status === 'waiting' && !['AwaitingPayment', 'PendingApproval'].includes(c.status)) return false;
      if (status === 'other' && ['Active', 'Expired', 'AwaitingPayment', 'PendingApproval'].includes(c.status)) return false;
      if (!q) return true;
      const code = (c.verificationCode ?? '').toLowerCase();
      const haystack = [c.title, code, code.replace(/-/g, ''), ...(c.fields ?? []).flatMap((f) => [f.label, f.value])].join(' \n ').toLowerCase();
      return q.split(/\s+/).every((word) => haystack.includes(word));
    });
  });

  // A field value matches the search (to highlight it on the card).
  matches(value: string): boolean {
    const q = this.query().trim().toLowerCase();
    return !!q && q.split(/\s+/).some((word) => value.toLowerCase().includes(word));
  }

  clearSearch(): void {
    this.query.set('');
    this.statusFilter.set('all');
    this.yearFilter.set('all');
  }

  yearOf(c: StudentCertificate): string {
    return (c.issuedAt ?? c.createdAt).slice(0, 4);
  }

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    readonly session: StudentSessionService,
  ) {}

  ngOnInit(): void {
    this.slug = this.route.snapshot.paramMap.get('tenantSlug') ?? '';
    if (!this.session.isSignedIn(this.slug)) {
      this.router.navigate(['/student', this.slug, 'login']);
      return;
    }
    this.load();
  }

  load(): void {
    this.session.certificates().subscribe({
      next: (list) => {
        this.certificates.set(list);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => this.failed(err, 'Could not load your certificates.'),
    });
  }

  confirmPay(): void {
    const c = this.paying();
    if (!c) return;
    this.payBusy.set(true);
    this.payError.set(null);
    this.session.pay(c.id).subscribe({
      next: (updated) => {
        this.certificates.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
        this.payBusy.set(false);
        this.paying.set(null);
      },
      error: (err: HttpErrorResponse) => {
        this.payBusy.set(false);
        if (err.status === 401) this.failed(err, '');
        else this.payError.set(err.error?.error ?? 'Payment failed. Please try again.');
      },
    });
  }

  changePassword(): void {
    this.passwordBusy.set(true);
    this.passwordError.set(null);
    this.session.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.passwordBusy.set(false);
        this.passwordChanged.set(true);
        this.currentPassword = '';
        this.newPassword = '';
      },
      error: (err: HttpErrorResponse) => {
        this.passwordBusy.set(false);
        this.passwordError.set(err.error?.error ?? 'Could not change your password.');
      },
    });
  }

  openPasswordDialog(): void {
    this.passwordChanged.set(false);
    this.passwordError.set(null);
    this.changingPassword.set(true);
  }

  signOut(): void {
    this.session.signOut();
    this.router.navigate(['/student', this.slug, 'login']);
  }

  statusLabel(status: StudentCertificate['status']): string {
    switch (status) {
      case 'AwaitingPayment': return 'Awaiting payment';
      case 'PendingApproval': return 'Waiting for approval';
      case 'Active': return 'Issued';
      case 'Rejected': return 'Not approved';
      default: return status;
    }
  }

  statusClasses(status: StudentCertificate['status']): string {
    switch (status) {
      case 'Active': return 'bg-green-100 text-green-800';
      case 'AwaitingPayment': return 'bg-sky-100 text-sky-800';
      case 'PendingApproval': return 'bg-amber-100 text-amber-800';
      case 'Suspended': return 'bg-orange-100 text-orange-800';
      case 'Revoked': return 'bg-red-100 text-red-800';
      default: return 'bg-gray-100 text-gray-600';
    }
  }

  // An expired session (or a removed student) goes back to sign-in.
  private failed(err: HttpErrorResponse, message: string): void {
    if (err.status === 401 || err.status === 403) {
      this.signOut();
      return;
    }
    this.error.set(err.error?.error ?? message);
    this.loading.set(false);
  }
}
