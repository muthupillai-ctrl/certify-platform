import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/services/auth.service';
import { AchievementsService } from '../../../core/services/achievements.service';
import { CertificateTemplatesService } from '../../../core/services/certificate-templates.service';
import { Achievement } from '../../../core/models/achievement.models';
import { CertificateTemplate } from '../../../core/models/certificate-template.models';
import { ModalComponent } from '../../../shared/modal/modal.component';
import {
  BuilderField,
  FieldSchemaBuilderComponent,
  fromFieldDefinitions,
  toFieldDefinitions,
} from '../../../shared/field-schema-builder/field-schema-builder.component';

@Component({
  selector: 'app-achievements',
  standalone: true,
  imports: [CommonModule, FormsModule, FieldSchemaBuilderComponent, ModalComponent],
  templateUrl: './achievements.component.html',
})
export class AchievementsComponent implements OnInit {
  readonly achievements = signal<Achievement[]>([]);
  readonly templates = signal<CertificateTemplate[]>([]);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly showForm = signal(false);
  readonly submitting = signal(false);

  // Set while amending an existing achievement instead of creating a new
  // one — the same form/fields below are reused either way (see submit()).
  readonly editingId = signal<string | null>(null);
  readonly viewingAchievement = signal<Achievement | null>(null);

  title = '';
  description = '';
  criteriaNarrative = '';
  validityPeriodMonths: number | null = null;
  fields: BuilderField[] = [];
  templateId = '';

  constructor(
    protected auth: AuthService,
    private achievementsService: AchievementsService,
    private certificateTemplatesService: CertificateTemplatesService,
  ) {}

  ngOnInit(): void {
    this.load();
    this.certificateTemplatesService.list().subscribe({
      next: (templates) => this.templates.set(templates),
      error: () => {
        /* Template picker just stays empty; staff can still build fields from scratch. */
      },
    });
  }

  load(): void {
    this.loading.set(true);
    this.achievementsService.list().subscribe({
      next: (achievements) => {
        this.achievements.set(achievements);
        this.loading.set(false);
      },
      error: () => {
        this.errorMessage.set('Failed to load achievements.');
        this.loading.set(false);
      },
    });
  }

  toggleForm(): void {
    this.errorMessage.set(null);
    if (this.showForm()) {
      this.showForm.set(false);
      this.resetForm();
    } else {
      this.showForm.set(true);
    }
  }

  // Deep-copies the picked template's field schema into the builder as a
  // starting point — staff can still add/remove/edit fields before saving,
  // matching the confirmed "Template pre-fills the form" flow. Not shown
  // while amending — re-templating an existing achievement is out of scope.
  onTemplateChange(): void {
    const template = this.templates().find((t) => t.id === this.templateId);
    this.fields = template ? fromFieldDefinitions(template.fieldSchema) : [];
  }

  view(achievement: Achievement): void {
    this.viewingAchievement.set(achievement);
  }

  closeView(): void {
    this.viewingAchievement.set(null);
  }

  edit(achievement: Achievement): void {
    this.errorMessage.set(null);
    this.editingId.set(achievement.id);
    this.title = achievement.title;
    this.description = achievement.description ?? '';
    this.criteriaNarrative = achievement.criteriaNarrative ?? '';
    this.validityPeriodMonths = achievement.validityPeriodMonths;
    this.fields = fromFieldDefinitions(achievement.fieldSchema);
    this.templateId = '';
    this.showForm.set(true);
  }

  submit(): void {
    this.errorMessage.set(null);
    this.submitting.set(true);

    const editingId = this.editingId();
    const request = {
      title: this.title,
      description: this.description || null,
      criteriaNarrative: this.criteriaNarrative || null,
      validityPeriodMonths: this.validityPeriodMonths,
      fieldSchema: toFieldDefinitions(this.fields),
    };

    const call = editingId
      ? this.achievementsService.update(editingId, request)
      : this.achievementsService.create({ ...request, templateId: this.templateId || null });

    call.subscribe({
      next: () => {
        this.submitting.set(false);
        this.showForm.set(false);
        this.resetForm();
        this.load();
      },
      error: (err) => {
        this.submitting.set(false);
        this.errorMessage.set(err.error?.error ?? `Failed to ${editingId ? 'amend' : 'create'} achievement.`);
      },
    });
  }

  fieldSummary(achievement: Achievement): string {
    if (achievement.fieldSchema.length === 0) return '—';
    return achievement.fieldSchema.map((f) => f.label).join(', ');
  }

  private resetForm(): void {
    this.title = '';
    this.description = '';
    this.criteriaNarrative = '';
    this.validityPeriodMonths = null;
    this.fields = [];
    this.templateId = '';
    this.editingId.set(null);
  }
}
