// Matches Certyfi.Infrastructure.Validation.FieldSchemaValidator's rules —
// a 'table' field's columns are themselves FieldDefinitions restricted to
// non-table types (no nesting), enforced server-side; the client-side
// restriction in the achievement builder is a UX hint only.
export type FieldType = 'text' | 'number' | 'date' | 'table';

export interface FieldDefinition {
  key: string;
  label: string;
  type: FieldType;
  columns?: FieldDefinition[];
  // Every certificate must fill it in (top-level fields only).
  required?: boolean;
}

// Matches Certyfi.Domain.Enums.TemplateKind.ToString() exactly — decides
// which certificate layout the verify page renders.
export type TemplateKind = 'Certificate' | 'MarksCard';

export interface Achievement {
  id: string;
  title: string;
  description: string | null;
  criteriaNarrative: string | null;
  validityPeriodMonths: number | null;
  isActive: boolean;
  fieldSchema: FieldDefinition[];
  templateKind: TemplateKind;
}

export interface CreateAchievementRequest {
  title: string;
  description: string | null;
  criteriaNarrative: string | null;
  validityPeriodMonths: number | null;
  fieldSchema: FieldDefinition[];
  templateId: string | null;
}

// No templateId — amend edits the achievement in place, it doesn't
// re-template it (see AchievementsController.Update on the backend).
export interface UpdateAchievementRequest {
  title: string;
  description: string | null;
  criteriaNarrative: string | null;
  validityPeriodMonths: number | null;
  fieldSchema: FieldDefinition[];
}
