#!/usr/bin/env python3
"""Rebuilds certyfi-presentation.html from slides/*.html in deck.json order.

The slides are the deck's source (as saved from the Claude Slides artifact).
In the standalone copy: speaker notes (<aside>) move into the NOTES script,
<x-icon>/<x-shape> become inline SVG (drawings reused from the existing file),
and uploaded images (/_blob/...) point at images/ via images/blobs.json.
Then run publish-to-app.py to refresh the app's home page copy.
"""
import html
import json
import re
from pathlib import Path

here = Path(__file__).resolve().parent
target = here / "certyfi-presentation.html"
old = target.read_text(encoding="utf-8")
deck = json.loads((here / "deck.json").read_text(encoding="utf-8"))
blobs = json.loads((here / "images" / "blobs.json").read_text(encoding="utf-8"))

icons = {m.group(1): re.sub(r"^<svg[^>]*>", "", m.group(0))[:-6]
         for m in re.finditer(r'<svg viewBox="0 0 24 24"[^>]*aria-label="(\w+)">.*?</svg>', old, re.S)}
# Icons first used after the standalone copy was made (drawn by hand once).
icons.setdefault("Trust", '<path d="M12 3 5 6v5c0 4.5 3 8.3 7 10 4-1.7 7-5.5 7-10V6l-7-3z"/><path d="m9 12 2 2 4-4"/>')
arrow = re.search(r'<svg viewBox="0 0 48 28".*?</svg>', old, re.S).group(0)


def icon(m: re.Match) -> str:
    name = re.search(r'name="(\w+)"', m.group(1)).group(1)
    style = re.search(r'style="([^"]*)"', m.group(1)).group(1)
    if name not in icons:
        raise SystemExit(f"build-standalone: no drawing for icon '{name}'; add its SVG once by hand")
    return (f'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" '
            f'stroke-linejoin="round" style="{style}; flex:none" aria-label="{name}">{icons[name]}</svg>')


notes, sections = {}, []
for sid in deck["order"]:
    s = (here / "slides" / f"{sid}.html").read_text(encoding="utf-8").strip()
    m = re.search(r"<aside>(.*?)</aside>", s, re.S)
    if m:
        notes[sid] = html.unescape(m.group(1).strip())
        s = s.replace(m.group(0), "")
    s = re.sub(r"<x-icon([^>]*)></x-icon>", icon, s)
    s = re.sub(r'<x-shape kind="arrow-right"[^>]*></x-shape>', arrow, s)
    for blob, path in blobs.items():
        s = s.replace(blob, path)
    for leftover in ("<x-icon", "<x-shape", "/_blob/"):
        if leftover in s:
            raise SystemExit(f"build-standalone: {sid} still has {leftover}")
    sections.append(s)

frame = '<div class="stage"><div class="frame" id="frame">'
start = old.index(frame) + len(frame)
last = old.rindex("<section id=")
end = old.index("</section>", last) + len("</section>")
out = old[:start] + "\n" + "\n".join(sections) + old[end:]
nm = re.search(r"const NOTES = (\{.*?\});", out, re.S)
out = out[:nm.start(1)] + json.dumps(notes, ensure_ascii=False) + out[nm.end(1):]
target.write_text(out, encoding="utf-8")
print(f"Wrote {target.name}: {len(sections)} slides, {len(notes)} with notes")
