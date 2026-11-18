import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './login.component.html',
})
export class LoginComponent {
  // Prefilled from whatever was used last time (see AuthService.getTenantSlug)
  // so a returning user on the same device doesn't retype it — it's an
  // institution code, not a secret.
  tenantSlug = '';
  email = '';
  password = '';
  readonly errorMessage = signal<string | null>(null);
  readonly loading = signal(false);
  readonly showPassword = signal(false);

  constructor(private auth: AuthService, private router: Router) {
    this.tenantSlug = this.auth.getTenantSlug() ?? '';
  }

  submit(): void {
    this.errorMessage.set(null);
    this.loading.set(true);

    this.auth.login(this.tenantSlug, { email: this.email, password: this.password }).subscribe({
      next: (res) => {
        this.loading.set(false);
        // Temporary password (after an admin reset): choose a new one first.
        this.router.navigate([res.passwordChangeRequired ? '/change-password' : '/admin']);
      },
      error: (err) => {
        this.loading.set(false);
        // A wrong tenantSlug resolves as 404 at the middleware (tenant not
        // found), not 401 — worth a distinct message rather than "invalid
        // email or password" for that case.
        // 403 with a message: the institution's account is suspended.
        this.errorMessage.set(
          err.status === 404
            ? 'Unknown institution code.'
            : err.status === 403 && err.error?.error
              ? err.error.error
              : 'Invalid email or password.',
        );
      },
    });
  }
}
