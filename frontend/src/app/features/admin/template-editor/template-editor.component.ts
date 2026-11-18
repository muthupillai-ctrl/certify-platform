import { Component, HostListener, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';
import { CertificateTemplatesService } from '../../../core/services/certificate-templates.service';
import { TenantSettingsService } from '../../../core/services/tenant-settings.service';
import { UploadsService } from '../../../core/services/uploads.service';
import { CertificateTemplate, TemplateKind } from '../../../core/models/certificate-template.models';
import { TemplateLayout } from '../../../core/models/certificate-preview.models';
import { TenantBranding } from '../../../core/models/tenant.models';
import {
  BuilderField,
  FieldSchemaBuilderComponent,
  fromFieldDefinitions,
  toFieldDefinitions,
} from '../../../shared/field-schema-builder/field-schema-builder.component';
import { CertificateCanvasEditorComponent } from '../../../shared/certificate-canvas-editor/certificate-canvas-editor.component';
import { addCoreElement, buildDummyPreviewData, generateDefaultLayout, missingCoreElements, syncLayoutWithFields } from '../../../shared/certificate-canvas-editor/layout-generation';
import { Signatory, SignatoriesService } from '../../../core/services/signatories.service';
import { CertificateFrame, FrameStyle, LayoutElement, LayoutElementType } from '../../../core/models/certificate-preview.models';
import { ModalComponent } from '../../../shared/modal/modal.component';
import { DesignGalleryComponent } from '../../../shared/certificate-design/design-gallery.component';
import { CertificateDesign } from '../../../shared/certificate-design/certificate-designs';
import { CERTIFICATE_FONTS } from '../../../shared/certificate-design/certificate-style';

// What undo/redo restores: the design (layout, frame, background) and the
// certificate's text settings. Fields and signatories aren't part of it.
interface Snapshot {
  layout: TemplateLayout;
  titleText: string;
  subtitleText: string;
  accentColor: string;
  fontFamily: string;
  backgroundImageUrl: string;
}
// Changes closer together than this are one undo step (typing, a slider).
const MERGE_MS = 700;
const HISTORY_LIMIT = 100;

const MAX_SIGNATORIES = 3;

export type CanvasSizePreset = 'landscape' | 'portrait' | 'square' | 'custom';

// Fixed certificate frames the background image gets cropped-to-fill (CSS
// background-size: cover, already how both CertificatePreviewComponent and
// CertificateCanvasEditorComponent render the background) — 'custom' is the
// only one that instead auto-fits the canvas to the uploaded image's own
// aspect ratio, capped at 1000px wide.
const CANVAS_SIZE_PRESETS: Record<Exclude<CanvasSizePreset, 'custom'>, { width: number; height: number }> = {
  landscape: { width: 1100, height: 850 },
  portrait: { width: 850, height: 1100 },
  square: { width: 900, height: 900 },
};

// Full-page (not modal) create/amend for a CertificateTemplate — the canvas
// editor needs real screen space, which is why this moved out of
// CertificateTemplatesComponent's old inline-modal form (see that
// component's own comment). "Creating a template" IS designing the actual
// certificate: the left rail sets what fields/text/colors exist, and the
// canvas on the right is a real drag-and-drop preview of the result,
// populated with dummy data the whole time.
@Component({
  selector: 'app-template-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, FieldSchemaBuilderComponent, CertificateCanvasEditorComponent, ModalComponent, DesignGalleryComponent],
  templateUrl: './template-editor.component.html',
})
export class TemplateEditorComponent implements OnInit {
  readonly loading = signal(false);
  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly branding = signal<TenantBranding | null>(null);

  templateId: string | null = null;
  name = '';
  description = '';
  kind: TemplateKind = 'Certificate';
  fields: BuilderField[] = [];
  titleText = '';
  subtitleText = '';
  accentColor = '';
  fontFamily = '';
  backgroundImageUrl = '';
  canvasSizePreset: CanvasSizePreset = 'landscape';
  readonly backgroundImageStatus = signal<'idle' | 'checking' | 'uploading' | 'loaded' | 'error'>('idle');
  readonly backgroundImageError = signal<string | null>(null);
  readonly backgroundImageDimensions = signal<{ width: number; height: number } | null>(null);
  private backgroundImageCheckTimer: ReturnType<typeof setTimeout> | null = null;
  layout: TemplateLayout = generateDefaultLayout('Certificate', []);

  // A new template starts by choosing a design (or a blank canvas).
  readonly choosingDesign = signal(false);
  readonly designModalOpen = signal(false);
  readonly fonts = CERTIFICATE_FONTS;
  readonly frameStyles: { key: FrameStyle; label: string }[] = [
    { key: 'none', label: 'None' }, { key: 'line', label: 'Single line' }, { key: 'double', label: 'Double line' },
    { key: 'classic', label: 'Classic (thick and thin)' }, { key: 'ornate', label: 'Ornate corners' },
    { key: 'corners', label: 'Corner brackets' }, { key: 'ribbon', label: 'Ribbons top and bottom' }, { key: 'band', label: 'Side band' },
  ];
  readonly imageUploading = signal(false);

  // Undo/redo (see Snapshot).
  private past: Snapshot[] = [];
  private future: Snapshot[] = [];
  private committed: Snapshot | null = null;
  private lastChangeAt = 0;
  readonly canUndo = signal(false);
  readonly canRedo = signal(false);

  // Who signs certificates from this template, in printing order.
  readonly maxSignatories = MAX_SIGNATORIES;
  readonly allSignatories = signal<Signatory[]>([]);
  signatoryIds: string[] = [];
  signatoryToAdd = '';
  // Final approval by an institution admin (third person). Switching it off
  // goes to the other admin to confirm (see Approvals & audit).
  requiresFinalApproval = false;
  savedRequiresFinalApproval = false;
  finalApprovalOffPending = false;

  // Tracks whether the user has ever manually edited the canvas itself —
  // while untouched, it stays in sync with Kind so a brand-new template
  // always shows something sensible; the moment the user customizes it,
  // auto-sync stops so their edits are never silently overwritten. Name and
  // Title are deliberately NOT linked this way: Name is the internal label
  // staff use to find this template in the list, Title is the heading text
  // that actually appears on the certificate — conflating them meant typing
  // an internal name silently changed what recipients would see.
  private layoutTouched = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private templatesService: CertificateTemplatesService,
    private tenantSettingsService: TenantSettingsService,
    private uploadsService: UploadsService,
    private auth: AuthService,
    private signatoriesService: SignatoriesService,
  ) {}

  ngOnInit(): void {
    this.tenantSettingsService.getBranding().subscribe({ next: (b) => this.branding.set(b) });
    this.signatoriesService.list().subscribe({ next: (list) => this.allSignatories.set(list) });

    this.templateId = this.route.snapshot.paramMap.get('id');
    if (!this.templateId) {
      this.choosingDesign.set(true);
      this.resetHistory();
    }
    if (this.templateId) {
      this.loading.set(true);
      this.templatesService.list().subscribe({
        next: (templates) => {
          const found = templates.find((t) => t.id === this.templateId);
          if (found) this.loadTemplate(found);
          else this.errorMessage.set('Template not found.');
          this.loading.set(false);
        },
        error: () => {
          this.errorMessage.set('Failed to load template.');
          this.loading.set(false);
        },
      });
    }
  }

  private loadTemplate(t: CertificateTemplate): void {
    this.name = t.name;
    this.description = t.description ?? '';
    this.kind = t.kind;
    this.fields = fromFieldDefinitions(t.fieldSchema);
    this.titleText = t.titleText;
    this.subtitleText = t.subtitleText ?? '';
    this.accentColor = t.accentColor ?? '';
    this.fontFamily = t.fontFamily ?? '';
    this.backgroundImageUrl = t.backgroundImageUrl ?? '';
    this.backgroundImageStatus.set(t.backgroundImageUrl ? 'loaded' : 'idle');
    this.layout = t.layout;
    this.signatoryIds = [...t.signatoryIds];
    this.requiresFinalApproval = t.requiresFinalApproval;
    this.savedRequiresFinalApproval = t.requiresFinalApproval;
    this.finalApprovalOffPending = t.finalApprovalOffPending;
    this.canvasSizePreset = this.inferCanvasSizePreset(t.layout.canvasWidth, t.layout.canvasHeight);
    this.layoutTouched = true;
    this.resetHistory();
  }

  // ---------- designs ----------

  // A design (or null: a blank canvas) fills the template's look; its
  // fields and signatories stay as they are (a design may suggest fields
  // when there are none yet).
  applyDesign(design: CertificateDesign | null): void {
    if (design) {
      const current = toFieldDefinitions(this.fields);
      const result = design.build(current);
      if (result.fields && current.length === 0) this.fields = fromFieldDefinitions(result.fields);
      this.kind = design.kind;
      this.layout = result.layout;
      this.titleText = result.titleText;
      this.subtitleText = result.subtitleText ?? '';
      this.fontFamily = result.fontFamily;
      this.accentColor = result.accentColor;
      this.backgroundImageUrl = '';
      this.backgroundImageStatus.set('idle');
      this.canvasSizePreset = this.inferCanvasSizePreset(result.layout.canvasWidth, result.layout.canvasHeight);
      this.layoutTouched = true;
    } else if (this.choosingDesign()) {
      this.layout = generateDefaultLayout(this.kind, toFieldDefinitions(this.fields));
      this.layoutTouched = false;
    }
    if (!this.name && design) this.name = design.name;
    this.choosingDesign.set(false);
    this.designModalOpen.set(false);
    this.afterChange(true);
  }

  // ---------- undo / redo ----------

  private snapshot(): Snapshot {
    return {
      layout: this.layout, titleText: this.titleText, subtitleText: this.subtitleText,
      accentColor: this.accentColor, fontFamily: this.fontFamily, backgroundImageUrl: this.backgroundImageUrl,
    };
  }

  private restore(s: Snapshot): void {
    this.layout = s.layout;
    this.titleText = s.titleText;
    this.subtitleText = s.subtitleText;
    this.accentColor = s.accentColor;
    this.fontFamily = s.fontFamily;
    this.backgroundImageUrl = s.backgroundImageUrl;
    this.backgroundImageStatus.set(s.backgroundImageUrl ? 'loaded' : 'idle');
    this.layoutTouched = true;
  }

  private resetHistory(): void {
    this.past = [];
    this.future = [];
    this.committed = this.snapshot();
    this.syncHistory();
  }

  private syncHistory(): void {
    this.canUndo.set(this.past.length > 0);
    this.canRedo.set(this.future.length > 0);
  }

  // Call after any change to the design. Changes in quick succession (typing,
  // dragging a slider) become one undo step; separate = a new step.
  afterChange(separate = false): void {
    const now = Date.now();
    if (this.committed && (separate || now - this.lastChangeAt > MERGE_MS)) {
      this.past.push(this.committed);
      if (this.past.length > HISTORY_LIMIT) this.past.shift();
    }
    this.lastChangeAt = now;
    this.future = [];
    this.committed = this.snapshot();
    this.syncHistory();
  }

  undo(): void {
    const previous = this.past.pop();
    if (!previous) return;
    this.future.push(this.snapshot());
    this.restore(previous);
    this.committed = previous;
    this.lastChangeAt = 0;
    this.syncHistory();
  }

  redo(): void {
    const next = this.future.pop();
    if (!next) return;
    this.past.push(this.snapshot());
    this.restore(next);
    this.committed = next;
    this.lastChangeAt = 0;
    this.syncHistory();
  }

  @HostListener('document:keydown', ['$event'])
  onKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;
    if (!(event.metaKey || event.ctrlKey)) return;
    const key = event.key.toLowerCase();
    if (key === 'z' && !event.shiftKey) {
      event.preventDefault();
      this.undo();
    } else if ((key === 'z' && event.shiftKey) || key === 'y') {
      event.preventDefault();
      this.redo();
    }
  }

  // ---------- frame, background, new elements ----------

  get frame(): CertificateFrame {
    return this.layout.frame ?? { style: 'none' };
  }

  setFrame(changes: Partial<CertificateFrame>): void {
    const frame: CertificateFrame = { color: this.accentColor || '#1f2937', width: 2, inset: 24, ...this.frame, ...changes };
    this.layout = { ...this.layout, frame: frame.style === 'none' ? null : frame };
    this.afterChange();
  }

  setBackgroundColor(color: string | null): void {
    this.layout = { ...this.layout, backgroundColor: color ?? undefined };
    this.afterChange();
  }

  private addElement(el: LayoutElement): void {
    this.layout = { ...this.layout, elements: [...this.layout.elements, el] };
    this.layoutTouched = true;
    this.afterChange(true);
  }

  addText(): void {
    const width = Math.min(500, this.layout.canvasWidth - 120);
    this.addElement({ id: `text_${Date.now().toString(36)}`, type: 'text', text: 'Your text', x: Math.round((this.layout.canvasWidth - width) / 2), y: Math.round(this.layout.canvasHeight / 2), width, fontSize: 18, align: 'center', color: '#111827' });
  }

  addLine(): void {
    const width = Math.min(360, this.layout.canvasWidth - 120);
    this.addElement({ id: `line_${Date.now().toString(36)}`, type: 'line', x: Math.round((this.layout.canvasWidth - width) / 2), y: Math.round(this.layout.canvasHeight / 2), width, height: 2, color: this.accentColor || '#111827' });
  }

  addImage(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.imageUploading.set(true);
    this.errorMessage.set(null);
    this.uploadsService.uploadImage(file).subscribe({
      next: (r) => {
        this.imageUploading.set(false);
        this.addElement({ id: `image_${Date.now().toString(36)}`, type: 'image', src: r.url, x: Math.round(this.layout.canvasWidth / 2 - 70), y: Math.round(this.layout.canvasHeight / 2 - 70), width: 140, height: 140 });
      },
      error: (err) => {
        this.imageUploading.set(false);
        this.errorMessage.set(err.error?.error ?? 'Could not upload the image.');
      },
    });
  }

  private inferCanvasSizePreset(width: number, height: number): CanvasSizePreset {
    for (const [preset, size] of Object.entries(CANVAS_SIZE_PRESETS)) {
      if (size.width === width && size.height === height) return preset as CanvasSizePreset;
    }
    return 'custom';
  }

  onCanvasSizePresetChange(): void {
    if (this.backgroundImageDimensions()) this.applyCanvasSize();
  }

  // Applies the selected certificate-size preset to the canvas — fixed
  // dimensions for every preset but 'custom', which instead fits the
  // canvas to backgroundImageDimensions()'s own aspect ratio (capped at
  // 1000px wide). Presets crop-to-fill via the existing background-size:
  // cover CSS, so switching presets never touches the stored image itself.
  private applyCanvasSize(): void {
    const dims = this.backgroundImageDimensions();
    let width: number;
    let height: number;
    if (this.canvasSizePreset === 'custom') {
      if (!dims) return;
      const maxWidth = 1000;
      width = Math.min(dims.width, maxWidth);
      height = Math.round(width * (dims.height / dims.width));
    } else {
      ({ width, height } = CANVAS_SIZE_PRESETS[this.canvasSizePreset]);
    }
    this.layout = { ...this.layout, canvasWidth: width, canvasHeight: height };
    this.layoutTouched = true;
    this.afterChange(true);
  }

  onKindChange(): void {
    if (!this.layoutTouched) {
      this.layout = generateDefaultLayout(this.kind, toFieldDefinitions(this.fields));
    }
  }

  // "Import a professionally designed certificate" — the background image
  // is the whole point of the imported design, so the canvas should match
  // its real proportions rather than stretching/cropping it into whatever
  // size the layout happened to be. Debounced on every keystroke (typing
  // OR pasting both fire (input)) rather than gated on blur — a silent
  // blur-only check gave no feedback at all while the field still had
  // focus, which is exactly what looked like "nothing happens" when
  // pasting a URL. backgroundImageStatus/backgroundImageDimensions make
  // the check's outcome visible in the form itself, not just as a canvas
  // resize the user might not even be able to see at the same time.
  onBackgroundImageUrlInput(): void {
    if (this.backgroundImageCheckTimer) clearTimeout(this.backgroundImageCheckTimer);
    this.backgroundImageError.set(null);
    this.backgroundImageDimensions.set(null);

    const url = this.backgroundImageUrl.trim();
    if (!url) {
      this.backgroundImageStatus.set('idle');
      return;
    }
    if (!/^https?:\/\//.test(url)) {
      this.backgroundImageStatus.set('error');
      this.backgroundImageError.set('Must be an http:// or https:// URL.');
      return;
    }

    this.backgroundImageStatus.set('checking');
    this.backgroundImageCheckTimer = setTimeout(() => this.checkBackgroundImage(url), 500);
  }

  private checkBackgroundImage(url: string): void {
    const img = new Image();
    img.onload = () => {
      if (this.backgroundImageUrl.trim() !== url) return; // superseded by a newer edit
      this.backgroundImageStatus.set('loaded');
      this.backgroundImageDimensions.set({ width: img.naturalWidth, height: img.naturalHeight });
      this.applyCanvasSize();
    };
    img.onerror = () => {
      if (this.backgroundImageUrl.trim() !== url) return;
      this.backgroundImageStatus.set('error');
      this.backgroundImageError.set("Couldn't load an image from that URL — check it points directly to an image file, not a web page.");
    };
    img.src = url;
  }

  // Direct upload — added after the URL-paste path turned out impractical
  // in practice: finding an already-hosted direct image link for a locally
  // designed certificate background is its own hurdle. Reads the file's
  // own dimensions locally (no need to wait on the upload just to size the
  // canvas) while the actual upload happens in the background; the result
  // becomes an ordinary backgroundImageUrl, so everything downstream
  // (saving, rendering, snapshotting onto achievements) is unchanged.
  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    if (this.backgroundImageCheckTimer) clearTimeout(this.backgroundImageCheckTimer);
    this.backgroundImageError.set(null);
    this.backgroundImageDimensions.set(null);
    this.backgroundImageStatus.set('uploading');

    const objectUrl = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      this.backgroundImageDimensions.set({ width: img.naturalWidth, height: img.naturalHeight });
      this.applyCanvasSize();
      URL.revokeObjectURL(objectUrl);
    };
    img.src = objectUrl;

    this.uploadsService.uploadBackgroundImage(file).subscribe({
      next: (res) => {
        this.backgroundImageUrl = res.url;
        this.backgroundImageStatus.set('loaded');
        this.afterChange(true);
      },
      error: (err) => {
        this.backgroundImageStatus.set('error');
        this.backgroundImageError.set(err.error?.error ?? 'Upload failed.');
      },
    });

    input.value = '';
  }

  onFieldsChange(): void {
    this.layout = syncLayoutWithFields(this.layout, toFieldDefinitions(this.fields));
    this.afterChange();
  }

  onLayoutChange(layout: TemplateLayout): void {
    this.layout = layout;
    this.layoutTouched = true;
    this.afterChange();
  }

  resetLayout(): void {
    this.layout = generateDefaultLayout(this.kind, toFieldDefinitions(this.fields));
    this.layoutTouched = false;
    this.afterChange(true);
  }

  // The gallery's inputs, fixed while it's open (so thumbnails draw once).
  readonly galleryFields = signal<ReturnType<typeof toFieldDefinitions>>([]);
  readonly galleryInstitution = computed(() => ({
    name: this.auth.currentUser()?.tenant_slug ?? 'Your Institution',
    logoUrl: this.branding()?.logoUrl ?? null,
    primaryColor: this.branding()?.primaryColor ?? null,
  }));

  openDesigns(): void {
    this.galleryFields.set(toFieldDefinitions(this.fields));
    this.designModalOpen.set(true);
  }

  chosenSignatories(): Signatory[] {
    return this.signatoryIds.map((id) => this.allSignatories().find((s) => s.id === id)).filter((s): s is Signatory => !!s);
  }

  availableSignatories(): Signatory[] {
    return this.allSignatories().filter((s) => !this.signatoryIds.includes(s.id));
  }

  addSignatory(): void {
    if (this.signatoryToAdd && this.signatoryIds.length < MAX_SIGNATORIES) this.signatoryIds = [...this.signatoryIds, this.signatoryToAdd];
    this.signatoryToAdd = '';
  }

  removeSignatory(id: string): void {
    this.signatoryIds = this.signatoryIds.filter((x) => x !== id);
  }

  moveSignatory(index: number, by: -1 | 1): void {
    const ids = [...this.signatoryIds];
    const j = index + by;
    if (j < 0 || j >= ids.length) return;
    [ids[index], ids[j]] = [ids[j], ids[index]];
    this.signatoryIds = ids;
  }

  // Core items the design doesn't show yet (saving needs all of them).
  missingCore(): { label: string; type: LayoutElementType }[] {
    return missingCoreElements(this.layout);
  }

  addMissing(type: LayoutElementType): void {
    this.layout = addCoreElement(this.layout, type);
    this.layoutTouched = true;
    this.afterChange(true);
  }

  canSave(): boolean {
    return this.signatoryIds.length > 0 && this.missingCore().length === 0;
  }

  previewData() {
    return buildDummyPreviewData(
      {
        kind: this.kind,
        titleText: this.titleText,
        subtitleText: this.subtitleText || null,
        accentColor: this.accentColor || null,
        fontFamily: this.fontFamily || null,
        fieldSchema: toFieldDefinitions(this.fields),
        layout: this.layout,
        backgroundImageUrl: this.backgroundImageUrl || null,
        signatories: this.chosenSignatories().map((s) => ({ name: s.name, designation: s.designation })),
      },
      {
        name: this.auth.currentUser()?.tenant_slug ?? '',
        logoUrl: this.branding()?.logoUrl ?? null,
        primaryColor: this.branding()?.primaryColor ?? null,
      },
    );
  }

  submit(): void {
    this.errorMessage.set(null);
    this.submitting.set(true);

    const request = {
      name: this.name,
      description: this.description || null,
      kind: this.kind,
      fieldSchema: toFieldDefinitions(this.fields),
      // Left blank on purpose is allowed — the backend defaults an empty
      // Title to this template's Name at save time (see
      // CertificateTemplatesController.TryResolveVisualDesign) as a last-
      // resort safety net, but the editor itself never silently mirrors
      // Name into Title while you're typing.
      titleText: this.titleText || undefined,
      subtitleText: this.subtitleText || null,
      accentColor: this.accentColor || null,
      fontFamily: this.fontFamily || null,
      layout: this.layout,
      backgroundImageUrl: this.backgroundImageUrl || null,
      signatoryIds: this.signatoryIds,
      requiresFinalApproval: this.requiresFinalApproval,
    };

    const call = this.templateId ? this.templatesService.update(this.templateId, request) : this.templatesService.create(request);

    const askedToTurnOff = this.savedRequiresFinalApproval && !this.requiresFinalApproval;
    call.subscribe({
      next: () => {
        this.submitting.set(false);
        this.router.navigate(['/admin/templates'], askedToTurnOff ? { queryParams: { finalApprovalOff: 'asked' } } : undefined);
      },
      error: (err) => {
        this.submitting.set(false);
        this.errorMessage.set(err.error?.error ?? `Failed to ${this.templateId ? 'amend' : 'create'} certificate template.`);
      },
    });
  }
}
