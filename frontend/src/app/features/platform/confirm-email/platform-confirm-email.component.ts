import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { PlatformAccountService } from '../../../core/services/platform-account.service';

// Opened from the link emailed to a platform admin's new address.
@Component({
  selector: 'app-platform-confirm-email',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="min-h-dvh flex items-center justify-center bg-gray-50 px-4">
      <div class="w-full max-w-sm">
        <p class="text-xs font-medium uppercase tracking-wider text-gray-500 mb-1">Certyfi · Platform</p>
        @if (busy()) {
          <p class="text-sm text-gray-500">Confirming...</p>
        } @else if (email()) {
          <h1 class="text-xl font-semibold text-gray-900 mb-2">Email confirmed</h1>
          <p class="text-sm text-gray-600 mb-4">You now sign in with {{ email() }}. Your previous address has been told.</p>
          <a routerLink="/platform/login" class="inline-block px-4 py-2 rounded-md bg-gray-900 text-white text-sm font-medium hover:bg-gray-700">Platform sign-in</a>
        } @else {
          <h1 class="text-xl font-semibold text-gray-900 mb-2">Link not working</h1>
          <p class="text-sm text-gray-600" role="alert">{{ error() }}</p>
        }
      </div>
    </div>
  `,
})
export class PlatformConfirmEmailComponent implements OnInit {
  readonly busy = signal(true);
  readonly email = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor(private route: ActivatedRoute, private accounts: PlatformAccountService) {}

  ngOnInit(): void {
    this.accounts.confirmEmail(this.route.snapshot.paramMap.get('token') ?? '').subscribe({
      next: (r) => {
        this.email.set(r.email);
        this.busy.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.error.set(err.error?.error ?? "This link isn't valid.");
        this.busy.set(false);
      },
    });
  }
}
