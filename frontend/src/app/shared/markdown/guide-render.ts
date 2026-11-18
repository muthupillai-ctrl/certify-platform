import { Marked, Tokens } from 'marked';

// Turns one of Certyfi's own Markdown guides (developer guide, user guides)
// into HTML: headings get ids (and a contents list), code blocks get Copy
// buttons, tables scroll inside their own box, and <!-- tabs --> groups of
// code blocks become language tabs. The Markdown is our own file from our
// own assets, so the HTML is trusted.

export interface TocEntry {
  id: string;
  text: string;
  level: number;
}

export interface RenderedGuide {
  html: string;
  toc: TocEntry[];
  // Plain text of each section (for search): the heading's id and text.
  sections: { id: string; title: string; text: string }[];
}

// Tab names for language examples.
export const LANGUAGES: Record<string, string> = {
  bash: 'curl', javascript: 'Node.js', python: 'Python', php: 'PHP', csharp: 'C#', java: 'Java',
};
export const LANGUAGE_KEY = 'certyfi_dev_language';

const escape = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const plain = (html: string) => html.replace(/<[^>]+>/g, ' ').replace(/&amp;/g, '&').replace(/&#39;/g, "'").replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/\s+/g, ' ').trim();

export function renderGuide(markdown: string, tocDepth = 3): RenderedGuide {
  const toc: TocEntry[] = [];
  const used = new Set<string>();
  const slug = (s: string) => {
    const base = s.toLowerCase().replace(/<[^>]+>/g, '').replace(/&[a-z#0-9]+;/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'section';
    let id = base;
    for (let i = 2; used.has(id); i++) id = `${base}-${i}`;
    used.add(id);
    return id;
  };

  const marked = new Marked({
    gfm: true,
    renderer: {
      heading({ tokens, depth }: Tokens.Heading) {
        const inner = this.parser.parseInline(tokens);
        const id = slug(inner);
        if (depth <= tocDepth) toc.push({ id, text: plain(inner), level: depth });
        return `<h${depth} id="${id}">${inner}</h${depth}>`;
      },
      code({ text: code, lang }: Tokens.Code) {
        const name = lang ? (LANGUAGES[lang] ?? lang) : '';
        return `<div class="dev-code" data-lang="${escape(lang ?? '')}">${name ? `<span class="dev-code-lang">${escape(name)}</span>` : ''}<button type="button" class="dev-copy" data-copy>Copy</button><pre><code>${escape(code)}</code></pre></div>`;
      },
      image({ href, title, text }: Tokens.Image) {
        return `<figure class="dev-figure"><img src="${escape(href)}" alt="${escape(text)}" loading="lazy" />${text ? `<figcaption>${escape(title || text)}</figcaption>` : ''}</figure>`;
      },
    },
  });

  const html = tabs((marked.parse(markdown, { async: false }) as string)
    .replaceAll('<table>', '<div class="dev-table"><table>')
    .replaceAll('</table>', '</table></div>'));

  // Sections for search: text between one heading and the next.
  const sections: RenderedGuide['sections'] = [];
  const parts = html.split(/(?=<h[1-4] id=")/);
  for (const part of parts) {
    const m = /^<h[1-4] id="([^"]+)">([\s\S]*?)<\/h[1-4]>/.exec(part);
    if (m) sections.push({ id: m[1], title: plain(m[2]), text: plain(part.slice(m[0].length)) });
  }
  return { html, toc, sections };
}

// Each <!-- tabs --> group: one tab per code block, showing the reader's
// language (remembered) or the first.
function tabs(html: string): string {
  let preferred = '';
  try {
    preferred = localStorage.getItem(LANGUAGE_KEY) ?? '';
  } catch {
    // No storage: the first tab shows.
  }
  let group = 0;
  return html.replace(/<!-- tabs -->([\s\S]*?)<!-- \/tabs -->/g, (_, inner: string) => {
    const blocks = inner.match(/<div class="dev-code" data-lang="[^"]*">[\s\S]*?<\/pre><\/div>/g) ?? [];
    const langs = blocks.map((b) => /data-lang="([^"]*)"/.exec(b)![1]);
    const active = langs.includes(preferred) ? preferred : langs[0];
    const id = `tabs-${group++}`;
    const buttons = langs.map((l, i) =>
      `<button type="button" role="tab" id="${id}-t${i}" aria-controls="${id}-p${i}" aria-selected="${l === active}" tabindex="${l === active ? 0 : -1}" data-tab="${l}">${LANGUAGES[l] ?? l}</button>`).join('');
    const panels = blocks.map((b, i) =>
      `<div role="tabpanel" id="${id}-p${i}" aria-labelledby="${id}-t${i}" data-panel="${langs[i]}"${langs[i] === active ? '' : ' hidden'}>${b}</div>`).join('');
    return `<div class="dev-tabs"><div role="tablist" aria-label="Language">${buttons}</div>${panels}</div>`;
  });
}

// Click and key handling for a rendered guide: Copy buttons and tabs.
export function handleGuideClick(event: MouseEvent, host: HTMLElement): void {
  const tab = (event.target as HTMLElement).closest<HTMLElement>('[role=tab]');
  if (tab) {
    showLanguage(host, tab.dataset['tab']!);
    return;
  }
  const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-copy]');
  const code = button?.parentElement?.querySelector('pre');
  if (!button || !code) return;
  const done = () => {
    button.textContent = 'Copied';
    setTimeout(() => (button.textContent = 'Copy'), 1500);
  };
  navigator.clipboard?.writeText(code.innerText).then(done, () => {
    const range = document.createRange();
    range.selectNodeContents(code);
    const selection = window.getSelection();
    selection?.removeAllRanges();
    selection?.addRange(range);
  });
}

export function handleGuideKeydown(event: KeyboardEvent, host: HTMLElement): void {
  const tab = (event.target as HTMLElement).closest<HTMLElement>('[role=tab]');
  if (!tab || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
  const all = Array.from(tab.parentElement!.querySelectorAll<HTMLElement>('[role=tab]'));
  const next = all[(all.indexOf(tab) + (event.key === 'ArrowRight' ? 1 : all.length - 1)) % all.length];
  event.preventDefault();
  showLanguage(host, next.dataset['tab']!);
  next.focus();
}

// Shows one language in every tab group.
function showLanguage(host: HTMLElement, lang: string): void {
  for (const group of Array.from(host.querySelectorAll<HTMLElement>('.dev-tabs'))) {
    if (!group.querySelector(`[data-panel="${CSS.escape(lang)}"]`)) continue;
    group.querySelectorAll<HTMLElement>('[role=tab]').forEach((t) => {
      const on = t.dataset['tab'] === lang;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
    });
    group.querySelectorAll<HTMLElement>('[role=tabpanel]').forEach((p) => (p.hidden = p.dataset['panel'] !== lang));
  }
  try {
    localStorage.setItem(LANGUAGE_KEY, lang);
  } catch {
    // Not remembered; fine.
  }
}
