import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TenantsService } from '../../../core/services/tenants.service';
import { Tenant } from '../../../core/models/tenant.models';

@Component({
  selector: 'app-tenants',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './tenants.component.html',
})
export class TenantsComponent implements OnInit {
  readonly tenants = signal<Tenant[]>([]);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly showForm = signal(false);
  readonly submitting = signal(false);

  name = '';
  slug = '';
  institutionAdminEmail = '';
  institutionAdminInitialPassword = '';
  institutionAdminFullName = '';
  secondAdminEmail = '';
  secondAdminFullName = '';
  secondAdminInitialPassword = '';

  constructor(private tenantsService: TenantsService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.tenantsService.list().subscribe({
      next: (tenants) => {
        this.tenants.set(tenants);
        this.loading.set(false);
      },
      error: () => {
        this.errorMessage.set('Failed to load tenants.');
        this.loading.set(false);
      },
    });
  }

  toggleForm(): void {
    this.errorMessage.set(null);
    this.showForm.set(!this.showForm());
  }

  submit(): void {
    this.errorMessage.set(null);
    this.submitting.set(true);

    this.tenantsService
      .create({
        name: this.name,
        slug: this.slug,
        institutionAdminEmail: this.institutionAdminEmail,
        institutionAdminInitialPassword: this.institutionAdminInitialPassword,
        institutionAdminFullName: this.institutionAdminFullName,
        secondAdminEmail: this.secondAdminEmail,
        secondAdminFullName: this.secondAdminFullName,
        secondAdminInitialPassword: this.secondAdminInitialPassword,
      })
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.showForm.set(false);
          this.resetForm();
          this.load();
        },
        error: (err) => {
          this.submitting.set(false);
          this.errorMessage.set(err.error?.error ?? 'Failed to create tenant.');
        },
      });
  }

  private resetForm(): void {
    this.name = '';
    this.slug = '';
    this.institutionAdminEmail = '';
    this.institutionAdminInitialPassword = '';
    this.institutionAdminFullName = '';
    this.secondAdminEmail = '';
    this.secondAdminFullName = '';
    this.secondAdminInitialPassword = '';
  }
}
