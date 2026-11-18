import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { PlatformAccountService } from '../../../core/services/platform-account.service';

// Opened from the platform "forgot password" email: choose a new password,
// then sign in. The link works once, for one hour.
@Component({
  selector: 'app-platform-reset-password',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="min-h-dvh flex items-center justify-center bg-gray-50 px-4">
      <div class="w-full max-w-sm">
        <p class="text-xs font-medium uppercase tracking-wider text-gray-500 mb-1">Certyfi · Platform</p>
        @if (done()) {
          <h1 class="text-xl font-semibold text-gray-900 mb-2">Password changed</h1>
          <p class="text-sm text-gray-600 mb-4">Sign in with your new password. Any other signed-in sessions have been ended.</p>
          <a routerLink="/platform/login" class="inline-block px-4 py-2 rounded-md bg-gray-900 text-white text-sm font-medium hover:bg-gray-700">Platform sign-in</a>
        } @else {
          <h1 class="text-xl font-semibold text-gray-900 mb-6">Choose a new password</h1>
          <form (ngSubmit)="submit()" #f="ngForm" class="space-y-4">
            <div>
              <label for="pr-new" class="block text-sm font-medium text-gray-700 mb-1">New password</label>
              <input id="pr-new" name="password" type="password" [(ngModel)]="password" required minlength="12" autocomplete="new-password"
                class="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-gray-900" />
              <p class="mt-1 text-xs text-gray-500">At least 12 characters.</p>
            </div>
            <div>
              <label for="pr-confirm" class="block text-sm font-medium text-gray-700 mb-1">New password again</label>
              <input id="pr-confirm" name="confirm" type="password" [(ngModel)]="confirm" required autocomplete="new-password"
                class="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-gray-900" />
            </div>
            @if (error()) {
              <p class="text-sm text-red-600" role="alert">{{ error() }}</p>
            }
            <button type="submit" [disabled]="f.invalid || busy()"
              class="w-full px-4 py-2 rounded-md bg-gray-900 text-white text-sm font-medium hover:bg-gray-700 disabled:opacity-50 cursor-pointer">
              {{ busy() ? 'Saving...' : 'Set new password' }}
            </button>
          </form>
          <a routerLink="/platform/login" class="block mt-4 text-sm text-gray-500 hover:text-gray-700">Back to sign in</a>
        }
      </div>
    </div>
  `,
})
export class PlatformResetPasswordComponent {
  readonly busy = signal(false);
  readonly done = signal(false);
  readonly error = signal<string | null>(null);
  password = '';
  confirm = '';

  constructor(private route: ActivatedRoute, private accounts: PlatformAccountService) {}

  submit(): void {
    this.error.set(null);
    if (this.password !== this.confirm) {
      this.error.set("The two passwords don't match.");
      return;
    }
    this.busy.set(true);
    this.accounts.resetPassword(this.route.snapshot.paramMap.get('token') ?? '', this.password).subscribe({
      next: () => {
        this.busy.set(false);
        this.done.set(true);
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(err.error?.error ?? 'Could not change your password.');
      },
    });
  }
}
