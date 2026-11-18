import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import {
  PlatformAccountChange, PlatformAccountService, PlatformAdmin, PlatformAdminWithPassword,
} from '../../../core/services/platform-account.service';
import { ModalComponent } from '../../../shared/modal/modal.component';

// Platform users: the accounts that can sign in to the platform. A second
// account means losing one never locks everyone out.
@Component({
  selector: 'app-platform-users',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './platform-users.component.html',
})
export class PlatformUsersComponent implements OnInit {
  readonly admins = signal<PlatformAdmin[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly adding = signal(false);
  readonly busy = signal(false);
  newEmail = '';
  newName = '';

  // A temporary password just made (new account or reset), shown once.
  readonly issued = signal<{ who: string; email: string; password: string } | null>(null);
  readonly copied = signal(false);

  readonly historyFor = signal<PlatformAdmin | null>(null);
  readonly history = signal<PlatformAccountChange[]>([]);
  readonly confirming = signal<{ admin: PlatformAdmin; action: 'reset' | 'deactivate' } | null>(null);

  constructor(private accounts: PlatformAccountService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.accounts.listAdmins().subscribe({
      next: (list) => {
        this.admins.set(list);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.error.set(err.error?.error ?? 'Could not load platform users.');
        this.loading.set(false);
      },
    });
  }

  add(): void {
    this.busy.set(true);
    this.error.set(null);
    this.accounts.createAdmin(this.newEmail, this.newName).subscribe({
      next: (r) => this.done(r, 'created'),
      error: (err: HttpErrorResponse) => this.fail(err),
    });
  }

  confirm(): void {
    const c = this.confirming();
    if (!c) return;
    this.busy.set(true);
    this.error.set(null);
    if (c.action === 'reset') {
      this.accounts.resetAdmin(c.admin.id).subscribe({ next: (r) => this.done(r, 'reset'), error: (err: HttpErrorResponse) => this.fail(err) });
    } else {
      this.accounts.setAdminActive(c.admin.id, false).subscribe({ next: () => this.done(null, ''), error: (err: HttpErrorResponse) => this.fail(err) });
    }
  }

  reactivate(admin: PlatformAdmin): void {
    this.accounts.setAdminActive(admin.id, true).subscribe({ next: () => this.load(), error: (err: HttpErrorResponse) => this.fail(err) });
  }

  showHistory(admin: PlatformAdmin): void {
    this.historyFor.set(admin);
    this.history.set([]);
    this.accounts.adminHistory(admin.id).subscribe({ next: (h) => this.history.set(h) });
  }

  copy(): void {
    const p = this.issued()?.password;
    if (p) navigator.clipboard?.writeText(p).then(() => this.copied.set(true));
  }

  private done(r: PlatformAdminWithPassword | null, how: string): void {
    this.busy.set(false);
    this.adding.set(false);
    this.confirming.set(null);
    this.newEmail = this.newName = '';
    this.copied.set(false);
    if (r) this.issued.set({ who: `${r.admin.fullName} (${how})`, email: r.admin.email, password: r.temporaryPassword });
    this.load();
  }

  private fail(err: HttpErrorResponse): void {
    this.busy.set(false);
    this.error.set(err.error?.error ?? 'Something went wrong.');
  }
}
