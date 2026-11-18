import { FontKey, LayoutElement } from '../../core/models/certificate-preview.models';

// The fonts certificates can use, loaded from Google Fonts (index.html).
// Keys match LayoutValidator.FontKeys on the backend.
export const CERTIFICATE_FONTS: { key: FontKey; label: string; stack: string; group: 'Serif' | 'Script' | 'Sans' }[] = [
  { key: 'serif', label: 'Georgia (classic serif)', stack: "Georgia, 'Times New Roman', serif", group: 'Serif' },
  { key: 'playfair', label: 'Playfair Display', stack: "'Playfair Display', Georgia, serif", group: 'Serif' },
  { key: 'cormorant', label: 'Cormorant Garamond', stack: "'Cormorant Garamond', Georgia, serif", group: 'Serif' },
  { key: 'cinzel', label: 'Cinzel (engraved capitals)', stack: "Cinzel, Georgia, serif", group: 'Serif' },
  { key: 'garamond', label: 'EB Garamond', stack: "'EB Garamond', Georgia, serif", group: 'Serif' },
  { key: 'merriweather', label: 'Merriweather', stack: "Merriweather, Georgia, serif", group: 'Serif' },
  { key: 'great-vibes', label: 'Great Vibes (script)', stack: "'Great Vibes', 'Brush Script MT', cursive", group: 'Script' },
  { key: 'pinyon', label: 'Pinyon Script', stack: "'Pinyon Script', 'Brush Script MT', cursive", group: 'Script' },
  { key: 'sans', label: 'System sans', stack: "ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif", group: 'Sans' },
  { key: 'montserrat', label: 'Montserrat', stack: "Montserrat, system-ui, sans-serif", group: 'Sans' },
  { key: 'raleway', label: 'Raleway', stack: "Raleway, system-ui, sans-serif", group: 'Sans' },
  { key: 'lato', label: 'Lato', stack: "Lato, system-ui, sans-serif", group: 'Sans' },
];

export function fontStack(key: string | null | undefined): string | null {
  return CERTIFICATE_FONTS.find((f) => f.key === key)?.stack ?? null;
}

// Type settings of one element, as CSS. The element's own font wins over
// the template's; either one replaces the built-in look of the element
// (the class cert-font-set makes everything inside inherit it).
export function typeStyle(el: LayoutElement, templateFont: string | null | undefined): Record<string, string> {
  const style: Record<string, string> = {};
  if (el.fontSize !== undefined) style['font-size'] = `${el.fontSize}px`;
  if (el.color) style['color'] = el.color;
  if (el.align) style['text-align'] = el.align;
  if (el.bold) style['font-weight'] = '600';
  const font = fontStack(el.fontFamily) ?? fontStack(templateFont);
  if (font) style['font-family'] = font;
  if (el.italic) style['font-style'] = 'italic';
  if (el.uppercase) style['text-transform'] = 'uppercase';
  if (el.letterSpacing !== undefined && el.letterSpacing !== null) style['letter-spacing'] = `${el.letterSpacing}px`;
  if (el.lineHeight) style['line-height'] = String(el.lineHeight);
  if (el.opacity !== undefined && el.opacity !== null) style['opacity'] = String(el.opacity);
  return style;
}

export function hasFont(el: LayoutElement, templateFont: string | null | undefined): boolean {
  return !!(fontStack(el.fontFamily) ?? fontStack(templateFont));
}

// A 'line' element: a rule across its box, horizontal or vertical by shape,
// as thick as the box's short side.
export function lineStyle(el: LayoutElement): Record<string, string> {
  const height = el.height ?? 2;
  const vertical = height > el.width;
  const thickness = Math.max(1, Math.round(vertical ? el.width : height));
  const kind = el.lineStyle ?? 'solid';
  const border = `${kind === 'double' ? Math.max(3, thickness) : thickness}px ${kind} ${el.color || '#111827'}`;
  return vertical
    ? { width: '0', height: '100%', 'border-left': border, margin: '0 auto' }
    : { width: '100%', height: '0', 'border-top': border, 'margin-top': `${Math.max(0, (height - thickness) / 2)}px` };
}
