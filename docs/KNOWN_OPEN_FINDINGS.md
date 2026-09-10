# Known Open Findings

This file is a user-curated reference list of open, unresolved findings that were explicitly
chosen not to act on immediately. It is not a task/bug tracker and is not automatically
maintained â€” entries are only added, edited, or removed when explicitly requested.

## Modern Windows 11 Notepad renders as cascaded/ghosted frames when reparented

- **First seen:** 2026-09-10
- **Last seen:** 2026-09-10
- **Occurrences:** 2
- **Description:** When using the Window Reparenting feature (docs/REPARENT_FEATURE_PLAN.md) to
  reparent modern Windows 11 Notepad (the packaged/WinUI3-based Notepad, tabs UI) into a
  ReparentHostWindow socket, clicking inside the embedded window produces a cascade of duplicated
  Notepad title-bar/frame artifacts (each showing its own min/max/close glyphs), and the host
  window itself sometimes briefly snaps to near-full-screen size. Confirmed via manual testing
  that this does **not** happen with classic Win32 apps â€” it is specific to modern Notepad.
  **Recurrence (2nd occurrence, same day):** after later fixes (content-only reparenting â€”
  stripping the target's own WS_CAPTION/WS_THICKFRAME/etc. so only client-area content is
  embedded, replacing the earlier WS_CHILD-only approach) resolved the drag/resize-ghosting issue
  for all classic Win32 apps (Notepad++, Explorer, etc.), modern Notepad still showed distinct
  symptoms of the same underlying app-compat issue: (a) visible painting glitches in its own tab
  strip/toolbar area even without interaction, (b) the window looking visually broken/misaligned
  after being restored back to a normal top-level window, and (c) a cascade of duplicated
  title-bar-like frames trailing behind when the WindowWorks host window's own title bar is
  dragged to reposition it. Classic apps do not exhibit any of these three symptoms.
- **Analysis:** Modern Windows 11 Notepad is a packaged WinUI3/XAML app that renders via its own
  DirectComposition visual tree tied to its original top-level HWND. Forcing it into `WS_CHILD`
  via `SetParent` (per the reparent mechanics in `ReparentEngine.Reparent`, which match the
  PowerToys-verified sequence in the plan's Â§8) appears to corrupt/duplicate that composition
  surface â€” an app-compatibility limitation explicitly anticipated by the plan's Â§4 ("many apps
  ... custom-rendered UI ... don't tolerate WS_CHILD + SetParent and may glitch"), not a bug in
  the reparent/host-frame code itself. `SetWindowPos`/`SWP_FRAMECHANGED` sequencing was verified
  correct against the plan and real PowerToys source during this investigation. The 2nd-occurrence
  symptoms are consistent with this same root cause: modern Notepad's visible "title bar" (with its
  tabs) is drawn by the app itself in client-space via DirectComposition, not a real OS non-client
  caption â€” so `Reparent()`'s later fix of clearing `WS_CAPTION`/`WS_THICKFRAME` (which correctly
  removes real OS-drawn chrome for classic apps) cannot remove it, and its own composition surface
  continues to desync/duplicate whenever the host window moves or the app's own tab strip repaints.
- **Suggested handling (not yet implemented):** use classic Win32 apps (e.g. mspaint, WordPad,
  classic notepad.exe) for manual testing of the reparent mechanics; consider whether Phase 1
  should detect/warn about packaged/WinUI3 targets specifically (in addition to the general
  compatibility-warning toggle already planned in Â§4/Â§9), or simply document this as a known
  limitation for v1.

## Reparenting/restoring "Chrome Legacy Window" (Chrome_RenderWidgetHostHWND) leaves the browser window mouse-input-dead

