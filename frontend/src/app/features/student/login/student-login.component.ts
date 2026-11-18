import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { StudentSessionService } from '../../../core/services/student-session.service';
import { StudentPortalInstitution } from '../../../core/models/student.models';
import { StudentShellComponent } from '../../../shared/student-shell/student-shell.component';

// Student portal sign-in. /student/login needs only email and password: the
// API finds the student's institution, and asks which one if the password
// matches at several. /student/<code>/login (the link in emails) signs in
// to that institution and shows its banners.
@Component({
  selector: 'app-student-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StudentShellComponent],
  templateUrl: './student-login.component.html',
})
export class StudentLoginComponent implements OnInit {
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  // Forgot password: asking for the email, then "check your inbox".
  readonly forgotMode = signal(false);
  readonly resetSent = signal(false);
  readonly showPassword = signal(false);
  // The password matched at more than one institution: which to open.
  readonly choices = signal<StudentPortalInstitution[] | null>(null);
  email = '';
  password = '';
  // From the address, if it names the institution.
  routeSlug = '';
  // Whose name and banners to show: the address's institution, else the
  // one this device last signed in to (none on a first visit).
  shellSlug = '';

  constructor(private route: ActivatedRoute, private router: Router, private session: StudentSessionService) {}

  ngOnInit(): void {
    this.routeSlug = this.route.snapshot.paramMap.get('tenantSlug') ?? '';
    const slug = this.routeSlug || this.session.lastInstitution();
    this.shellSlug = slug;
    if (slug && this.session.isSignedIn(slug)) this.router.navigate(['/student', slug]);
  }

  sendReset(): void {
    this.error.set(null);
    this.loading.set(true);
    const call: Observable<void> = this.routeSlug
      ? this.session.forgotPassword(this.routeSlug, this.email)
      : this.session.forgotPasswordAnywhere(this.email);
    call.subscribe({
      next: () => {
        this.loading.set(false);
        this.resetSent.set(true);
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        this.error.set(err.status === 429 ? 'Too many attempts. Wait a minute and try again.' : 'Could not send the email. Please try again.');
      },
    });
  }

  showForgot(on: boolean): void {
    this.forgotMode.set(on);
    this.resetSent.set(false);
    this.choices.set(null);
    this.error.set(null);
  }

  submit(): void {
    this.error.set(null);
    this.loading.set(true);
    if (this.routeSlug) {
      this.signInTo(this.routeSlug);
      return;
    }
    this.session.signInAnywhere(this.email, this.password).subscribe({
      next: (r) => {
        if (r.signedIn) {
          this.router.navigate(['/student', r.signedIn.student.institutionSlug]);
        } else {
          this.choices.set(r.institutions ?? []);
          this.loading.set(false);
        }
      },
      error: (err: HttpErrorResponse) => this.failed(err),
    });
  }

  choose(institution: StudentPortalInstitution): void {
    this.error.set(null);
    this.loading.set(true);
    this.signInTo(institution.slug);
  }

  backToForm(): void {
    this.choices.set(null);
    this.error.set(null);
  }

  private signInTo(slug: string): void {
    this.session.login(slug, this.email, this.password).subscribe({
      next: () => this.router.navigate(['/student', slug]),
      error: (err: HttpErrorResponse) => this.failed(err),
    });
  }

  private failed(err: HttpErrorResponse): void {
    this.error.set(
      err.status === 401 ? 'Wrong email or password.'
        : err.status === 429 ? 'Too many attempts. Wait a minute and try again.'
        : 'Could not sign in. Please try again.',
    );
    this.loading.set(false);
  }
}
