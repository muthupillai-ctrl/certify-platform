import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { FieldDefinition, FieldType } from '../../core/models/achievement.models';

// A column type is a FieldType minus 'table' — no nested tables, matching
// the server-side restriction in FieldSchemaValidator (this is a UX hint
// only; the server remains the real enforcement).
export type ColumnType = Exclude<FieldType, 'table'>;

// Builder-only row shapes — never sent as-is; toFieldDefinitions() strips
// the UI-only bits (columns stay empty for non-table fields, etc.) before
// the field schema is serialized on submit.
export interface BuilderColumn {
  label: string;
  key: string;
  type: ColumnType;
}
export interface BuilderField {
  label: string;
  key: string;
  type: FieldType;
  columns: BuilderColumn[];
  required?: boolean;
}

// Turns a human-entered label into a safe, unique JSON object key so staff
// never see or type raw JSON — camelCase, alphanumeric only, deduped
// against keys already used elsewhere in the schema.
function slugifyKey(label: string, existingKeys: string[]): string {
  const words = label
    .trim()
    .split(/[^a-zA-Z0-9]+/)
    .filter(Boolean);
  let base = words
    .map((word, i) => (i === 0 ? word.toLowerCase() : word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()))
    .join('');
  if (!base || !/^[A-Za-z]/.test(base)) {
    base = 'field' + base.charAt(0).toUpperCase() + base.slice(1);
  }
  let key = base;
  let suffix = 2;
  while (existingKeys.includes(key)) {
    key = `${base}${suffix}`;
    suffix++;
  }
  return key;
}

// Strips the UI-only bits (empty rows, standalone column type restriction)
// before a built schema is submitted to the API.
export function toFieldDefinitions(fields: BuilderField[]): FieldDefinition[] {
  return fields
    .filter((f) => f.label && f.key)
    .map((f) => ({
      key: f.key,
      label: f.label,
      type: f.type,
      ...(f.required ? { required: true } : {}),
      ...(f.type === 'table'
        ? { columns: f.columns.filter((c) => c.label && c.key).map((c) => ({ key: c.key, label: c.label, type: c.type })) }
        : {}),
    }));
}

// The reverse — used to pre-fill the builder from an already-saved schema
// (e.g. a certificate template's fields, when starting a new achievement
// from it).
export function fromFieldDefinitions(defs: FieldDefinition[]): BuilderField[] {
  return defs.map((d) => ({
    label: d.label,
    key: d.key,
    type: d.type,
    columns: (d.columns ?? []).map((c) => ({ label: c.label, key: c.key, type: c.type as ColumnType })),
    required: d.required === true,
  }));
}

@Component({
  selector: 'app-field-schema-builder',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './field-schema-builder.component.html',
})
export class FieldSchemaBuilderComponent {
  @Input() fields: BuilderField[] = [];
  @Output() fieldsChange = new EventEmitter<BuilderField[]>();

  addField(): void {
    this.fields.push({ label: '', key: '', type: 'text', columns: [] });
    this.fieldsChange.emit(this.fields);
  }

  removeField(index: number): void {
    this.fields.splice(index, 1);
    this.fieldsChange.emit(this.fields);
  }

  onFieldLabelChange(field: BuilderField): void {
    const otherKeys = this.fields.filter((f) => f !== field).map((f) => f.key);
    field.key = field.label ? slugifyKey(field.label, otherKeys) : '';
    this.fieldsChange.emit(this.fields);
  }

  onFieldTypeChange(field: BuilderField): void {
    if (field.type !== 'table') {
      field.columns = [];
    } else if (field.columns.length === 0) {
      this.addColumn(field);
      return;
    }
    this.fieldsChange.emit(this.fields);
  }

  addColumn(field: BuilderField): void {
    field.columns.push({ label: '', key: '', type: 'text' });
    this.fieldsChange.emit(this.fields);
  }

  removeColumn(field: BuilderField, index: number): void {
    field.columns.splice(index, 1);
    this.fieldsChange.emit(this.fields);
  }

  onColumnLabelChange(field: BuilderField, column: BuilderColumn): void {
    const otherKeys = field.columns.filter((c) => c !== column).map((c) => c.key);
    column.key = column.label ? slugifyKey(column.label, otherKeys) : '';
    this.fieldsChange.emit(this.fields);
  }
}
