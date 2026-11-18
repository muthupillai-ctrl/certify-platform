import { FieldDefinition } from '../../core/models/achievement.models';
import { CertificatePreviewData, CORE_ELEMENTS, LayoutElement, LayoutElementType, SignatoryName, TemplateLayout } from '../../core/models/certificate-preview.models';
import { TemplateKind } from '../../core/models/certificate-template.models';
import { FieldValues } from '../../core/models/credential.models';

// TypeScript port of Certyfi.Infrastructure.Validation.DefaultLayouts.Generate
// — deliberately kept element-for-element identical (same fixed elements,
// same y-position increments) so "Reset to default layout" in the canvas
// editor produces the exact same starting point the backend would generate
// server-side for a template that omits LayoutJson entirely.
export function generateDefaultLayout(kind: TemplateKind, fieldSchema: FieldDefinition[]): TemplateLayout {
  const elements: LayoutElement[] = [
    { id: 'institutionName', type: 'institutionName', x: 60, y: 24, width: 580, fontSize: 11, align: 'center', color: '#6b7280' },
    { id: 'logo', type: 'logo', x: 300, y: 60, width: 100, height: 60 },
    { id: 'title', type: 'title', x: 60, y: 140, width: 580, fontSize: 32, align: 'center', color: '#111827', bold: true },
    { id: 'subtitle', type: 'subtitle', x: 60, y: 182, width: 580, fontSize: 14, align: 'center', color: '#6b7280' },
    { id: 'recipientName', type: 'recipientName', x: 60, y: 240, width: 580, fontSize: 28, align: 'center', color: '#111827' },
    { id: 'achievementTitle', type: 'achievementTitle', x: 60, y: 300, width: 580, fontSize: 18, align: 'center', color: '#111827' },
  ];

  if (kind === 'Certificate') {
    elements.push({ id: 'achievementDescription', type: 'achievementDescription', x: 110, y: 336, width: 480, fontSize: 12, align: 'center', color: '#4b5563' });
  }

  let fieldY = 380;
  for (const field of fieldSchema) {
    if (field.type === 'table') {
      elements.push({ id: `field_${field.key}`, type: 'table', fieldKey: field.key, x: 60, y: fieldY, width: 580, height: 200, fontSize: 12, color: '#111827' });
      fieldY += 220;
    } else {
      elements.push({ id: `field_${field.key}`, type: 'field', fieldKey: field.key, x: 60, y: fieldY, width: 260, fontSize: 13, align: 'left', color: '#111827' });
      fieldY += 36;
    }
  }

  // Signatories above the footer; must match DefaultLayouts.cs.
  const signatoriesY = Math.max(fieldY + 30, 470);
  elements.push({ id: 'signatories', type: 'signatories', x: 60, y: signatoriesY, width: 580, height: 70, fontSize: 12, align: 'center', color: '#111827' });
  const footerY = signatoriesY + 130;
  elements.push({ id: 'issuedDate', type: 'issuedDate', x: 60, y: footerY, width: 200, fontSize: 11, align: 'left', color: '#6b7280' });
  // 140px: the smallest slot where the offline-capable QR reliably scans (see QrCodeComponent); smaller slots fall back to the plain link.
  elements.push({ id: 'qrCode', type: 'qrCode', x: 280, y: footerY - 60, width: 140, height: 140 });
  elements.push({ id: 'credentialId', type: 'credentialId', x: 420, y: footerY, width: 220, fontSize: 10, align: 'right', color: '#6b7280' });

  return { canvasWidth: 700, canvasHeight: footerY + 120, backgroundColor: '#fdfbf5', elements };
}

// Called whenever the field schema changes while editing a template (a
// field is added/removed in FieldSchemaBuilderComponent) — keeps the
// layout in sync without discarding any positions the user has already
// dragged: existing field/table elements keep their current x/y, new
// fields get appended below the lowest existing element, and elements for
// removed fields are dropped.
export function syncLayoutWithFields(layout: TemplateLayout, fieldSchema: FieldDefinition[]): TemplateLayout {
  const validKeys = new Set(fieldSchema.map((f) => f.key));
  const elements = layout.elements.filter((el) => (el.type !== 'field' && el.type !== 'table') || (el.fieldKey && validKeys.has(el.fieldKey)));

  const representedKeys = new Set(elements.filter((el) => el.fieldKey).map((el) => el.fieldKey));
  let nextY = elements.reduce((max, el) => Math.max(max, el.y + (el.height ?? 24)), 40);

  for (const field of fieldSchema) {
    if (representedKeys.has(field.key)) continue;
    if (field.type === 'table') {
      elements.push({ id: `field_${field.key}`, type: 'table', fieldKey: field.key, x: 60, y: nextY, width: 580, height: 200, fontSize: 12, color: '#111827' });
      nextY += 220;
    } else {
      elements.push({ id: `field_${field.key}`, type: 'field', fieldKey: field.key, x: 60, y: nextY, width: 260, fontSize: 13, align: 'left', color: '#111827' });
      nextY += 36;
    }
  }

  const canvasHeight = Math.max(layout.canvasHeight, nextY + 100);
  return { ...layout, canvasHeight, elements };
}

