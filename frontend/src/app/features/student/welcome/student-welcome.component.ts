import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { StudentSessionService, slugOf } from '../../../core/services/student-session.service';
import { StudentWelcome } from '../../../core/models/student.models';
import { StudentShellComponent } from '../../../shared/student-shell/student-shell.component';

// The student's portal link (sent by their institution's app): greet them
// and have them choose a password, then go straight to their certificates.
@Component({
  selector: 'app-student-welcome',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StudentShellComponent],
  templateUrl: './student-welcome.component.html',
})
export class StudentWelcomeComponent implements OnInit {
  readonly loading = signal(true);
  readonly linkError = signal<string | null>(null);
  readonly welcome = signal<StudentWelcome | null>(null);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  password = '';
  confirm = '';
  private token = '';

  constructor(private route: ActivatedRoute, private router: Router, private session: StudentSessionService) {}

  get slug(): string {
    return slugOf(this.token);
  }

  ngOnInit(): void {
    this.token = this.route.snapshot.paramMap.get('token') ?? '';
    this.session.welcome(this.token).subscribe({
      next: (w) => {
        this.welcome.set(w);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        // A mangled link can name no institution at all; the server then
        // answers about the institution, which means nothing to a student.
        const message: string | undefined = err.error?.error;
        this.linkError.set(
          err.status === 404 || err.status === 410
            ? (message && !message.startsWith('Tenant') ? message : "This link isn't valid. Ask for a new one.")
            : 'Something went wrong. Please try again.',
        );
        this.loading.set(false);
      },
    });
  }

  submit(): void {
    this.error.set(null);
    if (this.password !== this.confirm) {
      this.error.set("The two passwords don't match.");
      return;
    }
    this.saving.set(true);
    this.session.setUp(this.token, this.password).subscribe({
      next: (r) => this.router.navigate(['/student', r.student.institutionSlug]),
      error: (err: HttpErrorResponse) => {
        this.error.set(err.error?.error ?? 'Could not set your password.');
        this.saving.set(false);
      },
    });
  }
}
