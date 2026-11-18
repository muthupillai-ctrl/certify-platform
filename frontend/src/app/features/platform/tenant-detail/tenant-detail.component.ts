import { Component, OnInit, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TenantsService } from '../../../core/services/tenants.service';
import { TenantDetail, TenantProfile } from '../../../core/models/tenant.models';
import { InstitutionProfileFormComponent } from '../../../shared/institution-profile-form/institution-profile-form.component';
import { UserManagerComponent } from '../../../shared/user-manager/user-manager.component';

// SuperAdmin view of one institution: status (suspend / reactivate),
// counts, its profile, and its staff accounts — e.g. to help an institution
// whose only admin is locked out (reset their password from here).
@Component({
  selector: 'app-tenant-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, InstitutionProfileFormComponent, UserManagerComponent],
  templateUrl: './tenant-detail.component.html',
})
export class TenantDetailComponent implements OnInit {
  readonly tenant = signal<TenantDetail | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly tab = signal<'profile' | 'users'>('profile');

  readonly profileSaving = signal(false);
  readonly profileError = signal<string | null>(null);
  readonly profileSaved = signal<string | null>(null);

  readonly statusBusy = signal(false);
  readonly statusError = signal<string | null>(null);
  readonly showSuspend = signal(false);

  // Add an institution admin (only Ahamsys can).
  @ViewChild(UserManagerComponent) manager?: UserManagerComponent;
  readonly addingAdmin = signal(false);
  readonly adminBusy = signal(false);
  readonly adminError = signal<string | null>(null);
  newAdmin = { fullName: '', email: '', initialPassword: '' };

  addAdmin(): void {
    this.adminBusy.set(true);
    this.adminError.set(null);
    this.tenantsService.addAdmin(this.id, this.newAdmin).subscribe({
      next: () => {
        this.adminBusy.set(false);
        this.addingAdmin.set(false);
        this.newAdmin = { fullName: '', email: '', initialPassword: '' };
        this.manager?.load();
      },
      error: (err) => {
        this.adminBusy.set(false);
        this.adminError.set(err.error?.error ?? 'Could not add the admin.');
      },
    });
  }
  suspendReason = '';

  private id = '';

  constructor(
    private route: ActivatedRoute,
    private tenantsService: TenantsService,
  ) {}

  ngOnInit(): void {
    this.id = this.route.snapshot.paramMap.get('id') ?? '';
    this.tenantsService.get(this.id).subscribe({
      next: (t) => this.tenant.set(t),
      error: (err) => this.loadError.set(err.status === 404 ? 'Institution not found.' : 'Failed to load the institution.'),
    });
  }

  saveProfile(profile: TenantProfile): void {
    this.profileSaving.set(true);
    this.profileError.set(null);
    this.profileSaved.set(null);
    this.tenantsService.updateProfile(this.id, profile).subscribe({
      next: (t) => {
        this.tenant.set(t);
        this.profileSaving.set(false);
        this.profileSaved.set('Profile saved.');
      },
      error: (err) => {
        this.profileSaving.set(false);
        this.profileError.set(err.error?.error ?? 'Failed to save the profile.');
      },
    });
  }

  suspend(): void {
    this.statusBusy.set(true);
    this.statusError.set(null);
    this.tenantsService.suspend(this.id, this.suspendReason).subscribe({
      next: (t) => {
        this.tenant.set(t);
        this.statusBusy.set(false);
        this.showSuspend.set(false);
        this.suspendReason = '';
      },
      error: (err) => {
        this.statusBusy.set(false);
        this.statusError.set(err.error?.error ?? 'Failed to suspend.');
      },
    });
  }

  reactivate(): void {
    if (!confirm('Reactivate this institution? Its staff will be able to sign in again.')) return;
    this.statusBusy.set(true);
    this.statusError.set(null);
    this.tenantsService.reactivate(this.id).subscribe({
      next: (t) => {
        this.tenant.set(t);
        this.statusBusy.set(false);
      },
      error: (err) => {
        this.statusBusy.set(false);
        this.statusError.set(err.error?.error ?? 'Failed to reactivate.');
      },
    });
  }
}
