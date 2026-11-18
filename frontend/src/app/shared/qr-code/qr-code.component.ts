import { AfterViewInit, Component, ElementRef, Input, OnChanges, ViewChild } from '@angular/core';
import * as QRCode from 'qrcode';

// Generated entirely client-side — no network call to a third-party QR API,
// so the (public, but not otherwise advertised) verification URL never
// leaves the browser just to render a code for it.
//
// Drawn module by module onto a high-resolution canvas (at least 4 device
// pixels per CSS pixel, whole pixels per module) and shown at exactly
// `size` with nearest-neighbour scaling, so edges stay sharp on Retina
// screens, in the PDF and in print — the old 1x canvas was stretched and
// blurred. A certificate QR carrying the signed offline summary is ~300
// characters (61–69 modules), so:
//   - long data uses error correction L (7%) instead of M — 61 modules
//     rather than 69; short links keep M;
//   - below 2 CSS px per module, `fallbackData` (the plain verification
//     link, 37 modules) is drawn instead: it verifies online, only the
//     offline check is lost. Threshold from decode tests (jsQR) of this
//     exact rendering seen by a camera at 1.2x its size: the offline code
//     decodes from 140px up, not at 120px. For scanning from a screen, the
//     verify page's "Show QR code" draws it large.
@Component({
  selector: 'app-qr-code',
  standalone: true,
  template: `<canvas #canvas role="img" [attr.aria-label]="label" style="image-rendering: pixelated"></canvas>`,
})
export class QrCodeComponent implements AfterViewInit, OnChanges {
  @Input({ required: true }) data!: string;
  @Input() size = 160;
  // Shorter data to use when `data` would be too dense to scan at `size`.
  @Input() fallbackData: string | null = null;
  @Input() label = 'QR code to verify this certificate';

  private static readonly MinCssPxPerModule = 2;
  private static readonly MinDevicePxPerCssPx = 4;
  private static readonly QuietZoneModules = 2;

  @ViewChild('canvas') private canvasRef!: ElementRef<HTMLCanvasElement>;
  private viewReady = false;

  ngAfterViewInit(): void {
    this.viewReady = true;
    this.render();
  }

  ngOnChanges(): void {
    if (this.viewReady) this.render();
  }

  private render(): void {
    if (!this.data) return;
    try {
      let qr = this.create(this.data);
      const quiet = QrCodeComponent.QuietZoneModules;
      if (this.fallbackData && this.size / (qr.modules.size + 2 * quiet) < QrCodeComponent.MinCssPxPerModule) {
        qr = this.create(this.fallbackData);
      }

      const count = qr.modules.size;
      const total = count + 2 * quiet;
      const scale = Math.max(QrCodeComponent.MinDevicePxPerCssPx, window.devicePixelRatio || 1);
      const cell = Math.max(1, Math.ceil((this.size * scale) / total));
      const canvas = this.canvasRef.nativeElement;
      canvas.width = canvas.height = cell * total;
      canvas.style.width = canvas.style.height = `${this.size}px`;

      const ctx = canvas.getContext('2d')!;
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = '#000000';
      for (let row = 0; row < count; row++) {
        for (let col = 0; col < count; col++) {
          if (qr.modules.get(row, col)) ctx.fillRect((col + quiet) * cell, (row + quiet) * cell, cell, cell);
        }
      }
    } catch {
      /* Malformed data would only come from a bug elsewhere — nothing useful to show the user here. */
    }
  }

  private create(text: string): QRCode.QRCode {
    return QRCode.create(text, { errorCorrectionLevel: text.length > 120 ? 'L' : 'M' });
  }
}
