import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../../core/services/auth.service';
import { PlatformAccount, PlatformAccountService } from '../../../core/services/platform-account.service';

// The signed-in platform admin's own profile. With a temporary password
// (new account or reset) only the password section shows, and nothing
// else in the platform works until it's changed.
@Component({
  selector: 'app-platform-profile',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './platform-profile.component.html',
})
export class PlatformProfileComponent implements OnInit {
  readonly account = signal<PlatformAccount | null>(null);
  readonly error = signal<string | null>(null);

  name = '';
  readonly nameBusy = signal(false);
  readonly nameNote = signal<string | null>(null);

  currentPassword = '';
  newPassword = '';
  confirmPassword = '';
  readonly passwordBusy = signal(false);
  readonly passwordError = signal<string | null>(null);
  readonly passwordNote = signal<string | null>(null);

  newEmail = '';
  emailPassword = '';
  readonly emailBusy = signal(false);
  readonly emailError = signal<string | null>(null);
  readonly emailNote = signal<string | null>(null);

  constructor(private accounts: PlatformAccountService, readonly auth: AuthService, private router: Router) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.accounts.get().subscribe({
      next: (a) => {
        this.account.set(a);
        this.name = a.fullName;
      },
      error: (err: HttpErrorResponse) => this.failed(err, 'Could not load your profile.'),
    });
  }

  saveName(): void {
    this.nameBusy.set(true);
    this.nameNote.set(null);
    this.accounts.updateName(this.name).subscribe({
      next: (a) => {
        this.account.set(a);
        this.nameBusy.set(false);
        this.nameNote.set('Saved.');
      },
      error: (err: HttpErrorResponse) => {
        this.nameBusy.set(false);
        this.nameNote.set(err.error?.error ?? 'Could not save.');
      },
    });
  }

  changePassword(): void {
    this.passwordError.set(null);
    this.passwordNote.set(null);
    if (this.newPassword !== this.confirmPassword) {
      this.passwordError.set("The two new passwords don't match.");
      return;
    }
    this.passwordBusy.set(true);
    const wasTemporary = this.auth.passwordChangeRequired();
    this.accounts.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.passwordBusy.set(false);
        this.currentPassword = this.newPassword = this.confirmPassword = '';
        if (wasTemporary) {
          this.router.navigate(['/platform']);
          return;
        }
        this.passwordNote.set('Password changed. Any other signed-in sessions have been ended, and a confirmation was emailed to you.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.passwordBusy.set(false);
        if (err.status === 401) this.failed(err, '');
        else this.passwordError.set(err.error?.error ?? 'Could not change your password.');
      },
    });
  }

  changeEmail(): void {
    this.emailError.set(null);
    this.emailNote.set(null);
    this.emailBusy.set(true);
    this.accounts.changeEmail(this.newEmail, this.emailPassword).subscribe({
      next: (r) => {
        this.emailBusy.set(false);
        this.emailPassword = '';
        this.emailNote.set(`We've emailed a confirmation link to ${r.sentTo}. Your email changes when you open it (within 24 hours).`);
      },
      error: (err: HttpErrorResponse) => {
        this.emailBusy.set(false);
        if (err.status === 401) this.failed(err, '');
        else this.emailError.set(err.error?.error ?? 'Could not start the change.');
      },
    });
  }

  label(field: string): string {
    return field === 'Password' ? 'Password changed' : field;
  }

  // Session ended (password changed elsewhere, account reset or
  // deactivated): back to sign-in.
  private failed(err: HttpErrorResponse, message: string): void {
    if (err.status === 401) {
      this.auth.logout();
      this.router.navigate(['/platform/login']);
      return;
    }
    this.error.set(err.error?.error ?? message);
  }
}
