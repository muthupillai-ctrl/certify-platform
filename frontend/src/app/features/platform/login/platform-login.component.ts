import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { PlatformAccountService } from '../../../core/services/platform-account.service';

@Component({
  selector: 'app-platform-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './platform-login.component.html',
})
export class PlatformLoginComponent {
  email = '';
  password = '';
  readonly errorMessage = signal<string | null>(null);
  readonly loading = signal(false);
  readonly forgotMode = signal(false);
  readonly resetSent = signal(false);

  constructor(
    private auth: AuthService,
    private router: Router,
    private accounts: PlatformAccountService,
  ) {}

  showForgot(on: boolean): void {
    this.forgotMode.set(on);
    this.resetSent.set(false);
    this.errorMessage.set(null);
  }

  sendReset(): void {
    this.errorMessage.set(null);
    this.loading.set(true);
    this.accounts.forgotPassword(this.email).subscribe({
      next: () => {
        this.loading.set(false);
        this.resetSent.set(true);
      },
      error: (err) => {
        this.loading.set(false);
        this.errorMessage.set(err.status === 429 ? 'Too many attempts. Wait a minute and try again.' : 'Could not send the email. Please try again.');
      },
    });
  }

  submit(): void {
    this.errorMessage.set(null);
    this.loading.set(true);

    this.auth.platformLogin({ email: this.email, password: this.password }).subscribe({
      next: (res) => {
        this.loading.set(false);
        this.router.navigate([res.passwordChangeRequired ? '/platform/profile' : '/platform']);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set('Invalid email or password.');
      },
    });
  }
}
