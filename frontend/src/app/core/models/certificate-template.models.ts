import type { FieldDefinition, TemplateKind } from './achievement.models';
import type { SignatoryName, TemplateLayout } from './certificate-preview.models';

export type { TemplateKind };

export interface CertificateTemplate {
  id: string;
  name: string;
  description: string | null;
  kind: TemplateKind;
  fieldSchema: FieldDefinition[];
  isActive: boolean;
  titleText: string;
  subtitleText: string | null;
  accentColor: string | null;
  fontFamily: string | null;
  layout: TemplateLayout;
  backgroundImageUrl: string | null;
  // One to three, in printing order (see Signatory on the backend).
  signatoryIds: string[];
  signatories: SignatoryName[];
  // A third person (an institution admin) approves before issue.
  requiresFinalApproval: boolean;
  // Switching it off was asked for and waits for a second admin.
  finalApprovalOffPending: boolean;
}

export interface CreateCertificateTemplateRequest {
  name: string;
  description: string | null;
  kind: TemplateKind;
  fieldSchema: FieldDefinition[];
  // Optional — the backend defaults titleText to name and generates a
  // Kind-appropriate layout when these are omitted (see
  // CertificateTemplatesController.TryResolveVisualDesign). Still a plain
  // form (Name/Description/Kind/fields) in Phase 1; the canvas editor
  // (Phase 2) is what actually sets these explicitly.
  titleText?: string;
  subtitleText?: string | null;
  accentColor?: string | null;
  fontFamily?: string | null;
  layout?: TemplateLayout;
  backgroundImageUrl?: string | null;
  signatoryIds?: string[];
  requiresFinalApproval?: boolean;
}

export interface UpdateCertificateTemplateRequest {
  name: string;
  description: string | null;
  kind: TemplateKind;
  fieldSchema: FieldDefinition[];
  titleText?: string;
  subtitleText?: string | null;
  accentColor?: string | null;
  fontFamily?: string | null;
  layout?: TemplateLayout;
  backgroundImageUrl?: string | null;
  signatoryIds?: string[];
  requiresFinalApproval?: boolean;
}
