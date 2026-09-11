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

## Crop-overlap warning does not fire for sibling/overlapping crop picks

- **First seen:** 2026-09-11
- **Last seen:** 2026-09-11
- **Occurrences:** 1 (reproduced consistently across multiple retries same day)
- **Description:** `ReparentController.ShowCropOverlapWarningIfNeeded` (implemented to satisfy Section 6.9's
  sibling-overlap warning requirement) is meant to show an informational MessageBox when the user
  crops a region that overlaps another already-cropped, still-tracked region of the same top-level
  window. Live testing (cropping File Explorer's navigation pane, then cropping an overlapping
  region) confirmed the warning never fires, even after a fix attempt (removing an overly-broad
  same-HWND exclusion) that was independently code-reviewed and approved twice. Live memory-dump
  inspection of the running process during a repro confirmed the tracking list correctly held two
  distinct, genuinely-different HWNDs (SysTreeView32 and CabinetWClass, both children of the
  same top-level Explorer window per GetAncestor(..., GA_ROOT)), with overlapping stored crop
  rects - i.e., the data the warning logic depends on all looked correct, yet the warning still did
  not appear. Diagnostic Debug.WriteLine tracing was added to ShowCropOverlapWarningIfNeeded to
  pinpoint exactly which branch/condition suppresses it, but the trace output was not successfully
  captured/reviewed (VS Debug Output pane) before the user decided to defer further investigation.
- **Analysis:** Root cause not yet determined. Data captured via live dotnet-dump inspection rules
  out the two most obvious theories (identical HWNDs colliding with _trackingList.IsTracked's
  pre-check, or non-overlapping rects/different top-level roots) - the stored state looks exactly
  like a case the fix should handle correctly, so the remaining suspects are: a timing issue (e.g.
  the crop rect stored for the first entry no longer matches what's visually in the second
  screenshot by the time the second pick runs), an issue in how/when entries transition out of
  Active state, or a bug in the diagnostic-untested code path itself that only shows up with the
  temporary Debug.WriteLine tracing added (not yet confirmed).
- **Suggested handling (not yet implemented):** User confirmed this is non-critical (the feature is
  purely informational - a heads-up MessageBox, not a safety/correctness mechanism) and chose to
  leave it as a known, deferred issue rather than continue debugging now. The temporary
  Debug.WriteLine diagnostic lines left in ShowCropOverlapWarningIfNeeded should be reviewed via
  the VS Debug Output pane on a future repro attempt to get the missing ground-truth trace before
  attempting another fix; consider removing the diagnostic lines if/when this is picked back up and
  resolved, or leaving them if actively debugging.

## Intermittent blank/black crop-reparent host window (video content)

- **First seen:** 2026-09-11
- **Last seen:** 2026-09-11
- **Occurrences:** recurring, ~1-in-2/3 attempts (not a one-off; an earlier session had wrongly
  downgraded this to "non-reproducible")
- **Description:** When using Crop and Reparent on a region containing video playback (observed
  cropping a YouTube video region inside a browser window), the resulting reparented host window
  sometimes shows completely blank/black content instead of the video, roughly 1 in every 2-3
  attempts. Two live-inspection attempts this session failed because the user could not keep the
  reproduced blank window open long enough for live HWND/style/paint state inspection before
  closing the app.
- **Analysis:** Not yet diagnosed - no successful live inspection has been captured. Suspected
  (unconfirmed) to be related to DirectComposition/GPU-compositor surface handling for video
  content, similar in spirit to (but distinct from) the already-documented modern-Notepad
  DirectComposition finding above, but this has not been verified.
- **Suggested handling (not yet implemented):** On next repro, user will keep the blank window,
  WindowWorks, and the source browser window all open and notify immediately so live HWND/
  style/paint state can be captured (e.g. via dotnet-dump or direct Win32 inspection) before
  anything is closed. Tracked in this session's SQL todos table as `crop-blank-window-diagnosis`
  (pending).

## Transient ~10+ second self-recovering hang after a crop action

- **First seen:** 2026-09-11
- **Last seen:** 2026-09-11
- **Occurrences:** 1
- **Description:** After the confirmed cross-process UIA deadlock was fixed (see the fix
  described in docs/PROJECT_STATE.md/DESIGN_DECISIONS.md), the user observed one additional
  transient hang of roughly 10+ seconds following a crop action. Unlike the original deadlock,
  this one self-recovered on its own without requiring the process to be killed.
- **Analysis:** Not yet investigated. The self-recovering nature suggests a different (lesser)
  root cause than the fixed deadlock - possibly a slow-but-non-circular wait, or a different
  code path entirely - but this is unconfirmed. User explicitly deferred live investigation of
  this for now.
- **Suggested handling (not yet implemented):** Revisit when the user is ready to investigate
  live (e.g. arm the dotnet-dump hang watcher again and reproduce). Tracked in this session's
  SQL todos table as `residual-hang-10s` (blocked).
