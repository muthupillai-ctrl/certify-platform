import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Signatory, SignatoriesService } from '../../../core/services/signatories.service';
import { ModalComponent } from '../../../shared/modal/modal.component';

// The people whose authority the institution's certificates carry. Each
// template names one to three of them (template editor); certificates keep
// the names they were made with, so an edit here only affects new ones.
@Component({
  selector: 'app-signatories',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './signatories.component.html',
})
export class SignatoriesComponent implements OnInit {
  readonly signatories = signal<Signatory[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly editing = signal<Signatory | 'new' | null>(null);
  readonly saving = signal(false);
  readonly formError = signal<string | null>(null);
  name = '';
  designation = '';

  readonly deleting = signal<Signatory | null>(null);
  readonly deleteError = signal<string | null>(null);

  constructor(private service: SignatoriesService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.service.list().subscribe({
      next: (list) => {
        this.signatories.set(list);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.error.set(err.error?.error ?? 'Could not load signatories.');
        this.loading.set(false);
      },
    });
  }

  add(): void {
    this.name = '';
    this.designation = '';
    this.formError.set(null);
    this.editing.set('new');
  }

  edit(s: Signatory): void {
    this.name = s.name;
    this.designation = s.designation;
    this.formError.set(null);
    this.editing.set(s);
  }

  save(): void {
    const target = this.editing();
    if (!target) return;
    this.saving.set(true);
    this.formError.set(null);
    const call = target === 'new' ? this.service.create(this.name, this.designation) : this.service.update(target.id, this.name, this.designation);
    call.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.formError.set(err.error?.error ?? 'Could not save.');
      },
    });
  }

  askDelete(s: Signatory): void {
    this.deleteError.set(null);
    this.deleting.set(s);
  }

  confirmDelete(): void {
    const s = this.deleting();
    if (!s) return;
    this.service.delete(s.id).subscribe({
      next: () => {
        this.deleting.set(null);
        this.load();
      },
      error: (err: HttpErrorResponse) => this.deleteError.set(err.error?.error ?? 'Could not delete.'),
    });
  }
}
