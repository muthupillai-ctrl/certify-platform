import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { TemplatePreview, TemplatePreviewService } from '../../core/services/template-preview.service';
import { CertificatePreviewData } from '../../core/models/certificate-preview.models';
import { CertificatePreviewComponent } from '../../shared/certificate-preview/certificate-preview.component';
import { buildDummyPreviewData } from '../../shared/certificate-canvas-editor/layout-generation';

// Public page behind a partner app's template preview link (POST
// /partner/templates/{id}/preview-link): the template drawn with sample data
// by the same CertificatePreviewComponent real certificates use. Made to sit
// in a new tab or an iframe/modal in the partner's app, so no site chrome.
@Component({
  selector: 'app-template-preview',
  standalone: true,
  imports: [CommonModule, CertificatePreviewComponent],
  templateUrl: './template-preview.component.html',
})
export class TemplatePreviewComponent implements OnInit {
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly preview = signal<TemplatePreview | null>(null);

  // Where a real certificate's QR code and code go: a QR to the public
  // look-up page and an obviously made-up code.
  readonly sampleQrUrl = `${window.location.origin}/verify`;
  readonly sampleCode = 'XXXX-XXXX-XXXX';

  readonly data = computed<CertificatePreviewData | null>(() => {
    const p = this.preview();
    return p ? { ...buildDummyPreviewData(p.template, p.institution), status: 'Sample' } : null;
  });

  constructor(private route: ActivatedRoute, private previews: TemplatePreviewService) {}

  ngOnInit(): void {
    const token = this.route.snapshot.paramMap.get('token') ?? '';
    this.previews.get(token).subscribe({
      next: (p) => {
        this.preview.set(p);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.error.set(
          err.status === 410 || err.status === 404
            ? (err.error?.error ?? "This preview link isn't valid.")
            : 'The preview could not be loaded. Please try again.',
        );
        this.loading.set(false);
      },
    });
  }
}