- **First seen:** 2026-09-10
- **Last seen:** 2026-09-10
- **Occurrences:** 1
- **Description:** In the ancestor-chain picker (docs/REPARENT_FEATURE_PLAN.md), a Chrome/Chromium
  browser window's ancestor chain surfaces two yellow-box picks: the real content window (e.g. the
  YouTube tab) and an internal "Chrome Legacy Window" (class `Chrome_RenderWidgetHostHWND`).
  Reparenting the real content window and restoring it works fine. Reparenting the Chrome Legacy
  Window pick and then restoring it leaves the browser window's mouse input completely dead
  (clicks/hover do nothing) while keyboard input continues to work. This is a distinct symptom from
  the already-known "picking Chrome Legacy Window produces a blank reparented frame" limitation —
  this is the *restore* side, not the reparent side, and it corrupts the ordinary top-level browser
  window's usability afterward, not just the reparented view.
- **Analysis:** `Chrome_RenderWidgetHostHWND` is a Chromium-internal helper window (used for IME/
  accessibility/input plumbing), not independent UI — its position/z-order relative to the real
  content window is normally managed entirely by Chrome itself. Moving it out via `SetParent`
  (ReparentEngine.Reparent) and back (ReparentEngine.RestoreOriginalState) desyncs Chrome's internal
  expectations for that window, silently breaking mouse hit-testing for the browser while leaving
  keyboard/focus routing (which doesn't depend on this window's z-order/position) intact. Diagnostic
  logging added to `RestoreOriginalState` (final ExStyle/Style/enabled/visible/parent state) did not
  show any obviously-wrong Win32 style bits — style/parent restoration appears mechanically correct;
  the breakage is believed to be in Chrome's own internal window-position tracking for this helper
  HWND, not a WindowWorks-side style/flag bug.
- **Suggested handling (not yet implemented):** Two options were discussed and left open per user
  request: (a) detect and block reparenting of internal accessibility/legacy-helper windows like
  this one via a generic (non-hardcoded-classname) heuristic, or (b) attempt a best-effort z-order/
  position re-sync (e.g. `SetWindowPos`/`BringWindowToTop`) after restore specifically for this
  case. Neither has been implemented. For now, avoid picking "Chrome Legacy Window" during manual
  testing/use — pick the real content window instead.

## Reparented Chrome/Chromium window's own tab-strip acts as a draggable "title bar," causing paint glitches when dragged

- **First seen:** 2026-09-10
- **Last seen:** 2026-09-10
- **Occurrences:** 1
- **Description:** After reparenting a Chrome/Chromium browser window's real content window (e.g.
  the YouTube tab pick, not the Chrome Legacy Window pick) into the host frame, the host's own
  title bar is correctly the only *real* OS title bar (expected/working as designed). However, the
  reparented Chrome content itself still shows/behaves as if its own tab strip area is a draggable
  region: clicking and dragging within it moves things and produces background painting
  glitches/artifacts (diagonal stray pixel "staircase" marks, stray disconnected icon fragments),
  even though `Reparent()` already strips `WS_CAPTION`/`WS_THICKFRAME`/etc. real OS chrome bits.
- **Analysis:** Chrome implements its own custom draggable region in client-area content (via
  `WM_NCHITTEST` returning `HTCAPTION` for its tab-strip area) so the tab strip can be dragged like
  a title bar even in borderless/frameless window modes — this is an app-level emulation, separate
  from real `WS_CAPTION` non-client chrome, and is not affected by stripping `WS_CAPTION`/
  `WS_THICKFRAME` in `ReparentEngine.Reparent`. Once reparented (WS_CHILD inside the host's socket),
  dragging this region still triggers Chrome's own move logic (e.g. `WM_SYSCOMMAND`/`SC_MOVE`)
  against a window that is no longer really top-level, which conflicts with the host frame's layout
  and produces the observed ghosting/paint artifacts.
- **Suggested handling (not yet implemented):** intercept/suppress `WM_NCHITTEST` HTCAPTION
  responses and/or `WM_SYSCOMMAND`/`SC_MOVE` messages directed at the reparented target (would
  require subclassing the target's WndProc, which the codebase does not currently do — see the
  related, currently-removed `SubclassTarget`/`UnsubclassTarget` mechanism referenced only in old
  log entries, not in current code). Deferred; user chose to log this and move on to conditional
  resizability (Phase 1 item 4) instead of fixing now.
