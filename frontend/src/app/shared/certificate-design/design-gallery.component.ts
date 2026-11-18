import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FieldDefinition } from '../../core/models/achievement.models';
import { CertificatePreviewData } from '../../core/models/certificate-preview.models';
import { CertificatePreviewComponent } from '../certificate-preview/certificate-preview.component';
import { buildDummyPreviewData } from '../certificate-canvas-editor/layout-generation';
import { CERTIFICATE_DESIGNS, CertificateDesign } from './certificate-designs';

interface Card {
  design: CertificateDesign | null;
  preview: CertificatePreviewData | null;
}

// Thumbnails of the ready-made designs, drawn by the real certificate
// renderer with sample data and the template's own fields.
@Component({
  selector: 'app-design-gallery',
  standalone: true,
  imports: [CertificatePreviewComponent],
  template: `
    <ul class="grid gap-5 sm:grid-cols-2 xl:grid-cols-3" role="list">
      @for (card of cards; track card.design?.id ?? 'blank') {
        <li class="group bg-white border border-gray-200 rounded-xl overflow-hidden flex flex-col hover:border-gray-900 hover:shadow-md transition">
          <button type="button" (click)="picked.emit(card.design)" class="text-left flex flex-col h-full cursor-pointer"
            [attr.aria-label]="'Use the ' + (card.design?.name ?? 'blank') + ' design'">
            <div class="bg-gray-100 p-3 h-64 flex items-center justify-center overflow-hidden pointer-events-none">
              @if (card.preview; as p) {
                <!-- Sized to the design's own shape, so the whole certificate shows. -->
                <div class="h-full max-w-full" [style.aspect-ratio]="p.layout!.canvasWidth + ' / ' + p.layout!.canvasHeight">
                  <app-certificate-preview [data]="p" />
                </div>
              } @else {
                <div class="w-[85%] aspect-[11/8.5] bg-white border-2 border-dashed border-gray-300 flex items-center justify-center text-sm text-gray-500">Blank canvas</div>
              }
            </div>
            <div class="p-4 flex-1">
              <p class="font-semibold text-gray-900">{{ card.design?.name ?? 'Blank' }}</p>
              <p class="text-sm text-gray-600 mt-1">{{ card.design?.description ?? 'Start from a plain layout and design it yourself.' }}</p>
            </div>
          </button>
        </li>
      }
    </ul>
  `,
})
export class DesignGalleryComponent implements OnChanges {
  @Input() fields: FieldDefinition[] = [];
  @Input() institution = { name: 'Your Institution', logoUrl: null as string | null, primaryColor: null as string | null };
  @Input() showBlank = true;
  @Output() picked = new EventEmitter<CertificateDesign | null>();

  cards: Card[] = [];

  ngOnChanges(): void {
    const sampleSignatories = [
      { name: 'Dr. A. Kumar', designation: 'Principal' },
      { name: 'Prof. R. Iyer', designation: 'Head of Department' },
    ];
    this.cards = CERTIFICATE_DESIGNS.map((design) => {
      const r = design.build(this.fields);
      const preview = buildDummyPreviewData(
        { kind: design.kind, titleText: r.titleText, subtitleText: r.subtitleText, accentColor: r.accentColor, fontFamily: r.fontFamily,
          fieldSchema: r.fields ?? this.fields, layout: r.layout, signatories: sampleSignatories },
        this.institution,
      );
      return { design, preview };
    });
    if (this.showBlank) this.cards.push({ design: null, preview: null });
  }
}
