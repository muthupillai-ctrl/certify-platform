import { Component, ElementRef, OnInit, ViewEncapsulation, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { environment } from '../../../environments/environment';
import { TocEntry, handleGuideClick, handleGuideKeydown, renderGuide } from '../../shared/markdown/guide-render';

// The Partner API guide for developers at other companies, drawn from
// docs/partner-api.md (linked into assets/docs, so the guide is kept in one
// place). Examples show the address of whichever system serves the page,
// so the test site shows the test address.
@Component({
  selector: 'app-developers',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './developers.component.html',
  styleUrl: './developers.component.css',
  encapsulation: ViewEncapsulation.None,
})
export class DevelopersComponent implements OnInit {
  readonly html = signal<SafeHtml | null>(null);
  readonly toc = signal<TocEntry[]>([]);
  readonly error = signal(false);
  readonly environmentLabel = signal<string | null>(null);
  readonly origin = window.location.origin;

  constructor(private http: HttpClient, private sanitizer: DomSanitizer, private host: ElementRef<HTMLElement>) {}

  ngOnInit(): void {
    this.http.get<{ environmentLabel: string | null }>(`${environment.apiUrl}/system-info`).subscribe({
      next: (r) => this.environmentLabel.set(r.environmentLabel),
      error: () => undefined,
    });
    this.http.get('assets/docs/partner-api.md', { responseType: 'text' }).subscribe({
      next: (md) => {
        // This system's own address in every example; the page has its own heading.
        const text = md
          .replaceAll('https://<certyfi host>', this.origin)
          .replaceAll('https://certyfi.ahamsys.com', this.origin)
          .replace(/^# .*\n/, '');
        const guide = renderGuide(text);
        this.toc.set(guide.toc);
        this.html.set(this.sanitizer.bypassSecurityTrustHtml(guide.html));
      },
      error: () => this.error.set(true),
    });
  }

  onClick(event: MouseEvent): void {
    handleGuideClick(event, this.host.nativeElement);
  }

  onKeydown(event: KeyboardEvent): void {
    handleGuideKeydown(event, this.host.nativeElement);
  }

  scrollTo(id: string, event: Event): void {
    event.preventDefault();
    this.host.nativeElement.querySelector(`#${CSS.escape(id)}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    history.replaceState(null, '', `#${id}`);
  }
}
