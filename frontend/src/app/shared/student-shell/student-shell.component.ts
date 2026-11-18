import { Component, Input, OnChanges, SimpleChanges, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { StudentSessionService } from '../../core/services/student-session.service';
import { StudentPortalBanner, StudentPortalInstitution } from '../../core/models/student.models';

// The frame of every student page: a header with the institution, the page
// in the middle, and the institution's banners in a column on each side
// (below the page on narrow screens). Header actions are projected in
// with the [actions] attribute.
@Component({
  selector: 'app-student-shell',
  standalone: true,
  imports: [RouterLink, NgTemplateOutlet],
  templateUrl: './student-shell.component.html',
})
export class StudentShellComponent implements OnChanges {
  // The institution code; banners and branding load once it's known.
  @Input() slug = '';
  @Input() narrow = false;

  readonly institution = signal<StudentPortalInstitution | null>(null);
  readonly left = signal<StudentPortalBanner[]>([]);
  readonly right = signal<StudentPortalBanner[]>([]);

  constructor(private session: StudentSessionService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['slug']) return;
    this.institution.set(null);
    this.left.set([]);
    this.right.set([]);
    if (!this.slug) return;
    this.session.institution(this.slug).subscribe({ next: (i) => this.institution.set(i), error: () => undefined });
    this.session.banners(this.slug).subscribe({
      next: (list) => {
        this.left.set(list.filter((b) => b.side === 'Left'));
        this.right.set(list.filter((b) => b.side === 'Right'));
      },
      error: () => undefined,
    });
  }

  clicked(banner: StudentPortalBanner): void {
    if (banner.linkUrl) this.session.bannerClicked(this.slug, banner.id);
  }

  hasBanners(): boolean {
    return this.left().length + this.right().length > 0;
  }
}
