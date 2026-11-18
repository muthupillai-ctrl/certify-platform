#!/usr/bin/env python3
"""Copies the standalone presentation into the app as its public home page.

The app's home page (/) shows frontend/src/assets/presentation/index.html in
a frame. That copy is certyfi-presentation.html without the speaker notes
(some are internal, and anything in the file is readable by anyone) and
without the notes toggle.

Run after changing the deck:  python3 docs/presentation/publish-to-app.py
"""
import re
from pathlib import Path

here = Path(__file__).resolve().parent
source = (here / "certyfi-presentation.html").read_text(encoding="utf-8")
target = here.parent.parent / "frontend" / "src" / "assets" / "presentation" / "index.html"


def replace_once(text: str, pattern: str, replacement: str) -> str:
    result, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"publish-to-app: pattern not found: {pattern}")
    return result


public = source
public = replace_once(public, r"const NOTES = \{.*?\};", "const NOTES = {};")
public = replace_once(public, r"\s*<button id=\"nav-toggle\">.*?</button>", "")
public = replace_once(public, r"document\.getElementById\('nav-toggle'\)\.onclick = [^\n]*\n", "")
public = replace_once(public, r"\s*if \(e\.key\.toLowerCase\(\) === 'n'\)[^\n]*", "")
public = public.replace(" · N for speaker notes", "")
# Phones: no keyboard hint in the cramped bar, and swipe to change slides.
public = replace_once(public, r"</style>", """  @media (max-width: 640px) { .bar > span:nth-child(2) { display: none; } }
</style>""")
public = replace_once(public, r"addEventListener\('resize', fit\);", """let touchX = null;
addEventListener('touchstart', e => { touchX = e.touches[0].clientX; }, { passive: true });
addEventListener('touchend', e => {
  if (touchX === null) return;
  const dx = e.changedTouches[0].clientX - touchX;
  touchX = null;
  if (Math.abs(dx) > 50) show(i + (dx < 0 ? 1 : -1));
});
addEventListener('resize', fit);""")
# No note text may reach the public copy.
if "<aside" in public or "const NOTES = {};" not in public:
    raise SystemExit("publish-to-app: speaker notes are still in the public copy")

target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(public, encoding="utf-8")

# The slides' screenshots (referenced as images/<name>.png).
images = here / "images"
if images.is_dir():
    (target.parent / "images").mkdir(exist_ok=True)
    for image in images.iterdir():
        if image.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
            (target.parent / "images" / image.name).write_bytes(image.read_bytes())
print(f"Wrote {target.relative_to(here.parent.parent)} ({len(public):,} bytes)")
