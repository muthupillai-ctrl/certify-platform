import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

// The public landing page: what Certyfi is, for institutions, students and
// whoever checks a certificate, and the ways in. The presentation
// (/presentation) stays private behind the mobile-number gate.
@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink, FormsModule],
  templateUrl: './landing.component.html',
})
export class LandingComponent {
  // Students sign in per institution (/student/<code>/login).
  institutionCode = '';
  readonly year = new Date().getFullYear();

  readonly steps = [
    { title: 'Design', text: 'The institution designs its certificate and marks-card templates.' },
    { title: 'Prepare', text: 'A registrar, or a connected system, enters the recipient and details.' },
    { title: 'Approve', text: 'A different person reviews and approves it.' },
    { title: 'Sign', text: "It's signed with the institution's own key and recorded." },
    { title: 'Verify', text: 'Anyone scans the QR code and sees the answer at once.' },
  ];

  readonly layers = [
    { title: 'Official record', text: "Verification always shows the institution's own record, so an edited PDF gives itself away." },
    { title: 'Digital signature', text: "Each certificate is signed with the institution's own key, so it can't be forged." },
    { title: 'Tamper-evident audit trail', text: 'Every action is chained to the one before it, so nothing changes silently.' },
    { title: 'Bitcoin timestamp', text: 'A fingerprint of the records is timestamped on Bitcoin, so history can\'t be rewritten, even by us.' },
  ];

  constructor(readonly auth: AuthService, private router: Router) {}

  studentSignIn(): void {
    const code = this.institutionCode.trim().toLowerCase();
    if (code) this.router.navigate(['/student', code, 'login']);
  }
}
