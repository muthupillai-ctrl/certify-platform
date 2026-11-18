import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CdkDragEnd, CdkDragMove, DragDropModule } from '@angular/cdk/drag-drop';
import { CertificatePreviewData, LayoutAlign, LayoutElement, LineStyle, TemplateLayout } from '../../core/models/certificate-preview.models';
import { FieldDefinition } from '../../core/models/achievement.models';
import { QrCodeComponent } from '../qr-code/qr-code.component';
import { CertificateFrameComponent } from '../certificate-design/certificate-frame.component';
import { CERTIFICATE_FONTS, hasFont, lineStyle, typeStyle } from '../certificate-design/certificate-style';
import { UploadsService } from '../../core/services/uploads.service';

// Within this many canvas pixels of a guide, a dragged element snaps to it.
const SNAP = 6;

const TYPE_LABELS: Record<string, string> = {
  title: 'Title', subtitle: 'Subtitle', institutionName: 'Institution name', logo: 'Logo', recipientName: 'Recipient name',
  achievementTitle: 'Certificate title', achievementDescription: 'Description', field: 'Field', table: 'Table',
  issuedDate: 'Issue date', expiryDate: 'Expiry date', qrCode: 'QR code', credentialId: 'Certificate code',
  signatories: 'Signatories', text: 'Text', line: 'Line', image: 'Image',
};

// The interactive counterpart to CertificatePreviewComponent's positioned
// rendering: same element types and styles (certificate-style.ts), but each
// element can be dragged, selected and edited. Every change produces a new
// layout object (never edits the old one), so the template editor can keep
// an undo history. Keyboard: arrows nudge (Shift: 10px), Delete removes,
// Escape deselects; undo/redo is the template editor's.
@Component({
  selector: 'app-certificate-canvas-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, DragDropModule, QrCodeComponent, CertificateFrameComponent],
  templateUrl: './certificate-canvas-editor.component.html',
})
export class CertificateCanvasEditorComponent {
  @Input({ required: true }) layout!: TemplateLayout;
  @Input({ required: true }) previewData!: CertificatePreviewData;
  @Output() layoutChange = new EventEmitter<TemplateLayout>();
  @ViewChild('viewport') viewport?: ElementRef<HTMLElement>;
  @ViewChild('imageInput') imageInput?: ElementRef<HTMLInputElement>;

  selectedElementId: string | null = null;
  readonly aligns: LayoutAlign[] = ['left', 'center', 'right'];
  readonly fonts = CERTIFICATE_FONTS;
  readonly lineStyles: LineStyle[] = ['solid', 'dashed', 'dotted', 'double'];
  readonly zoom = signal(1);
  // Guides shown while dragging (canvas pixels), null when none.
  readonly guideX = signal<number | null>(null);
  readonly guideY = signal<number | null>(null);
  readonly uploading = signal(false);
  readonly uploadError = signal<string | null>(null);

  constructor(private uploads: UploadsService) {}

  get selectedElement(): LayoutElement | null {
    return this.layout.elements.find((el) => el.id === this.selectedElementId) ?? null;
  }

  label(el: LayoutElement): string {
    if (el.type === 'field' || el.type === 'table') return `${TYPE_LABELS[el.type]}: ${this.fieldForElement(el)?.label ?? el.fieldKey}`;
    return TYPE_LABELS[el.type] ?? el.type;
  }

  isTextual(el: LayoutElement): boolean {
    return !['logo', 'qrCode', 'image', 'line'].includes(el.type);
  }

  selectElement(el: LayoutElement): void {
    this.selectedElementId = el.id;
  }

  clearSelection(): void {
    this.selectedElementId = null;
  }

  // ---------- changes (always a new layout) ----------

  private emit(layout: TemplateLayout): void {
    this.layout = layout;
    this.layoutChange.emit(layout);
  }

  private patch(id: string, changes: Partial<LayoutElement>): void {
    this.emit({ ...this.layout, elements: this.layout.elements.map((e) => (e.id === id ? { ...e, ...changes } : e)) });
  }

  updateSelected(changes: Partial<LayoutElement>): void {
    if (this.selectedElementId) this.patch(this.selectedElementId, changes);
  }

  setAlign(align: LayoutAlign): void {
    this.updateSelected({ align });
  }

  // Deleting is layout-only: a field stays defined in Fields, it just isn't drawn.
  deleteSelected(): void {
    const id = this.selectedElementId;
    if (!id) return;
    this.selectedElementId = null;
    this.emit({ ...this.layout, elements: this.layout.elements.filter((e) => e.id !== id) });
  }

  duplicateSelected(): void {
    const el = this.selectedElement;
    if (!el) return;
    const copy: LayoutElement = { ...el, id: `${el.type}_${Date.now().toString(36)}`, x: el.x + 16, y: el.y + 16, locked: false };
    const at = this.layout.elements.indexOf(el);
    const elements = [...this.layout.elements];
    elements.splice(at + 1, 0, copy);
    this.emit({ ...this.layout, elements });
    this.selectedElementId = copy.id;
  }

  // Later in the list = drawn on top.
  moveLayer(to: 'front' | 'forward' | 'backward' | 'back'): void {
    const el = this.selectedElement;
    if (!el) return;
    const elements = this.layout.elements.filter((e) => e !== el);
    const at = this.layout.elements.indexOf(el);
    const index = { front: elements.length, back: 0, forward: Math.min(at + 1, elements.length), backward: Math.max(at - 1, 0) }[to];
    elements.splice(index, 0, el);
    this.emit({ ...this.layout, elements });
  }

