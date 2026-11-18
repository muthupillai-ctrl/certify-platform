import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CertificateTemplatesService } from '../../../core/services/certificate-templates.service';
import { TenantSettingsService } from '../../../core/services/tenant-settings.service';
import { AuthService } from '../../../core/services/auth.service';
import { CertificateTemplate } from '../../../core/models/certificate-template.models';
import { TenantBranding } from '../../../core/models/tenant.models';
import { CertificatePreviewData } from '../../../core/models/certificate-preview.models';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { CertificatePreviewComponent } from '../../../shared/certificate-preview/certificate-preview.component';
import { buildDummyPreviewData } from '../../../shared/certificate-canvas-editor/layout-generation';

// List/catalog page only — Create/Amend live at their own full-page routes
// (see TemplateEditorComponent) since the canvas editor needs real screen
// space that a modal can't give it. View renders the template through the
// SAME CertificatePreviewComponent a real credential uses, populated with
// dummy values — "see the actual certificate with dummy values", not a
// text-only field listing.
@Component({
  selector: 'app-certificate-templates',
  standalone: true,
  imports: [CommonModule, RouterLink, ModalComponent, CertificatePreviewComponent],
  templateUrl: './certificate-templates.component.html',
})
export class CertificateTemplatesComponent implements OnInit {
  readonly templates = signal<CertificateTemplate[]>([]);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly deactivatingId = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);
  readonly viewingTemplate = signal<CertificateTemplate | null>(null);
  readonly branding = signal<TenantBranding | null>(null);

  constructor(
    private certificateTemplatesService: CertificateTemplatesService,
    private tenantSettingsService: TenantSettingsService,
    private auth: AuthService,
  ) {}

  ngOnInit(): void {
    this.load();
    this.tenantSettingsService.getBranding().subscribe({ next: (b) => this.branding.set(b) });
  }

  load(): void {
    this.loading.set(true);
    this.certificateTemplatesService.list().subscribe({
      next: (templates) => {
        this.templates.set(templates);
        this.loading.set(false);
      },
      error: () => {
        this.errorMessage.set('Failed to load certificate templates.');
        this.loading.set(false);
      },
    });
  }

  view(template: CertificateTemplate): void {
    this.viewingTemplate.set(template);
  }

  closeView(): void {
    this.viewingTemplate.set(null);
  }

  viewingPreviewData(): CertificatePreviewData | null {
    const t = this.viewingTemplate();
    if (!t) return null;
    return buildDummyPreviewData(t, {
      name: this.auth.currentUser()?.tenant_slug ?? '',
      logoUrl: this.branding()?.logoUrl ?? null,
      primaryColor: this.branding()?.primaryColor ?? null,
    });
  }

  deactivate(template: CertificateTemplate): void {
    this.errorMessage.set(null);
    this.deactivatingId.set(template.id);
    this.certificateTemplatesService.deactivate(template.id).subscribe({
      next: () => {
        this.deactivatingId.set(null);
        this.load();
      },
      error: (err) => {
        this.deactivatingId.set(null);
        this.errorMessage.set(err.error?.error ?? 'Failed to deactivate template.');
      },
    });
  }

  // Permanent — the backend refuses with a 409 (and a clear message) once
  // any achievement has been created from this template; that error is
  // just surfaced as-is rather than re-worded here.
  deleteTemplate(template: CertificateTemplate): void {
    if (!confirm(`Permanently delete "${template.name}"? This cannot be undone.`)) return;

    this.errorMessage.set(null);
    this.deletingId.set(template.id);
    this.certificateTemplatesService.delete(template.id).subscribe({
      next: () => {
        this.deletingId.set(null);
        this.load();
      },
      error: (err) => {
        this.deletingId.set(null);
        this.errorMessage.set(err.error?.error ?? 'Failed to delete template.');
      },
    });
  }

  fieldSummary(template: CertificateTemplate): string {
    if (template.fieldSchema.length === 0) return '—';
    return template.fieldSchema.map((f) => f.label).join(', ');
  }
}
