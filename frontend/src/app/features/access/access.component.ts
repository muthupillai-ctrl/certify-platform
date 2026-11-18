import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { HomeAccessService } from '../../core/services/home-access.service';

// "Enter your mobile number" for the private home page. Verification and the
// logins are not behind it, so they're linked here too.
@Component({
  selector: 'app-access',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './access.component.html',
})
export class AccessComponent implements OnInit {
  mobile = '';
  readonly checking = signal(false);
  readonly error = signal<string | null>(null);

  constructor(
    private access: HomeAccessService,
    private router: Router,
  ) {}

  ngOnInit(): void {
    // Already allowed (e.g. a bookmark): straight on.
    this.access.hasAccess().subscribe((ok) => {
      if (ok) void this.router.navigateByUrl('/presentation');
    });
  }

  valid(): boolean {
    const digits = this.mobile.replace(/\D/g, '');
    return /^[\d\s+\-().]+$/.test(this.mobile.trim()) && digits.length >= 10 && digits.length <= 16;
  }

  submit(): void {
    if (!this.valid()) return;
    this.checking.set(true);
    this.error.set(null);
    this.access.enter(this.mobile).subscribe({
      // A full load, so the server-side check also sees the new pass.
      next: () => (window.location.href = '/presentation'),
      error: (err) => {
        this.checking.set(false);
        this.error.set(
          err.status === 429
            ? 'Too many attempts. Please wait a minute and try again.'
            : (err.error?.error ?? 'Something went wrong. Please try again.'),
        );
      },
    });
  }
}