  // A line turns between horizontal and vertical (its box's sides swap).
  turnLine(): void {
    const el = this.selectedElement;
    if (el?.type === 'line') this.updateSelected({ width: el.height ?? 2, height: el.width });
  }

  lineThickness(el: LayoutElement): number {
    const h = el.height ?? 2;
    return h > el.width ? el.width : h;
  }

  setLineThickness(el: LayoutElement, value: number): void {
    const t = Math.max(1, Math.min(40, Number(value) || 1));
    const vertical = (el.height ?? 2) > el.width;
    this.updateSelected(vertical ? { width: t } : { height: t });
  }

  replaceImage(): void {
    this.imageInput?.nativeElement.click();
  }

  onImageChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    const id = this.selectedElementId;
    if (!file || !id) return;
    this.uploading.set(true);
    this.uploadError.set(null);
    this.uploads.uploadImage(file).subscribe({
      next: (r) => {
        this.uploading.set(false);
        this.patch(id, { src: r.url });
      },
      error: (err) => {
        this.uploading.set(false);
        this.uploadError.set(err.error?.error ?? 'Could not upload the image.');
      },
    });
  }

  // ---------- dragging, guides and snapping ----------

  onDragMoved(el: LayoutElement, event: CdkDragMove): void {
    const box = event.source.element.nativeElement;
    const { x, y } = event.source.getFreeDragPosition();
    const snapped = this.snap(el, x, y, box.offsetWidth, box.offsetHeight);
    this.guideX.set(snapped.guideX);
    this.guideY.set(snapped.guideY);
  }

  onDragEnded(el: LayoutElement, event: CdkDragEnd): void {
    const box = event.source.element.nativeElement;
    const pos = event.source.getFreeDragPosition();
    const snapped = this.snap(el, pos.x, pos.y, box.offsetWidth, box.offsetHeight);
    this.guideX.set(null);
    this.guideY.set(null);
    this.patch(el.id, { x: Math.round(snapped.x), y: Math.round(snapped.y) });
  }

  // Snaps the element's centre or edges to the canvas centre lines and to
  // other elements' centres and edges.
  private snap(el: LayoutElement, x: number, y: number, w: number, h: number) {
    const xs = [this.layout.canvasWidth / 2];
    const ys = [this.layout.canvasHeight / 2];
    for (const o of this.layout.elements) {
      if (o.id === el.id) continue;
      xs.push(o.x, o.x + o.width / 2, o.x + o.width);
      if (o.height) ys.push(o.y, o.y + o.height / 2, o.y + o.height);
      else ys.push(o.y);
    }
    let guideX: number | null = null;
    let guideY: number | null = null;
    const tolerance = SNAP / this.zoom();
    for (const [offset, edge] of [[w / 2, 'c'], [0, 'l'], [w, 'r']] as const) {
      const hit = xs.find((g) => Math.abs(x + offset - g) <= tolerance);
      if (hit !== undefined) {
        x = hit - offset;
        guideX = hit;
        break;
      }
      void edge;
    }
    for (const offset of [h / 2, 0, h]) {
      const hit = ys.find((g) => Math.abs(y + offset - g) <= tolerance);
      if (hit !== undefined) {
        y = hit - offset;
        guideY = hit;
        break;
      }
    }
    return { x, y, guideX, guideY };
  }

  // ---------- keyboard ----------

  @HostListener('document:keydown', ['$event'])
  onKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (!this.selectedElementId || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.isContentEditable) return;
    const el = this.selectedElement;
    if (!el) return;
    const step = event.shiftKey ? 10 : 1;
    const moves: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    if (moves[event.key]) {
      event.preventDefault();
      if (el.locked) return;
      const [dx, dy] = moves[event.key];
      this.patch(el.id, { x: el.x + dx, y: el.y + dy });
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      this.deleteSelected();
    } else if (event.key === 'Escape') {
      this.clearSelection();
    }
  }

  // ---------- zoom ----------

  zoomBy(delta: number): void {
    this.zoom.set(Math.min(2, Math.max(0.25, Math.round((this.zoom() + delta) * 100) / 100)));
  }

  zoomToFit(): void {
    const width = this.viewport?.nativeElement.clientWidth ?? this.layout.canvasWidth;
    this.zoom.set(Math.min(1.5, Math.max(0.25, Math.floor(((width - 48) / this.layout.canvasWidth) * 100) / 100)));
  }

  // ---------- drawing helpers (same rules as CertificatePreviewComponent) ----------

  fieldValue(key: string): string {
    const value = this.previewData.fieldValues[key];
    return typeof value === 'string' ? value : '';
  }

  tableRows(key: string): Array<Record<string, string | null>> {
    const value = this.previewData.fieldValues[key];
    return Array.isArray(value) ? value : [];
  }

  fieldForElement(el: LayoutElement): FieldDefinition | null {
    return this.previewData.fieldSchema.find((f) => f.key === el.fieldKey) ?? null;
  }

  // Position comes from cdkDragFreeDragPosition's transform; this only sizes the box.
  elementBoxStyle(el: LayoutElement): Record<string, string> {
    const style: Record<string, string> = { position: 'absolute', top: '0', left: '0', width: `${el.width}px` };
    if (el.height !== undefined) style['height'] = `${el.height}px`;
    return style;
  }

  elementTextStyle(el: LayoutElement): Record<string, string> {
    return typeStyle(el, this.previewData.fontFamily);
  }

  hasFont(el: LayoutElement): boolean {
    return hasFont(el, this.previewData.fontFamily);
  }

  lineStyle(el: LayoutElement): Record<string, string> {
    return lineStyle(el);
  }
}
