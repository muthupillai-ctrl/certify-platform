import { Component, OnDestroy, OnInit, computed } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { CertificateSearchService } from '../../../core/services/certificate-search.service';
import { CertificateActivityService } from '../../../core/services/certificate-activity.service';

@Component({
  selector: 'app-admin-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './admin-shell.component.html',
})
export class AdminShellComponent implements OnInit, OnDestroy {
  readonly initials = computed(() => {
    const email = this.auth.currentUser()?.email ?? '';
    const name = email.split('@')[0] ?? '';
    const parts = name.split(/[._-]/).filter(Boolean);
    const chars = parts.length >= 2 ? [parts[0][0], parts[1][0]] : [name[0] ?? '?'];
    return chars.join('').toUpperCase();
  });

  // Roles are checked in seniority order — a user can hold more than one,
  // and the topbar only has room to show a single label.
  readonly roleLabel = computed(() => {
    if (this.auth.hasRole('InstitutionAdmin')) return 'Institution Admin';
    if (this.auth.hasRole('Approver')) return 'Approver';
    if (this.auth.hasRole('Registrar')) return 'Registrar';
    if (this.auth.hasRole('Learner')) return 'Learner';
    return '';
  });

  private activityTimer: ReturnType<typeof setInterval> | undefined;

  constructor(
    protected auth: AuthService,
    private router: Router,
    readonly search: CertificateSearchService,
    readonly activity: CertificateActivityService,
  ) {}

  // The Certificates badge: pending approvals for this user, refreshed
  // every minute (and by the Certificates page after any change).
  ngOnInit(): void {
    this.refreshActivity();
    this.activityTimer = setInterval(() => this.refreshActivity(), 60_000);
  }

  ngOnDestroy(): void {
    clearInterval(this.activityTimer);
  }

  private refreshActivity(): void {
    this.activity.refresh().subscribe({ error: () => undefined });
  }

  // Searching from any admin page takes you to Certificates, where the
  // text filters the list (see CredentialsComponent.filteredCredentials).
  onSearch(value: string): void {
    this.search.query.set(value);
    if (value.trim() && !this.router.url.startsWith('/admin/certificates')) {
      void this.router.navigate(['/admin/certificates']);
    }
  }

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/login']);
  }
}
