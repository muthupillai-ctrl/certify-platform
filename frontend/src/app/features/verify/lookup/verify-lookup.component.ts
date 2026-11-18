import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { VerificationService } from '../../../core/services/verification.service';
import { InstallPromptService } from '../../../core/services/install-prompt.service';

// Public look-up for someone holding a certificate but not its link: the
// institution code plus the certificate code printed on it. On a match it
// opens the normal verify page, so the verifier sees exactly what a QR scan
// would show.
@Component({
  selector: 'app-verify-lookup',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './verify-lookup.component.html',
})
export class VerifyLookupComponent {
  institution = '';
  code = '';
  // "paper": an older paper certificate, by its printed number and the name on it.
  mode: 'code' | 'paper' = 'code';
  paperNumber = '';
  recipientName = '';
  readonly searching = signal(false);
  readonly error = signal<string | null>(null);

  constructor(
    private verification: VerificationService,
    private router: Router,
    readonly installPrompt: InstallPromptService,
  ) {}

  submit(): void {
    this.error.set(null);
    this.searching.set(true);
    const request = this.mode === 'paper'
      ? this.verification.lookupPaper(this.institution, this.paperNumber, this.recipientName)
      : this.verification.lookup(this.institution, this.code);
    request.subscribe({
      next: (r) => {
        this.searching.set(false);
        void this.router.navigate(['/verify', r.tenantSlug, r.credentialId]);
      },
      error: (err) => {
        this.searching.set(false);
        this.error.set(
          err.status === 404
            ? this.mode === 'paper'
              ? 'No certificate matches. Check the institution code and number, and give the name exactly as printed (initials can be left out).'
              : 'No certificate matches that institution code and certificate code. Check both and try again.'
            : err.status === 429
              ? (err.error?.error ?? 'Too many look-ups. Please wait a minute and try again.')
              : err.status === 0
                ? "You're offline. Look-up needs an internet connection; scanning the QR code works offline."
                : 'Something went wrong. Please try again.',
        );
      },
    });
  }
}
