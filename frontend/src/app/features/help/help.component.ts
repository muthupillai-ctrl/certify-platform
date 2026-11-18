import { Component, ElementRef, OnInit, ViewEncapsulation, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { forkJoin } from 'rxjs';
import { RenderedGuide, TocEntry, handleGuideClick, handleGuideKeydown, renderGuide } from '../../shared/markdown/guide-render';

interface GuideInfo {
  id: string;
  title: string;
  audience: string;
  summary: string;
}

// The user guides (docs/user-guide/*.md, linked into assets/help): /help
// lists them with a search across all of them; /help/<guide> shows one.
export const GUIDES: GuideInfo[] = [
  { id: 'institution', title: 'Institution guide', audience: 'Administrators, registrars and approvers', summary: 'Set up your institution, design templates, issue and approve certificates, import paper certificates, manage students and connect your LMS.' },
  { id: 'students', title: 'Student guide', audience: 'Students', summary: 'Sign in to the student portal, pay when needed, and view, download and share your certificates.' },
  { id: 'verifiers', title: 'Verifier guide', audience: 'Employers and anyone checking a certificate', summary: 'Check a certificate by QR code, code or file, and understand what the result means.' },
];

interface SearchHit {
  guide: GuideInfo;
  id: string;
  title: string;
  snippet: string;
}

@Component({
  selector: 'app-help',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './help.component.html',
  styleUrls: ['../developers/developers.component.css', './help.component.css'],
  encapsulation: ViewEncapsulation.None,
})
export class HelpComponent implements OnInit {
  readonly guides = GUIDES;
  readonly current = signal<GuideInfo | null>(null);
  readonly html = signal<SafeHtml | null>(null);
  readonly toc = signal<TocEntry[]>([]);
  readonly error = signal(false);
  readonly query = signal('');
  private readonly rendered = signal<Record<string, RenderedGuide>>({});

  // Every word must appear in the section's title or text.
  readonly hits = computed<SearchHit[]>(() => {
    const words = this.query().toLowerCase().split(/\s+/).filter((w) => w.length > 1);
    if (!words.length) return [];
    const hits: (SearchHit & { score: number })[] = [];
    for (const guide of GUIDES) {
      for (const s of this.rendered()[guide.id]?.sections ?? []) {
        const title = s.title.toLowerCase();
        const text = s.text.toLowerCase();
        if (!words.every((w) => title.includes(w) || text.includes(w))) continue;
        const at = Math.max(0, text.indexOf(words[0]));
        const snippet = (at > 60 ? '…' : '') + s.text.slice(Math.max(0, at - 60), at + 140) + (at + 140 < s.text.length ? '…' : '');
        hits.push({ guide, id: s.id, title: s.title, snippet, score: words.filter((w) => title.includes(w)).length });
      }
    }
    return hits.sort((a, b) => b.score - a.score).slice(0, 12);
  });

  constructor(
    private http: HttpClient,
    private sanitizer: DomSanitizer,
    private route: ActivatedRoute,
    private router: Router,
    private host: ElementRef<HTMLElement>,
  ) {}

  ngOnInit(): void {
    // Load all guides once (small files): needed for search everywhere.
    forkJoin(Object.fromEntries(GUIDES.map((g) => [g.id, this.http.get(`assets/help/${g.id}.md`, { responseType: 'text' })]))).subscribe({
      next: (files) => {
        const rendered: Record<string, RenderedGuide> = {};
        for (const g of GUIDES) rendered[g.id] = renderGuide((files as Record<string, string>)[g.id].replace(/^# .*\n/, ''));
        this.rendered.set(rendered);
        this.route.paramMap.subscribe((p) => this.show(p.get('guide')));
      },
      error: () => this.error.set(true),
    });
  }

  private show(id: string | null): void {
    const guide = GUIDES.find((g) => g.id === id) ?? null;
    if (id && !guide) {
      this.router.navigate(['/help']);
      return;
    }
    this.current.set(guide);
    this.query.set('');
    if (!guide) return;
    const r = this.rendered()[guide.id];
    this.toc.set(r.toc);
    this.html.set(this.sanitizer.bypassSecurityTrustHtml(r.html));
    // A link to a section: go there once it's drawn.
    const fragment = this.route.snapshot.fragment;
    setTimeout(() => (fragment ? this.scrollToId(fragment) : window.scrollTo({ top: 0 })), 50);
  }

  onClick(event: MouseEvent): void {
    // Links within a guide (#section) scroll instead of leaving the page.
    const link = (event.target as HTMLElement).closest<HTMLAnchorElement>('a[href^="#"]');
    if (link) {
      event.preventDefault();
      this.scrollToId(link.getAttribute('href')!.slice(1));
      return;
    }
    handleGuideClick(event, this.host.nativeElement);
  }

  onKeydown(event: KeyboardEvent): void {
    handleGuideKeydown(event, this.host.nativeElement);
  }

  scrollTo(id: string, event: Event): void {
    event.preventDefault();
    this.scrollToId(id);
  }

  openHit(hit: SearchHit): void {
    this.query.set('');
    if (this.current()?.id === hit.guide.id) {
      this.scrollToId(hit.id);
    } else {
      this.router.navigate(['/help', hit.guide.id], { fragment: hit.id });
    }
  }

  private scrollToId(id: string): void {
    this.host.nativeElement.querySelector(`#${CSS.escape(id)}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    history.replaceState(null, '', `${location.pathname}#${id}`);
  }
}
