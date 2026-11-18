import { AfterViewInit, Component, ElementRef, Input, OnChanges, OnDestroy, SimpleChanges, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CertificatePreviewData, LayoutElement } from '../../core/models/certificate-preview.models';
import { FieldDefinition } from '../../core/models/achievement.models';
import { QrCodeComponent } from '../qr-code/qr-code.component';
import { CertificateFrameComponent } from '../certificate-design/certificate-frame.component';
import { hasFont, lineStyle, typeStyle } from '../certificate-design/certificate-style';

// The certificate/transcript "card" itself — used by the public verify page
// (real, server-confirmed data), the New Credential form (a live, in-
// progress draft that doesn't exist on the server yet), and an admin
// Preview of an already-drafted credential of any status. Callers own the
// surrounding chrome (loading/error states, page width, "Print" link); this
// component only renders the card.
@Component({
  selector: 'app-certificate-preview',
  standalone: true,
  imports: [CommonModule, QrCodeComponent, CertificateFrameComponent],
  templateUrl: './certificate-preview.component.html',
})
export class CertificatePreviewComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input({ required: true }) data!: CertificatePreviewData;
  // null hides the QR/credential-id parts of the footer — used for a Draft
  // preview, where neither exists yet.
  @Input() qrUrl: string | null = null;
  // The plain verification link, drawn instead of qrUrl when a QR slot is
  // too small for the longer offline-capable link (see QrCodeComponent).
  @Input() qrFallbackUrl: string | null = null;
  @Input() credentialId: string | null = null;
  // Shown instead of credentialId wherever the certificate prints its ID:
  // the short code a verifier can type into the look-up page.
  @Input() verificationCode: string | null = null;

  // A layout-driven certificate's canvas is an arbitrary, sometimes large,
  // fixed pixel size (e.g. 1100px for the Landscape preset, or whatever an
  // uploaded background image's own dimensions produced) — unlike the two
  // legacy hardcoded layouts, which were always responsive/Tailwind-sized
  // to fit their container. Without scaling, a certificate wider than its
  // container (a View modal, the admin Overview tab, ...) just gets
  // silently clipped rather than shrunk to fit. ResizeObserver measures the
  // actual available width and this computes a CSS transform: scale(...)
  // that's never > 1 (never upscale past the design's real size), applied
  // with transform-origin: top left so it scales down toward the corner
  // rather than the center.
  readonly scale = signal(1);
  @ViewChild('scaleContainer') private scaleContainer?: ElementRef<HTMLDivElement>;
  private resizeObserver?: ResizeObserver;

  ngAfterViewInit(): void {
    if (!this.scaleContainer) return;
    this.resizeObserver = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width;
      if (width) this.updateScale(width);
    });
    this.resizeObserver.observe(this.scaleContainer.nativeElement);
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['data'] && this.scaleContainer) {
      this.updateScale(this.scaleContainer.nativeElement.clientWidth);
    }
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
  }

  private updateScale(containerWidth: number): void {
    const canvasWidth = this.data.layout?.canvasWidth;
    if (!canvasWidth || !containerWidth) {
      this.scale.set(1);
      return;
    }
    this.scale.set(Math.min(1, containerWidth / canvasWidth));
  }

  // Read-only, by schema-defined key — see FieldSchemaValidator/VerifyComponent's
  // original comment on why this must never spread/iterate the parsed
  // object's own keys.
  fieldValue(key: string): string {
    const value = this.data.fieldValues[key];
    return typeof value === 'string' ? value : '';
  }

  tableRows(key: string): Array<Record<string, string | null>> {
    const value = this.data.fieldValues[key];
    return Array.isArray(value) ? value : [];
  }

  flatFields(): FieldDefinition[] {
    return this.data.fieldSchema.filter((f) => f.type !== 'table');
  }

  mainTableField(): FieldDefinition | null {
    return this.data.fieldSchema.find((f) => f.type === 'table') ?? null;
  }

  // Position/size/font/color/alignment ONLY — see LayoutElement's own
  // comment on why the actual content is never part of this JSON.
  elementStyle(el: LayoutElement): Record<string, string> {
    const style: Record<string, string> = {
      position: 'absolute',
      left: `${el.x}px`,
      top: `${el.y}px`,
      width: `${el.width}px`,
    };
    if (el.height !== undefined) style['height'] = `${el.height}px`;
    return { ...style, ...typeStyle(el, this.data.fontFamily) };
  }

  hasFont(el: LayoutElement): boolean {
    return hasFont(el, this.data.fontFamily);
  }

  lineStyle(el: LayoutElement): Record<string, string> {
    return lineStyle(el);
  }

  // The design places the signatories itself (else they go below it).
  hasSignatoriesElement(): boolean {
    return !!this.data.layout?.elements.some((el) => el.type === 'signatories');
  }

  fieldForElement(el: LayoutElement): FieldDefinition | null {
    return this.data.fieldSchema.find((f) => f.key === el.fieldKey) ?? null;
  }
}
