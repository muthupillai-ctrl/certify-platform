import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TenantProfile } from '../../core/models/tenant.models';

// The institution profile form, shared by the institution's own Settings
// page and the SuperAdmin institution page. Emits the edited copy; the
// parent saves it and passes back saving/error/saved state.
@Component({
  selector: 'app-institution-profile-form',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './institution-profile-form.component.html',
})
export class InstitutionProfileFormComponent implements OnChanges {
  @Input({ required: true }) profile!: TenantProfile;
  @Input() saving = false;
  @Input() error: string | null = null;
  @Input() savedMessage: string | null = null;
  @Output() save = new EventEmitter<TenantProfile>();

  draft!: TenantProfile;

  ngOnChanges(): void {
    this.draft = { ...this.profile };
  }

  submit(): void {
    this.save.emit({ ...this.draft });
  }
}
