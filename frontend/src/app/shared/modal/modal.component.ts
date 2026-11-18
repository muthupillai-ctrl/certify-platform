import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

// Renders via fixed positioning, so it's immune to any ancestor's
// overflow-hidden/scroll clipping — unlike an absolutely-positioned
// in-table dropdown or inline expansion, which either gets clipped by a
// rounded-corner table container or, worse, expands the table row itself
// (the bug this component exists to fix — see CredentialsComponent).
@Component({
  selector: 'app-modal',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './modal.component.html',
})
export class ModalComponent {
  @Input() open = false;
  @Input() title = '';
  // Overridable for content wider than the default (e.g. a certificate
  // preview) — every existing caller keeps today's width unchanged.
  @Input() maxWidthClass = 'max-w-lg';
  @Output() closed = new EventEmitter<void>();

  onBackdropClick(): void {
    this.closed.emit();
  }
}
