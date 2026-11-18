import { Component, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { PortalBanner, PortalBannersService, SavePortalBanner } from '../../../core/services/portal-banners.service';
import { AuthService } from '../../../core/services/auth.service';
import { ModalComponent } from '../../../shared/modal/modal.component';

// Banners the institution shows beside its student portal: its own images
// and links (courses, events, admissions). Left and right columns, each in
// order; optional run dates; click counts.
@Component({
  selector: 'app-portal-banners',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './portal-banners.component.html',
})
export class PortalBannersComponent implements OnInit {
  readonly banners = signal<PortalBanner[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly left = computed(() => this.banners().filter((b) => b.side === 'Left'));
  readonly right = computed(() => this.banners().filter((b) => b.side === 'Right'));

  // Add/edit form.
  readonly editing = signal<PortalBanner | 'new' | null>(null);
  readonly saving = signal(false);
  readonly uploading = signal(false);
  readonly formError = signal<string | null>(null);
  form: SavePortalBanner = this.blank('Left');

  readonly deleting = signal<PortalBanner | null>(null);

  constructor(private service: PortalBannersService, readonly auth: AuthService) {}

  ngOnInit(): void {
    this.load();
  }

  get institutionSlug(): string {
    return this.auth.currentUser()?.tenant_slug ?? '';
  }

  load(): void {
    this.service.list().subscribe({
      next: (list) => {
        this.banners.set(list);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.error.set(err.error?.error ?? 'Could not load banners.');
        this.loading.set(false);
      },
    });
  }

  add(side: 'Left' | 'Right'): void {
    this.form = this.blank(side);
    this.formError.set(null);
    this.editing.set('new');
  }

  edit(b: PortalBanner): void {
    this.form = { title: b.title, imageUrl: b.imageUrl, linkUrl: b.linkUrl, side: b.side, startsOn: b.startsOn, endsOn: b.endsOn, isActive: b.isActive };
    this.formError.set(null);
    this.editing.set(b);
  }

  upload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Cleared, so choosing the same file again (next banner) still uploads.
    input.value = '';
    if (!file) return;
    this.uploading.set(true);
    this.formError.set(null);
    this.service.uploadImage(file).subscribe({
      next: (r) => {
        this.form.imageUrl = r.url;
        this.uploading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.uploading.set(false);
        this.formError.set(err.error?.error ?? 'Could not upload the image.');
      },
    });
  }

  save(): void {
    const target = this.editing();
    if (!target) return;
    this.saving.set(true);
    this.formError.set(null);
    const body: SavePortalBanner = {
      ...this.form,
      linkUrl: this.form.linkUrl?.trim() || null,
      startsOn: this.form.startsOn || null,
      endsOn: this.form.endsOn || null,
    };
    const call = target === 'new' ? this.service.create(body) : this.service.update(target.id, body);
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

  toggle(b: PortalBanner): void {
    this.service.update(b.id, { title: b.title, imageUrl: b.imageUrl, linkUrl: b.linkUrl, side: b.side, startsOn: b.startsOn, endsOn: b.endsOn, isActive: !b.isActive })
      .subscribe({ next: () => this.load(), error: (err: HttpErrorResponse) => this.error.set(err.error?.error ?? 'Could not save.') });
  }

  move(b: PortalBanner, by: -1 | 1): void {
    const side = (b.side === 'Left' ? this.left() : this.right()).map((x) => x.id);
    const i = side.indexOf(b.id);
    const j = i + by;
    if (j < 0 || j >= side.length) return;
    [side[i], side[j]] = [side[j], side[i]];
    const left = b.side === 'Left' ? side : this.left().map((x) => x.id);
    const right = b.side === 'Right' ? side : this.right().map((x) => x.id);
    this.service.reorder(left, right).subscribe({ next: (list) => this.banners.set(list) });
  }

  confirmDelete(): void {
    const b = this.deleting();
    if (!b) return;
    this.service.delete(b.id).subscribe({
      next: () => {
        this.deleting.set(null);
        this.load();
      },
      error: (err: HttpErrorResponse) => this.error.set(err.error?.error ?? 'Could not delete.'),
    });
  }

  // Shown on the portal today?
  live(b: PortalBanner): boolean {
    const today = new Date().toISOString().slice(0, 10);
    return b.isActive && (!b.startsOn || b.startsOn <= today) && (!b.endsOn || b.endsOn >= today);
  }

  runLabel(b: PortalBanner): string {
    if (!b.startsOn && !b.endsOn) return 'Always';
    if (b.startsOn && b.endsOn) return `${b.startsOn} to ${b.endsOn}`;
    return b.startsOn ? `From ${b.startsOn}` : `Until ${b.endsOn}`;
  }

  private blank(side: 'Left' | 'Right'): SavePortalBanner {
    return { title: '', imageUrl: '', linkUrl: null, side, startsOn: null, endsOn: null, isActive: true };
  }
}
