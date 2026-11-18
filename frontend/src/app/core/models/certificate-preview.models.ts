import type { FieldDefinition, TemplateKind } from './achievement.models';
import type { DisplayStatus, FieldValues } from './credential.models';

// 'Draft' is a client-only state — a credential that hasn't been created
// yet (the live preview in the New Credential form). Never returned by any
// API response; the API's DisplayStatus values are the only ones a real
// credential can carry. 'Sample' is a template drawn with made-up data for
// a partner app (the template-preview page): badged and watermarked so a
// screenshot can't pass for a real certificate.
export type PreviewStatus = DisplayStatus | 'Draft' | 'Sample';

// Matches Certyfi.Infrastructure.Validation.LayoutValidator's closed enum
// exactly. 'field'/'table' carry fieldKey; every other type's content comes
// from a fixed slot on CertificatePreviewData (titleText, recipientName,
// issuedAt, etc.) — never from the layout JSON itself.
export type LayoutElementType =
  | 'title' | 'subtitle' | 'institutionName' | 'logo' | 'recipientName'
  | 'achievementTitle' | 'achievementDescription' | 'field' | 'table'
  | 'issuedDate' | 'expiryDate' | 'qrCode' | 'credentialId' | 'signatories'
  // Design elements: the template's own words, a line, an image.
  | 'text' | 'line' | 'image';

// Fonts a template or element may use (certificate-style.ts; checked by
// LayoutValidator.FontKeys on the backend).
export type FontKey =
  | 'serif' | 'sans' | 'playfair' | 'cormorant' | 'cinzel' | 'garamond' | 'merriweather'
  | 'great-vibes' | 'pinyon' | 'montserrat' | 'raleway' | 'lato';

export type FrameStyle = 'none' | 'line' | 'double' | 'classic' | 'ornate' | 'corners' | 'ribbon' | 'band';

// A border Certyfi draws around the certificate (certificate-frame component).
export interface CertificateFrame {
  style: FrameStyle;
  color?: string;
  // Line thickness, px.
  width?: number;
  // Distance from the edge, px.
  inset?: number;
}

export type LineStyle = 'solid' | 'dashed' | 'dotted' | 'double';

// A signatory as printed on a certificate (name and designation only).
export interface SignatoryName {
  name: string;
  designation: string;
}

// What every certificate design must show (matches
// LayoutValidator.CoreElements): any one type of each entry will do.
export const CORE_ELEMENTS: { label: string; types: LayoutElementType[] }[] = [
  { label: 'Institution name or logo', types: ['institutionName', 'logo'] },
  { label: 'Recipient name', types: ['recipientName'] },
  { label: 'Certificate title', types: ['achievementTitle'] },
  { label: 'Issue date', types: ['issuedDate'] },
  { label: 'Signatories', types: ['signatories'] },
  { label: 'QR code or certificate code', types: ['qrCode', 'credentialId'] },
];

export type LayoutAlign = 'left' | 'center' | 'right';

export interface LayoutElement {
  id: string;
  type: LayoutElementType;
  x: number;
  y: number;
  width: number;
  height?: number;
  fontSize?: number;
  color?: string;
  align?: LayoutAlign;
  bold?: boolean;
  fieldKey?: string;
  // Type settings (any text element).
  fontFamily?: FontKey;
  italic?: boolean;
  uppercase?: boolean;
  letterSpacing?: number;
  lineHeight?: number;
  opacity?: number;
  // 'text': the words shown. 'image': its address. 'line': how it's drawn.
  text?: string;
  src?: string;
  lineStyle?: LineStyle;
  // Editor only: can't be dragged or nudged.
  locked?: boolean;
}

export interface TemplateLayout {
  canvasWidth: number;
  canvasHeight: number;
  backgroundColor?: string;
  frame?: CertificateFrame | null;
  elements: LayoutElement[];
}

// The one shape CertificatePreviewComponent renders from, whether the data
// came from the public VerifyCredentialResponse, an admin-side
// CredentialResponse (any status), or a live in-progress draft that
// doesn't exist on the server yet.
export interface CertificatePreviewData {
  recipientName: string;
  achievementTitle: string;
  achievementDescription: string | null;
  institutionName: string;
  institutionLogoUrl: string | null;
  institutionPrimaryColor: string | null;
  status: PreviewStatus;
  issuedAt: string | null;
  expiresOn: string | null;
  // A digitised paper certificate: shown as its issue date, with issuedAt
  // as "digitally re-issued" (optional: most certificates have neither).
  originalIssueDate?: string | null;
  legacyCertificateNumber?: string | null;
  fieldSchema: FieldDefinition[];
  fieldValues: FieldValues;
  templateKind: TemplateKind;
  // The template's own visual design, snapshotted onto the achievement at
  // creation time (see Achievement.TemplateLayoutJson). null for data that
  // predates this feature — CertificatePreviewComponent falls back to the
  // two legacy hardcoded layouts whenever layout is null, so nothing
  // issued before this shipped changes appearance.
  layout: TemplateLayout | null;
  titleText: string | null;
  subtitleText: string | null;
  accentColor: string | null;
  fontFamily: string | null;
  // An externally-hosted (http/https) image used as the certificate's
  // backdrop — the "import a professionally designed certificate" path.
  // Same URL-only convention as institutionLogoUrl; this app has no file
  // storage of its own.
  backgroundImageUrl: string | null;
  // Who signs it (a certificate keeps its own copy; null on older ones and
  // digitised paper certificates).
  signatories?: SignatoryName[] | null;
}
