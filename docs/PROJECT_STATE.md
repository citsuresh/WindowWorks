# Project State

> This file is overwritten, not appended, at the end of each working session.

## Current Focus
- 2026-09-29: Fixed browser crop positioning and fractional right-edge bounds. Reviewed, built,
  manually retested in a regular browser, and committed/pushed as `303b97a`.
- The user confirmed the original crop shift and thin right-edge artifact are gone; a remaining
  bottom line was part of the source video.
- Confirmed browser DOM picks are fixed-size crop hosts. The child-element resize setting applies
  only to native child-HWND picks; resizing a browser crop host alone would expose/clip content,
  not scale it.

## Open Tasks / Known Issues
- No outstanding tasks from the browser crop fix or resize-setting investigation.

## Recently Changed Files
- `WindowWorks/src/WindowWorks.App/CropRectGeometry.cs` — uses the target outer-window origin
  after non-client chrome removal (`303b97a`).
- `WindowWorks/src/WindowWorks.App/BrowserDomTreeWalker.cs` — floors UIA right edge to avoid
  rounding a fractional boundary outward (`303b97a`).
- `docs/KEY_FLOWS.md` — added the browser DOM crop/reparent flow.
- `docs/PROJECT_STATE.md` — refreshed for this session.
- `docs/CODE_SUMMARY.md`, `docs/full-graph.json`, `docs/project-dependencies.json` — refreshed
  project summary and incremental code graph.
