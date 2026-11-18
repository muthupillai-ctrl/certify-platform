import { Component, Input } from '@angular/core';
import { CertificateFrame } from '../../core/models/certificate-preview.models';

// The border Certyfi draws around a certificate (layout.frame): an SVG over
// the whole canvas, under the elements. Shared by the editor and every
// place a certificate is shown, so both look the same.
@Component({
  selector: 'app-certificate-frame',
  standalone: true,
  template: `
    @if (frame && frame.style !== 'none') {
      <svg class="absolute inset-0 pointer-events-none" [attr.width]="width" [attr.height]="height" [attr.viewBox]="'0 0 ' + width + ' ' + height" aria-hidden="true">
        @switch (frame.style) {
          @case ('line') {
            <rect [attr.x]="i" [attr.y]="i" [attr.width]="width - 2 * i" [attr.height]="height - 2 * i" fill="none" [attr.stroke]="c" [attr.stroke-width]="w" />
          }
          @case ('double') {
            <rect [attr.x]="i" [attr.y]="i" [attr.width]="width - 2 * i" [attr.height]="height - 2 * i" fill="none" [attr.stroke]="c" [attr.stroke-width]="w" />
            <rect [attr.x]="i + w * 2.5" [attr.y]="i + w * 2.5" [attr.width]="width - 2 * (i + w * 2.5)" [attr.height]="height - 2 * (i + w * 2.5)" fill="none" [attr.stroke]="c" [attr.stroke-width]="w" />
          }
          @case ('classic') {
            <rect [attr.x]="i" [attr.y]="i" [attr.width]="width - 2 * i" [attr.height]="height - 2 * i" fill="none" [attr.stroke]="c" [attr.stroke-width]="w * 3" />
            <rect [attr.x]="i + w * 5" [attr.y]="i + w * 5" [attr.width]="width - 2 * (i + w * 5)" [attr.height]="height - 2 * (i + w * 5)" fill="none" [attr.stroke]="c" [attr.stroke-width]="Math.max(1, w / 2)" />
          }
          @case ('ornate') {
            <rect [attr.x]="i" [attr.y]="i" [attr.width]="width - 2 * i" [attr.height]="height - 2 * i" fill="none" [attr.stroke]="c" [attr.stroke-width]="w" />
            <rect [attr.x]="i + w * 3" [attr.y]="i + w * 3" [attr.width]="width - 2 * (i + w * 3)" [attr.height]="height - 2 * (i + w * 3)" fill="none" [attr.stroke]="c" [attr.stroke-width]="Math.max(1, w / 2)" />
            @for (corner of corners; track corner) {
              <g [attr.transform]="cornerTransform(corner)" fill="none" [attr.stroke]="c" [attr.stroke-width]="Math.max(1, w * 0.6)">
                <path d="M0 70 V26 Q0 0 26 0 H70" />
                <path d="M12 70 V34 Q12 12 34 12 H70" />
                <path d="M22 22 q14 4 18 18 q-14 -4 -18 -18 z" [attr.fill]="c" />
                <circle cx="46" cy="46" r="3" [attr.fill]="c" />
              </g>
            }
          }
          @case ('corners') {
            @for (corner of corners; track corner) {
              <g [attr.transform]="cornerTransform(corner)" fill="none" [attr.stroke]="c" [attr.stroke-width]="w * 1.5" stroke-linecap="square">
                <path d="M0 90 V0 H90" />
              </g>
            }
          }
          @case ('ribbon') {
            <rect x="0" y="0" [attr.width]="width" [attr.height]="i + w * 6" [attr.fill]="c" />
            <rect x="0" [attr.y]="height - i - w * 6" [attr.width]="width" [attr.height]="i + w * 6" [attr.fill]="c" />
            <line [attr.x1]="0" [attr.x2]="width" [attr.y1]="i + w * 8" [attr.y2]="i + w * 8" [attr.stroke]="c" [attr.stroke-width]="Math.max(1, w / 2)" />
            <line [attr.x1]="0" [attr.x2]="width" [attr.y1]="height - i - w * 8" [attr.y2]="height - i - w * 8" [attr.stroke]="c" [attr.stroke-width]="Math.max(1, w / 2)" />
          }
          @case ('band') {
            <rect x="0" y="0" [attr.width]="i + w * 8" [attr.height]="height" [attr.fill]="c" />
            <rect [attr.x]="i + w * 10" y="0" [attr.width]="Math.max(1, w / 2)" [attr.height]="height" [attr.fill]="c" />
          }
        }
      </svg>
    }
  `,
})
export class CertificateFrameComponent {
  @Input() frame: CertificateFrame | null | undefined = null;
  @Input() width = 700;
  @Input() height = 500;
  readonly corners = ['tl', 'tr', 'br', 'bl'] as const;
  readonly Math = Math;

  get c(): string {
    return this.frame?.color || '#1f2937';
  }

  get w(): number {
    return this.frame?.width ?? 2;
  }

  get i(): number {
    return this.frame?.inset ?? 24;
  }

  // Each corner ornament is drawn for the top-left, then turned into place.
  cornerTransform(corner: 'tl' | 'tr' | 'br' | 'bl'): string {
    const o = this.i + this.w * 6;
    switch (corner) {
      case 'tl':
        return `translate(${o} ${o})`;
      case 'tr':
        return `translate(${this.width - o} ${o}) scale(-1 1)`;
      case 'br':
        return `translate(${this.width - o} ${this.height - o}) scale(-1 -1)`;
      default:
        return `translate(${o} ${this.height - o}) scale(1 -1)`;
    }
  }
}
