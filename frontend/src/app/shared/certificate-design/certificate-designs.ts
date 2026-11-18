import { FieldDefinition, TemplateKind } from '../../core/models/achievement.models';
import { CertificateFrame, FontKey, LayoutElement, TemplateLayout } from '../../core/models/certificate-preview.models';

// Ready-made certificate designs (the gallery in the template editor).
// Each builds a complete, editable layout for the template's own fields:
// every core item (LayoutValidator.CoreElements) is included, signatories
// too. Picking one fills the template; nothing else depends on it later.
export interface CertificateDesign {
  id: string;
  name: string;
  description: string;
  kind: TemplateKind;
  build(fields: FieldDefinition[]): DesignResult;
}

export interface DesignResult {
  layout: TemplateLayout;
  titleText: string;
  subtitleText: string | null;
  fontFamily: FontKey;
  accentColor: string;
  // Suggested fields, used only when the template has none yet.
  fields?: FieldDefinition[];
}

let seq = 0;
function el(type: LayoutElement['type'], x: number, y: number, width: number, extra: Partial<LayoutElement> = {}): LayoutElement {
  return { id: `${type}_${(seq++).toString(36)}`, type, x, y, width, ...extra };
}

// Simple fields in rows of up to three, centred; tables full width below.
function fieldBlock(fields: FieldDefinition[], top: number, left: number, width: number, color: string, labelAlign: 'center' | 'left'): { elements: LayoutElement[]; bottom: number } {
  const elements: LayoutElement[] = [];
  const simple = fields.filter((f) => f.type !== 'table');
  const tables = fields.filter((f) => f.type === 'table');
  let y = top;
  const perRow = Math.min(3, Math.max(1, simple.length));
  const colWidth = Math.floor(width / perRow);
  simple.forEach((f, i) => {
    const row = Math.floor(i / perRow);
    const col = i % perRow;
    const inRow = Math.min(perRow, simple.length - row * perRow);
    const rowLeft = labelAlign === 'center' ? left + Math.floor((width - inRow * colWidth) / 2) : left;
    elements.push(el('field', rowLeft + col * colWidth, top + row * 46, colWidth - 16, { fieldKey: f.key, fontSize: 14, align: labelAlign, color }));
  });
  if (simple.length) y = top + Math.ceil(simple.length / perRow) * 46 + 8;
  for (const t of tables) {
    elements.push(el('table', left, y, width, { fieldKey: t.key, height: 170, fontSize: 12, color }));
    y += 186;
  }
  return { elements, bottom: y };
}

interface CenteredStyle {
  background: string;
  frame: CertificateFrame;
  font: FontKey;
  accent: string;
  ink: string;
  muted: string;
  title: string;
  titleFont: FontKey;
  titleSize: number;
  titleUpper?: boolean;
  titleSpacing?: number;
  instFont: FontKey;
  intro: string;
  middle: string;
  nameFont: FontKey;
  nameSize: number;
  nameColor: string;
  // Short rules beside the intro line.
  flourish?: boolean;
}

// The traditional layout: everything centred down the page.
function centered(s: CenteredStyle, fields: FieldDefinition[]): DesignResult {
  const W = 1100;
  const cx = (w: number) => Math.round((W - w) / 2);
  const elements: LayoutElement[] = [
    el('logo', cx(110), 74, 110, { height: 64 }),
    el('institutionName', cx(820), 150, 820, { fontSize: 15, align: 'center', color: s.ink, fontFamily: s.instFont, uppercase: true, letterSpacing: 3 }),
    el('title', cx(860), 182, 860, { fontSize: s.titleSize, align: 'center', color: s.accent, fontFamily: s.titleFont, bold: true, uppercase: s.titleUpper, letterSpacing: s.titleSpacing }),
    el('text', cx(600), 262, 600, { text: s.intro, fontSize: 17, align: 'center', color: s.muted, italic: true }),
    el('recipientName', cx(860), 292, 860, { fontSize: s.nameSize, align: 'center', color: s.nameColor, fontFamily: s.nameFont }),
    el('line', cx(420), 376, 420, { height: 2, color: s.accent }),
    el('text', cx(600), 392, 600, { text: s.middle, fontSize: 17, align: 'center', color: s.muted, italic: true }),
    el('achievementTitle', cx(820), 424, 820, { fontSize: 26, align: 'center', color: s.ink, bold: true }),
    el('achievementDescription', cx(680), 466, 680, { fontSize: 14, align: 'center', color: s.muted }),
  ];
  if (s.flourish) {
    elements.push(el('line', 300, 274, 120, { height: 1, color: s.accent }), el('line', 680, 274, 120, { height: 1, color: s.accent }));
  }
  const block = fieldBlock(fields, 512, 160, 780, s.ink, 'center');
  elements.push(...block.elements);
  const sigY = Math.max(block.bottom + 12, 590);
  elements.push(el('signatories', cx(760), sigY, 760, { height: 74, fontSize: 14, align: 'center', color: s.ink }));
  const footY = sigY + 104;
  elements.push(
    el('issuedDate', 110, footY + 8, 240, { fontSize: 13, align: 'left', color: s.ink }),
    el('credentialId', 110, footY + 54, 300, { fontSize: 11, align: 'left', color: s.muted }),
    el('qrCode', W - 110 - 104, footY - 6, 104, { height: 104 }),
  );
  const layout: TemplateLayout = { canvasWidth: W, canvasHeight: Math.max(850, footY + 150), backgroundColor: s.background, frame: s.frame, elements };
  return { layout, titleText: s.title, subtitleText: null, fontFamily: s.font, accentColor: s.accent };
}

