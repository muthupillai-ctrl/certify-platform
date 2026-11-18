import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TenantSettingsService } from '../../../core/services/tenant-settings.service';
import { TenantProfile } from '../../../core/models/tenant.models';
import { InstitutionProfileFormComponent } from '../../../shared/institution-profile-form/institution-profile-form.component';

@Component({
  selector: 'app-tenant-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, InstitutionProfileFormComponent],
  templateUrl: './tenant-settings.component.html',
})
export class TenantSettingsComponent implements OnInit {
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly savedMessage = signal<string | null>(null);

  // Institution profile (public details + private contact/address).
  readonly profile = signal<TenantProfile | null>(null);
  readonly profileSaving = signal(false);
  readonly profileError = signal<string | null>(null);
  readonly profileSaved = signal<string | null>(null);

  logoUrl = '';
  primaryColor = '';

  // Student payments: days to pay before an unpaid certificate is cancelled.
  paymentDays: number | null = null;
  readonly paymentSaving = signal(false);
  readonly paymentError = signal<string | null>(null);
  readonly paymentSaved = signal<string | null>(null);

  constructor(private tenantSettingsService: TenantSettingsService) {}

  ngOnInit(): void {
    this.tenantSettingsService.getPayments().subscribe({
      next: (p) => (this.paymentDays = p.paymentDays),
      error: () => this.paymentError.set('Failed to load the payment setting.'),
    });

    this.tenantSettingsService.getProfile().subscribe({
      next: (p) => this.profile.set(p),
      error: () => this.profileError.set('Failed to load the institution profile.'),
    });

    this.tenantSettingsService.getBranding().subscribe({
      next: (branding) => {
        this.logoUrl = branding.logoUrl ?? '';
        this.primaryColor = branding.primaryColor ?? '';
        this.loading.set(false);
      },
      error: () => {
        this.errorMessage.set('Failed to load branding settings.');
        this.loading.set(false);
      },
    });
  }

  savePayments(): void {
    this.paymentError.set(null);
    this.paymentSaved.set(null);
    this.paymentSaving.set(true);
    this.tenantSettingsService.updatePayments(Number(this.paymentDays ?? 0)).subscribe({
      next: (p) => {
        this.paymentDays = p.paymentDays;
        this.paymentSaving.set(false);
        this.paymentSaved.set('Saved. It applies to certificates requested from now on.');
      },
      error: (err) => {
        this.paymentSaving.set(false);
        this.paymentError.set(err.error?.error ?? 'Failed to save.');
      },
    });
  }

  save(): void {
    this.errorMessage.set(null);
    this.savedMessage.set(null);
    this.saving.set(true);

    this.tenantSettingsService
      .updateBranding({
        logoUrl: this.logoUrl.trim() || null,
        primaryColor: this.primaryColor.trim() || null,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.savedMessage.set('Branding saved.');
        },
        error: (err) => {
          this.saving.set(false);
          this.errorMessage.set(err.error?.error ?? 'Failed to save branding settings.');
        },
      });
  }

  saveProfile(profile: TenantProfile): void {
    this.profileSaving.set(true);
    this.profileError.set(null);
    this.profileSaved.set(null);
    this.tenantSettingsService.updateProfile(profile).subscribe({
      next: (saved) => {
        this.profile.set(saved);
        this.profileSaving.set(false);
        this.profileSaved.set('Profile saved.');
      },
      error: (err) => {
        this.profileSaving.set(false);
        this.profileError.set(err.error?.error ?? 'Failed to save the profile.');
      },
    });
  }
}
