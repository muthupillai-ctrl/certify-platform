# Certyfi presentation — "What we're building"

Audience: management, prospective pilot institutions and partners (non-technical).
Status as of 9 October 2026. 22 slides. Screenshots are in `images/` (demo data).

| File | What it is |
|---|---|
| `certyfi-presentation.html` | Standalone copy. Open in a browser: ← → to page, **N** for speaker notes, print to PDF for a handout. |
| `deck.json`, `slides/*.html` | The deck's source, as saved from the Claude Slides artifact. |

The live, editable deck (with PowerPoint/PDF download) is the Claude artifact
https://claude.ai/artifact/4u53Mkf89XVozTBmRFUUma. Edits made there are not
synced here; save a fresh copy after changing it.

## The app's home page

The app's public home page (`/`) shows this presentation, with Institution
login, Platform login and "Verify a certificate". It uses a copy at
`frontend/src/assets/presentation/index.html` **without the speaker notes**
(some are internal, and anything in that file is readable by anyone), with
swipe navigation on phones. After changing the deck, save its slide files into
`slides/` (and any new images into `images/`, listed in `images/blobs.json`), then run:

    python3 docs/presentation/build-standalone.py
    python3 docs/presentation/publish-to-app.py

and rebuild/deploy the frontend.
