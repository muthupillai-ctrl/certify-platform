import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { PASSWORD_MIN_LENGTH } from '../../../core/models/auth.models';

// Staff user changing their own password. Also where anyone signed in with
// a temporary password (after an admin reset) is sent, and the only page
// they can reach until they've chosen a new one.
@Component({
  selector: 'app-change-password',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './change-password.component.html',
})
export class ChangePasswordComponent {
  currentPassword = '';
  newPassword = '';
  confirmPassword = '';
  readonly minLength = PASSWORD_MIN_LENGTH;
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal(false);
  readonly forced: boolean;

  constructor(
    readonly auth: AuthService,
    private router: Router,
  ) {
    this.forced = auth.passwordChangeRequired();
  }

  submit(): void {
    this.error.set(null);
    if (this.newPassword.length < this.minLength) {
      this.error.set(`The new password must be at least ${this.minLength} characters.`);
      return;
    }
    if (this.newPassword !== this.confirmPassword) {
      this.error.set("The new passwords don't match.");
      return;
    }
    this.saving.set(true);
    this.auth.changePassword({ currentPassword: this.currentPassword, newPassword: this.newPassword }).subscribe({
      next: () => {
        this.saving.set(false);
        this.done.set(true);
        this.currentPassword = this.newPassword = this.confirmPassword = '';
        if (this.forced) void this.router.navigate(['/admin']);
      },
      error: (err) => {
        this.saving.set(false);
        this.error.set(err.error?.error ?? "Couldn't change the password. Please try again.");
      },
    });
  }

  signOut(): void {
    this.auth.logout();
    void this.router.navigate(['/login']);
  }
}