export const CERTIFICATE_DESIGNS: CertificateDesign[] = [
  {
    id: 'classic', name: 'Classic', kind: 'Certificate',
    description: 'Engraved capitals, a script name and a navy double rule. Formal and timeless.',
    build: (fields) => centered({
      background: '#fdfaf2', frame: { style: 'classic', color: '#1e3a5f', width: 2, inset: 28 }, font: 'garamond',
      accent: '#1e3a5f', ink: '#1f2937', muted: '#4b5563', title: 'Certificate of Achievement', titleFont: 'cinzel', titleSize: 46,
      instFont: 'garamond', intro: 'This is to certify that', middle: 'has successfully completed',
      nameFont: 'great-vibes', nameSize: 62, nameColor: '#1e3a5f',
    }, fields),
  },
  {
    id: 'elegant', name: 'Elegant', kind: 'Certificate',
    description: 'Gold ornamental corners, Playfair headings and a flowing script name.',
    build: (fields) => centered({
      background: '#fffdf7', frame: { style: 'ornate', color: '#b08d3c', width: 2, inset: 22 }, font: 'playfair',
      accent: '#9a7b2f', ink: '#2b2b2b', muted: '#5f5a4f', title: 'Certificate of Achievement', titleFont: 'playfair', titleSize: 48,
      instFont: 'cinzel', intro: 'proudly presented to', middle: 'in recognition of completing',
      nameFont: 'pinyon', nameSize: 64, nameColor: '#2b2b2b', flourish: true,
    }, fields),
  },
  {
    id: 'modern', name: 'Modern', kind: 'Certificate',
    description: 'A bold side band, left-aligned type and clean sans-serif. Good for tech and corporate.',
    build: (fields) => {
      const accent = '#0f766e';
      const elements: LayoutElement[] = [
        el('logo', 140, 70, 120, { height: 56 }),
        el('institutionName', 140, 140, 700, { fontSize: 14, color: '#475569', uppercase: true, letterSpacing: 3, fontFamily: 'montserrat' }),
        el('title', 140, 178, 820, { fontSize: 44, color: '#0f172a', bold: true, uppercase: true, letterSpacing: 4, fontFamily: 'montserrat' }),
        el('line', 140, 246, 90, { height: 6, color: accent }),
        el('text', 140, 282, 600, { text: 'Awarded to', fontSize: 16, color: '#64748b', uppercase: true, letterSpacing: 2 }),
        el('recipientName', 140, 308, 860, { fontSize: 52, color: '#0f172a', bold: true, fontFamily: 'raleway' }),
        el('text', 140, 386, 600, { text: 'for successfully completing', fontSize: 16, color: '#64748b' }),
        el('achievementTitle', 140, 414, 860, { fontSize: 26, color: accent, bold: true }),
        el('achievementDescription', 140, 456, 760, { fontSize: 14, color: '#475569' }),
      ];
      const block = fieldBlock(fields, 500, 140, 780, '#0f172a', 'left');
      elements.push(...block.elements);
      const sigY = Math.max(block.bottom + 12, 600);
      elements.push(el('signatories', 140, sigY, 640, { height: 74, fontSize: 14, align: 'left', color: '#0f172a' }));
      const footY = sigY + 104;
      elements.push(
        el('issuedDate', 140, footY + 8, 240, { fontSize: 13, color: '#0f172a' }),
        el('credentialId', 400, footY + 8, 320, { fontSize: 11, color: '#64748b' }),
        el('qrCode', 1100 - 90 - 104, footY - 30, 104, { height: 104 }),
      );
      return {
        layout: { canvasWidth: 1100, canvasHeight: Math.max(850, footY + 140), backgroundColor: '#ffffff', frame: { style: 'band', color: accent, width: 4, inset: 20 }, elements },
        titleText: 'Certificate', subtitleText: null, fontFamily: 'lato', accentColor: accent,
      };
    },
  },
  {
    id: 'minimal', name: 'Minimal', kind: 'Certificate',
    description: 'A single fine line, generous space and spaced capitals. Quiet and modern.',
    build: (fields) => centered({
      background: '#ffffff', frame: { style: 'line', color: '#111827', width: 1, inset: 40 }, font: 'raleway',
      accent: '#111827', ink: '#111827', muted: '#6b7280', title: 'Certificate', titleFont: 'raleway', titleSize: 34, titleUpper: true, titleSpacing: 10,
      instFont: 'raleway', intro: 'presented to', middle: 'for completing',
      nameFont: 'cormorant', nameSize: 50, nameColor: '#111827',
    }, fields),
  },
  {
    id: 'training', name: 'Training', kind: 'Certificate',
    description: 'Blue corner brackets and friendly sans-serif type. Made for courses and workshops.',
    build: (fields) => centered({
      background: '#f8fafc', frame: { style: 'corners', color: '#2563eb', width: 3, inset: 30 }, font: 'lato',
      accent: '#1d4ed8', ink: '#0f172a', muted: '#475569', title: 'Certificate of Completion', titleFont: 'montserrat', titleSize: 42,
      instFont: 'montserrat', intro: 'This certifies that', middle: 'has completed the training',
      nameFont: 'montserrat', nameSize: 50, nameColor: '#0f172a',
    }, fields),
  },
  {
    id: 'award', name: 'Award', kind: 'Certificate',
    description: 'Deep red ribbons top and bottom with gold script. For prizes and merit awards.',
    build: (fields) => centered({
      background: '#fffaf3', frame: { style: 'ribbon', color: '#7a2331', width: 3, inset: 8 }, font: 'cormorant',
      accent: '#7a2331', ink: '#2a1a1c', muted: '#5b4a4c', title: 'Certificate of Excellence', titleFont: 'cinzel', titleSize: 46,
      instFont: 'cinzel', intro: 'is hereby awarded to', middle: 'in recognition of outstanding performance in',
      nameFont: 'great-vibes', nameSize: 64, nameColor: '#a0782b', flourish: true,
    }, fields),
  },
  {
    id: 'participation', name: 'Participation', kind: 'Certificate',
    description: 'A soft slate double border and Cormorant type. For events, seminars and competitions.',
    build: (fields) => centered({
      background: '#ffffff', frame: { style: 'double', color: '#475569', width: 2, inset: 26 }, font: 'cormorant',
      accent: '#334155', ink: '#1e293b', muted: '#475569', title: 'Certificate of Participation', titleFont: 'cormorant', titleSize: 50,
      instFont: 'cormorant', intro: 'This is to certify that', middle: 'participated in',
      nameFont: 'playfair', nameSize: 48, nameColor: '#1e293b',
    }, fields),
  },
  {
    id: 'transcript', name: 'Academic transcript', kind: 'MarksCard',
    description: 'Portrait statement of marks with a subjects table, student details and signatories.',
    build: (fields) => {
      const suggested: FieldDefinition[] = [
        { key: 'rollNumber', label: 'Roll Number', type: 'text', required: true },
        { key: 'program', label: 'Programme', type: 'text' },
        { key: 'session', label: 'Session', type: 'text' },
        { key: 'subjects', label: 'Subjects', type: 'table', columns: [
          { key: 'subject', label: 'Subject', type: 'text' },
          { key: 'maxMarks', label: 'Max marks', type: 'number' },
          { key: 'marks', label: 'Marks obtained', type: 'number' },
          { key: 'grade', label: 'Grade', type: 'text' },
        ] },
      ];
      const use = fields.length ? fields : suggested;
      const W = 850;
      const ink = '#111827';
      const elements: LayoutElement[] = [
        el('logo', 70, 64, 90, { height: 70 }),
        el('institutionName', 180, 72, 600, { fontSize: 20, color: ink, bold: true, uppercase: true, letterSpacing: 1, fontFamily: 'merriweather' }),
        el('text', 180, 104, 600, { text: 'Office of the Controller of Examinations', fontSize: 13, color: '#4b5563', italic: true }),
        el('line', 70, 148, W - 140, { height: 2, color: '#1f2937' }),
        el('title', 70, 166, W - 140, { fontSize: 26, color: ink, bold: true, align: 'center', uppercase: true, letterSpacing: 3 }),
        el('achievementTitle', 70, 206, W - 140, { fontSize: 16, color: '#374151', align: 'center' }),
        el('text', 70, 250, 120, { text: 'Name', fontSize: 13, color: '#6b7280' }),
        el('recipientName', 190, 244, W - 260, { fontSize: 22, color: ink, bold: true }),
      ];
      const block = fieldBlock(use, 292, 70, W - 140, ink, 'left');
      elements.push(...block.elements);
      const sigY = Math.max(block.bottom + 30, 820);
      elements.push(
        el('signatories', 70, sigY, W - 140, { height: 74, fontSize: 13, align: 'center', color: ink }),
        el('issuedDate', 70, sigY + 112, 260, { fontSize: 12, color: ink }),
        el('credentialId', 70, sigY + 156, 320, { fontSize: 11, color: '#6b7280' }),
        el('qrCode', W - 70 - 100, sigY + 100, 100, { height: 100 }),
      );
      return {
        layout: { canvasWidth: W, canvasHeight: Math.max(1100, sigY + 240), backgroundColor: '#ffffff', frame: { style: 'double', color: '#1f2937', width: 1.5, inset: 30 }, elements },
        titleText: 'Statement of Marks', subtitleText: null, fontFamily: 'merriweather', accentColor: '#1f2937',
        fields: fields.length ? undefined : suggested,
      };
    },
  },
];