// Sample values so the canvas editor and Template View always show
// something plausible for every field, regardless of type — text/number/
// date all just need to look reasonable, since CertificatePreviewComponent
// renders every field value as plain text (no date/number formatting).
function dummyFieldValue(type: FieldDefinition['type']): string {
  switch (type) {
    case 'number':
      return '85';
    case 'date':
      return '15 Jan 2026';
    default:
      return 'Sample text';
  }
}

function buildDummyFieldValues(fieldSchema: FieldDefinition[]): FieldValues {
  const values: FieldValues = {};
  for (const field of fieldSchema) {
    if (field.type === 'table') {
      const columns = field.columns ?? [];
      values[field.key] = [1, 2].map(() => {
        const row: Record<string, string | null> = {};
        for (const column of columns) row[column.key] = dummyFieldValue(column.type);
        return row;
      });
    } else {
      values[field.key] = dummyFieldValue(field.type);
    }
  }
  return values;
}

// The one dummy dataset shared by the canvas editor (Phase 2's live
// drag-and-drop preview) and the Template View page ("see the actual
// certificate with dummy values") — both need to render a template that
// has no real credential behind it yet.
export function buildDummyPreviewData(
  template: {
    kind: TemplateKind;
    titleText: string;
    subtitleText: string | null;
    accentColor: string | null;
    fontFamily: string | null;
    fieldSchema: FieldDefinition[];
    layout: TemplateLayout;
    backgroundImageUrl?: string | null;
    signatories?: SignatoryName[];
  },
  institution: { name: string; logoUrl: string | null; primaryColor: string | null },
): CertificatePreviewData {
  return {
    recipientName: 'Jane Doe',
    achievementTitle: 'Sample Achievement Title',
    achievementDescription: 'A short description of what this achievement recognizes.',
    institutionName: institution.name,
    institutionLogoUrl: institution.logoUrl,
    institutionPrimaryColor: institution.primaryColor,
    status: 'Active',
    issuedAt: new Date().toISOString(),
    expiresOn: null,
    fieldSchema: template.fieldSchema,
    fieldValues: buildDummyFieldValues(template.fieldSchema),
    templateKind: template.kind,
    layout: template.layout,
    titleText: template.titleText,
    subtitleText: template.subtitleText,
    accentColor: template.accentColor,
    fontFamily: template.fontFamily,
    backgroundImageUrl: template.backgroundImageUrl ?? null,
    signatories: template.signatories?.length ? template.signatories : [{ name: 'Signatory name', designation: 'Designation' }],
  };
}

// Labels of the core items (CORE_ELEMENTS) a layout doesn't show.
export function missingCoreElements(layout: TemplateLayout): { label: string; type: LayoutElementType }[] {
  const types = new Set(layout.elements.map((el) => el.type));
  return CORE_ELEMENTS.filter((c) => !c.types.some((t) => types.has(t))).map((c) => ({ label: c.label, type: c.types[0] }));
}

// Adds a missing core element below everything else (the canvas grows to
// fit); the user then drags it into place.
export function addCoreElement(layout: TemplateLayout, type: LayoutElementType): TemplateLayout {
  const bottom = layout.elements.reduce((max, el) => Math.max(max, el.y + (el.height ?? 30)), 40);
  const y = bottom + 20;
  const width = Math.min(580, layout.canvasWidth - 120);
  const x = Math.round((layout.canvasWidth - width) / 2);
  const presets: Partial<Record<LayoutElementType, Partial<LayoutElement>>> = {
    signatories: { width, height: 70, fontSize: 12, align: 'center', color: '#111827' },
    recipientName: { width, fontSize: 28, align: 'center', color: '#111827' },
    achievementTitle: { width, fontSize: 18, align: 'center', color: '#111827' },
    institutionName: { width, fontSize: 11, align: 'center', color: '#6b7280' },
    issuedDate: { width: 200, fontSize: 11, align: 'left', color: '#6b7280' },
    qrCode: { width: 140, height: 140 },
  };
  const element: LayoutElement = { id: `${type}_${Date.now()}`, type, x: type === 'issuedDate' ? 60 : type === 'qrCode' ? Math.round(layout.canvasWidth / 2 - 70) : x, y, width, ...presets[type] };
  const needed = y + (element.height ?? 40) + 40;
  return { ...layout, canvasHeight: Math.max(layout.canvasHeight, needed), elements: [...layout.elements, element] };
}
