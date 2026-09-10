# Window Reparenting Feature — Implementation Plan

> Status: **Exploration / design only — not yet implemented.**
> This document consolidates the design discussion for an enhanced,
> PowerToys-Crop-and-Lock-inspired window reparenting feature for WindowWorks.

> **Rollout rule — do not auto-commit:** No commits related to this feature
> (implementation, refactors, or fixes) should be made automatically at any
> stage. Every change must be manually verified and tested by hand first, and
> explicitly confirmed by the project owner before it is committed. This
> applies throughout development, not just at final release, given the high
> app-compatibility risk inherent in reparenting.

## Start here (orientation for a new implementation session)

This document is fully self-contained — read it in full before writing any
code; no prior conversation/chat history needs to be referenced. Suggested
reading order for someone about to start implementing:

1. **§1 Goal** and **§11 Terminology** — know what you're building and what
   to call it ("Window Reparenting" feature; "Pop Out and Reparent" /
   "Crop and Reparent" user-facing actions).
2. **§14 Phased implementation plan** — this is the actual work breakdown.
   Start with **Phase 0**, then **Phase 1** (the large, fully-hardened
   foundation phase — covers picker UX, whole/ancestor-element pop-out,
   Settings toggles, Reset Reparenting, elevation checks, WinEvent hook,
   graceful shutdown, and crash recovery, all together). Do not skip ahead
   to Phase 2/3 until Phase 1's full numbered acceptance checklist is
   manually verified and explicitly signed off by the project owner (per
   the rollout rule above).
3. Refer back to **§5–§9** for the detailed design rationale behind whatever
   part of Phase 1 (or later phases) you're currently implementing — each
   phase in §14 cites the specific sub-sections it depends on.
4. **§10 Out of scope** and **§13 Open questions** — check these before
   assuming something is missing; most "what about X" questions that came
   up during design were already resolved and are marked as such inline
   (look for **"resolved:"**). Genuinely open items are listed plainly
   without a resolution.
5. `docs/ROADMAP.md` has one related backlog item (multi-monitor validation,
   deferred due to lack of test hardware) — not part of this plan's phases.

## 1. Goal

Allow a user to pick a window (or a nested native control within a window) and
"reparent" it into a WindowWorks-managed host frame, optionally cropped to a
sub-region, so it can be embedded/positioned as part of a layout/preset —
similar in spirit to PowerToys' Crop and Lock "Reparent" mode, but generalized
to whole-window selection with an interactive ancestor-chain picker.

## 2. Background: how PowerToys does it

PowerToys' Crop and Lock utility (`src/modules/CropAndLock`) has two modes:

- **Thumbnail mode** — `DwmRegisterThumbnail`, view-only, safe, no reparenting.
- **Reparent mode** — literally calls Win32 `SetParent()` to embed the target
  window inside a small child "socket" window hosted by a new crop window.
  Key mechanics (see `ReparentCropAndLockWindow.cpp`):
  1. Save original window state (`GWL_EXSTYLE`, `GWL_STYLE`, `WINDOWPLACEMENT`, rect).
  2. Compute DPI-aware crop geometry.
  3. `SetParent(target, childWindow)` + add `WS_CHILD` style.
  4. `SetWindowPos` to align the crop rect within the host's client area.
  5. Forward `WM_MOUSEACTIVATE`/activation to the embedded window (child
	 windows don't get normal OS activation).
  6. On close: `SetParent(target, nullptr)` + restore original style/placement.

Known PowerToys issues: **#28275** — imperfect restoration of maximized
windows after un-reparenting — **verified via the GitHub issue itself: this
was a real, reproducible bug (closed window ended up un-maximized and
shifted after restore), but it is CLOSED with "Fix Committed" (closed
2023-12-08), and the current shipped source (`RestoreOriginalState()`,
verified in §8) already contains the fix** — it explicitly checks
`originalPlacement.showCmd != SW_SHOWMAXIMIZED` before forcing
`SW_RESTORE`, i.e. it now correctly preserves the maximized state instead of
always restoring. **This plan's own maximized-window handling (§6.5, §8)
already mirrors the *fixed* behavior**, not the old buggy one — no action
needed, this is just a citation-accuracy correction (the issue is historical
context, not a currently-live risk to worry about). **#28308** (still open,
verified via the issue itself) — a feature *request* (not a bug) asking for
an option to keep the original window visible/usable while reparented
(rather than always hiding it, per §7) — correctly already out of scope for
this plan's v1 (per §10), consistent with how the issue itself remains
unaddressed by Microsoft. FancyZones/Workspaces deliberately avoid reparenting
entirely — they only use `MoveWindow`/`SetWindowPos` plus custom window
property tagging (`GWLP_USERDATA`).

## 3. Advantages of reparenting

- True interactivity with the embedded window/control (not just a live view).
- Compact footprint — show only the relevant part of a window.
- Works on arbitrary windows without needing app cooperation.

## 4. Pitfalls / risks (must be mitigated, not ignored)

- **App compatibility breakage** — many apps (games, DirectX/OpenGL surfaces,
  custom-rendered UI, elevated processes) don't tolerate `WS_CHILD` +
  `SetParent` and may glitch, lose input, or crash.
- **Focus/activation quirks** — child windows need manual activation
  forwarding.
- **Fragile state restoration** — maximize/restore placement, extended
  styles, DPI/monitor geometry must be manually saved/replayed.
- **Elevation/UAC boundary** — `SetParent` fails across integrity levels.
- **Z-order/taskbar/Alt-Tab side effects** — reparented windows can disappear
  from Alt-Tab/taskbar/virtual-desktop switching.
- **Single-owner limitation** — a window can only be reparented into one host
  at a time.
- **Crash cleanup** — if WindowWorks crashes before un-reparenting, the
  original window can be left orphaned as a child of a dead window.

**Mitigation:** show a compatibility warning **in the Settings UI** (not a
per-action `MessageBox`) — a persistent notice/toggle such as "Window
reparenting may cause compatibility issues with some apps — enable at your
own risk," so users opt in once rather than being interrupted every time.

## 5. Selection model: whole window vs. sub-element

- **Whole top-level window** reparenting is simple, safe, and matches the
  primary use case (embedding an app into a WindowWorks layout slot). No crop
  math, no partial-viewport rendering issues, cleaner restore.
- **Sub-element reparenting** only makes sense for **native Win32 child
  HWNDs** (classic MFC/WinForms apps with real child controls). Modern UI
  frameworks (WPF, UWP, Chromium/Electron, most WinForms with custom-drawn
  controls) render everything inside a single HWND — there is no separate
  window handle per visual element, so:
  - UI Automation (`IUIAutomation::ElementFromPoint`) can identify a visual
	element, but that element has **no HWND** and therefore **cannot** be
	passed to `SetParent`.
  - **Decision: do not highlight/offer UIA-only elements that can't actually
	be reparented.** Only ever highlight a real, reparentable HWND. This
	keeps the picker UI honest — whatever is highlighted is exactly what
	will be reparented if selected.
- **Browsers** (Chromium/Edge/Firefox/Electron): the entire page is one
  renderer HWND — hovering over any DOM element (including e.g. a YouTube
  `<video>`) resolves to the same whole-window HWND. No element-level
  reparenting is possible here.
  - **Exception:** a browser's native **Picture-in-Picture** popped-out video
	player is a genuine separate top-level HWND and can be reparented like
	any other window.
  - **Fallback for "just the video" scenarios:** use the **crop mode** (same
	mechanism as PowerToys Crop and Lock) to reparent the whole browser
	window but visually clip/position it to show only the video's screen
	region. Known caveat: the crop rect is a static screen-rect snapshot; if
	the video's on-page position shifts (resize, scroll, layout change,
	fullscreen toggle), the crop misaligns. This is an accepted limitation
	matching PowerToys' own behavior — no live crop tracking in v1.

## 6. Picker UX design (v1 scope)

### 6.1 Activation & input capture

- Entering "pick" mode shows a full-screen, topmost overlay window that
  **owns mouse input over the yellow-box confirm UI** (not click-through
  there) so the picker can intercept the actual confirm click — the
  underlying app must never receive a click intended for a box (otherwise
  clicking a box could fall through and activate the app normally,
  defeating the picker). **Refined in §6.2:** the overlay's background
  region (everywhere except the box UI itself) remains click-through so
  hover-discovery (`WindowFromPoint`) can see through to the real app
  underneath — see §6.2 for the corrected hit-testing mechanism and why a
  fully input-owning full-screen overlay does not work for discovery. This
  still mirrors the spirit of PowerToys Color Picker / Crop and Lock's own
  picker overlays (owns the confirm gesture, not passive) while fixing the
  discovery mechanism.
- `Escape` or right-click cancels picking and restores normal input.

### 6.2 Hover discovery — containment (ancestor) chain only

- **Hit-testing mechanism (corrected — "excluding the picker's own overlay"
  is not by itself an implementation, since a mouse-input-owning full-screen
  overlay sits directly under the cursor and `WindowFromPoint` would simply
  return the overlay's own HWND):** the picker overlay must **not** be a
  single full-screen `WS_EX_TRANSPARENT`-free window covering the whole
  hit-test area for the underlying app discovery step. Two viable concrete
  approaches (pick one during Phase 1 implementation, not deferred):
  1. **Toggle-transparency approach:** keep the overlay input-owning
	 (`WS_EX_LAYERED` without `WS_EX_TRANSPARENT`) for the yellow-box UI
	 elements only (small hit-test regions around the boxes themselves),
	 but make the overlay's full-screen background region
	 `WS_EX_TRANSPARENT` so `WindowFromPoint` at the cursor naturally
	 resolves through to the real app underneath during hover/discovery.
	 Only the small yellow-box regions themselves need to intercept clicks
	 (the actual confirm gesture is clicking a box, per §6.3 — not clicking
	 the highlighted app content) — so the overlay does not need to own
	 input over the entire screen, only over the box UI.
  2. **`WindowFromPoint` + explicit self-exclusion loop:** call
	 `WindowFromPoint(cursor)`, and if the result is the picker's own
	 overlay HWND (or any of its owned windows), fall back to
	 `RealChildWindowFromPoint`/manual `EnumWindows` z-order walk starting
	 just below the overlay in z-order to find the real topmost app window
	 at that point. More code, but keeps the overlay simple
	 (fully input-owning) with the exclusion logic centralized in one
	 place.
  - **Recommendation: approach 1 (toggle-transparency / small hit-test
	regions for boxes only)** is simpler and lower-risk — it avoids needing
	a custom z-order-walking hit-test implementation, and naturally
	matches the confirm-via-box-click design (§6.3) where the overlay only
	ever needs to intercept clicks on the boxes themselves, never on the
	highlighted app content. Document this as the Phase 1 default; only
	fall back to approach 2 if approach 1 proves insufficient in practice
	(e.g. if the box regions themselves need to overlap/exceed screen
	edges in a way that complicates per-region transparency).
- On mouse-move, resolve the topmost HWND at the cursor via
  `WindowFromPoint` (using the hit-test mechanism above to correctly see
  through the picker's own overlay).
- Walk the containment chain upward via `GetAncestor(hwnd, GA_PARENT)`
  repeatedly until `GA_ROOT`, collecting each distinct HWND along the way.
- **Sibling/z-order occlusion picking (choosing a window that is fully
  hidden behind another, unrelated window at the same point) is explicitly
  OUT OF SCOPE for v1** — it requires `EnumWindows`/`EnumChildWindows` +
  manual rect-intersection scanning across the desktop, which is higher risk
  (performance, DPI/multi-monitor correctness, low real-world value) and is
  deferred to a possible future enhancement.
- Filtering rules for the ancestor chain:
  - Skip ancestors whose rect is pixel-identical to a child's (no visual
	difference — avoid duplicate-looking entries).
  - Skip invisible / 0×0 / off-screen helper windows.
  - Cap the number of listed levels (e.g. 4–5). If more ancestors exist,
	show a single "… N more ancestors — click to expand" entry instead of a
	scrollbar (scrolling conflicts with the continuously-updating,
	mouse-tracking nature of the picker).
- For single-HWND apps (browsers, WPF, Electron) the chain collapses to
  exactly one entry — the whole window — which is the expected, non-cluttered
  result.

### 6.3 Yellow box list (confirm UI)

- Near the cursor, show one small labeled box per ancestor-chain entry,
  ordered nearest-to-farthest (deepest/child-most first, root window last).
- Each box label identifies the HWND: class name, window title/text if
  available, and a size hint (e.g. `Edit — "Untitled" — 412×238`).
- Hovering over a box re-highlights the corresponding on-screen rect, so the
  user sees exactly what they're about to pick before committing.
- **Clicking a box** (not the highlighted rect itself) is the confirm
  gesture — this cleanly separates "preview" from "commit" and avoids any
  ambiguity/click-through risk, since the box is real UI owned by the
  picker overlay.
- An additional, visually distinct box (different color/tint, e.g. a
  scissors icon) is pinned at the bottom of the stack whenever "Crop and
  Reparent" is enabled (see §9 settings toggle — this box is entirely
  absent when that sub-toggle is off, and not present at all until Phase 2,
  which introduces crop mode): **"Crop a region instead."** This signals it
  is an action, not a hierarchy entry. Clicking it transitions into
  crop-rect mode (see §6.5), scoped to whichever ancestor level was most
  recently highlighted (not necessarily the root).

### 6.4 Adaptive box-stack positioning

- Compute the full stack's bounding size before placement.
- Use `MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST)` +
  `GetMonitorInfo` to get the current monitor's **work area** (excludes
  taskbar).
- Default anchor: right of cursor, top-aligned, small offset (~16px).
  - If it would overflow the right edge of the work area, flip to the left
	of the cursor.
  - If it would overflow the bottom edge, flip to grow upward
	(bottom-aligned to cursor) instead of downward.
  - Clamp to work-area bounds as a last resort on very small/narrow screens.
- Re-evaluate on every mouse-move as the cursor crosses monitors (DPI/work
  area may differ) — but only re-run the flip/clamp logic when the anchor
  corner actually changes, to avoid visual jitter near boundaries.

### 6.5 Crop-rect refinement mode

- Reuses PowerToys Crop-and-Lock-style drag-to-select rectangle UI, scoped to
  the HWND that was highlighted when "Crop a region instead" was clicked.
- After the user drags a rect, proceed with the same reparent mechanism
  (`SetParent` on that HWND) plus the crop-offset/clip `SetWindowPos` logic
  used by PowerToys, to align only the selected screen region within the
  host frame's client area.
- **Crop-geometry math, verified against the real `CropAndLock()`
  implementation (`ReparentCropAndLockWindow.cpp`) — worth copying this
  exact approach rather than re-deriving it:**
  - Compute `diffX`/`diffY` as the offset between the target's client area
    (`ClientAreaInScreenSpace`, i.e. excluding the title bar/borders) and
    its full window rect (`GetWindowRect`) — this accounts for non-client
    chrome so the crop rect (which the user drew relative to the visible
    client content) lines up correctly once applied via `SetWindowPos`
    relative to the window rect's origin.
  - **Maximized-window special case:** if `GetWindowPlacement(...).showCmd
    == SW_SHOWMAXIMIZED`, do **not** use the normal client/window-rect
    diff — instead use the difference between the current monitor's
    **work-area rect** (`GetMonitorInfo(...).rcWork`, from
    `MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST)`) and the window
    rect. This is necessary because a maximized window's reported
    `GetWindowRect` can extend slightly beyond the visible work area (the
    non-client "overhang" trick Windows uses for maximized windows), so
    the naive client/window diff would be wrong specifically in the
    maximized case.
  - The crop rect's per-monitor DPI is queried via
    `GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY)` — using
    `MonitorFromWindow(target, MONITOR_DEFAULTTONULL)` (returns `nullptr`
    if the window doesn't currently intersect a monitor, which should be
    treated as an edge case/failure rather than crashing) — and the crop
    host frame's own window rect is then computed via
    `AdjustWindowRectExForDpi(&windowRect, style, false, exStyle, dpi)`
    using the **higher of dpiX/dpiY** (not an average) as the single
    effective DPI value for that adjustment call.
  - Final target repositioning within the host is a single
    `SetWindowPos(target, nullptr, -cropRect.left, -cropRect.top, 0, 0, SWP_NOSIZE | SWP_FRAMECHANGED | SWP_NOZORDER)`
    — i.e. the target is moved by the **negative** crop-rect origin so that
    the desired crop region lands at the child window's (0,0), with the
    child window itself sized exactly to the crop rect's width/height
    (see §6.5/§8's `ChildWindow`/host-child-window sizing, mirrored from
    `ChildWindow` in the real source, which is a plain `WS_CHILD |
    WS_CLIPCHILDREN | WS_CLIPSIBLINGS` sized to exactly the crop
    width/height and created as a **child of the host frame itself** — a
    thin intermediate "socket" window between the host frame and the
    reparented target, not the host frame directly).
- **Host frame is fixed-size, not resizable — verified directly against the
  real source, and matches user intuition about why this must be so.**
  `ReparentCropAndLockWindow`'s constructor creates its window with
  `style &= ~(WS_MAXIMIZEBOX | WS_THICKFRAME)` — explicitly stripping both
  the maximize box and the resize-grip border — and its `MessageHandler`
  has no `WM_SIZE`/live-resize handling at all. **This is a deliberate
  design constraint of crop mode specifically, not an oversight:** the crop
  rect is a fixed pixel-mapping computed once at crop time (§6.5 above) —
  resizing the host frame post-crop would require either stretching the
  cropped content (visually wrong — it's a 1:1 region capture, not a
  scalable view) or re-deriving an entirely new crop selection live during
  a resize drag (needless complexity PowerToys avoids by simply disabling
  resize). **WindowWorks should match this behavior for Crop-and-Reparent
  targets (Phase 2): the host frame must not be resizable** (no resize
  grips/maximize in its control-strip chrome for a crop-mode target) —
  the only supported way to change the visible region is the existing
  "Adjust crop" control-strip button (§6.7), which re-invokes the crop-rect
  drag UI rather than trying to resize the fixed-size host live.
  **This does NOT apply to whole-window (non-cropped) reparenting (Phase
  1):** a whole-window target has no fixed crop-rect mapping — its host
  frame **should be resizable/movable** (per §6.7's "resize handles, drag
  handle" chrome and the Phase 1 `SetWindowPos`-re-application task), the
  same way any normal top-level window is resizable, since there's no
  captured sub-region to become geometrically invalid.
  - **Correction — this is actually a three-way distinction, not a binary
    whole-window-vs-crop split (found during a later review pass, prompted
    by a direct question about child-element resize risk).** §5/§6.2
    explicitly allow picking a **native ancestor-chain child HWND**
    (e.g. an MFC `CView` pane or WinForms `Panel` nested inside a parent),
    not just the top-level window — Phase 1's own title says
    "whole/ancestor-element" pop-out, and this is a genuinely different
    risk profile from a whole top-level window even when **not** using
    crop mode at all:
    - A **whole top-level window** is designed, by every windowing
      convention, to be resized by something external (the user dragging
      its border) — apps universally implement their own `WM_SIZE`
      relayout for exactly this. Our host frame resizing it just
      re-triggers a code path the app already handles correctly.
    - A **native child HWND control**, by contrast, was typically **only
      ever resized by its original parent's own layout/docking code**
      (e.g. anchoring, a splitter, a table-layout panel) — many simple
      child controls have **no internal `WM_SIZE`-driven relayout of
      their own at all**; their size was always dictated externally.
      Once reparented, the original parent's layout code that used to
      manage its size is gone (§6.8 already notes the *original parent*
      may error trying to reposition a since-removed child — this is the
      symmetric, previously-undocumented risk on the **child's own** side:
      forcibly resizing it from the *new* host frame can produce clipped/
      stretched content, stale scrollbars, un-relaid-out grandchildren, or
      visual corruption, since the control was never built to self-manage
      its own size).
    - **Decision: a reparented native child HWND (ancestor-chain pick,
      not the whole top-level window, and not crop mode) should default
      to the same fixed-size host-frame treatment as crop mode** — no
      resize grips/maximize affordance, drag-to-reposition only — **not**
      because of a fixed pixel-mapping (that's crop mode's reason), but
      because the control has no reliable self-relayout contract to invoke
      safely. This is a **conservative default for v1**, not a proven
      technical impossibility — some child controls (e.g. a modern
      WinForms `UserControl` with proper anchor/dock logic) may well
      handle being resized directly just fine, but there is no reliable
      way to detect this in advance, so treating all ancestor-chain
      child-HWND picks as fixed-size avoids a class of hard-to-predict
      per-app breakage. Revisit only if real usage shows this default is
      too restrictive for common cases.
    - **Now user-configurable, not just a hardcoded default (added after a
      direct design-review question about this exact tradeoff) — see §9's
      new "Allow resizing reparented child elements" setting.** The
      fixed-size behavior described above remains the v1 **default**, but
      an opt-in, off-by-default Settings toggle (with an explicit
      compatibility-risk warning, mirroring §4's pattern) lets a user
      override it per-app at their own risk once they've confirmed a given
      app's child control tolerates external resize. This does **not**
      change the underlying reasoning above (still no reliable way to
      detect tolerance in advance) — it just avoids hard-coding a
      one-size-fits-all rule with literally no escape hatch.
  - **Action: the host frame's resizable/fixed-size behavior must be
    conditional on which of the three pick types was used** — whole
    top-level window (resizable), ancestor-chain child HWND (fixed-size,
    new v1 default per above), or crop mode (fixed-size, existing reason)
    — decided at reparent time, not a global app-wide setting or a simple
    two-way "cropped vs. not" check.

### 6.6 Phased (incremental) picking model — not batch

- **Decision: reparenting is phased/incremental, not a batch/multi-select
  commit.** Each pick is applied immediately (one `SetParent` operation
  completes fully — save state, reparent, position — before the user does
  anything else). This was chosen over a "queue several picks, then commit
  all at once" batch model because:
  - It avoids the ordering/overlap complexity of batching (e.g., a crop
    target and a separately-reparented sibling control spatially overlapping
    — see §6.9 "Crop + sibling-child overlap" for the underlying problem this
    sidesteps).
  - It matches the proven PowerToys pattern (one pick → one reparent → done)
    and gives the user immediate, honest feedback of each action's real
    effect rather than a delayed batch result they must reason about in
    advance.
- **Picking additional children from the same original window afterward:**
  because reparenting a *child control* (not the whole top-level window)
  only affects that one HWND, the **original parent window is not
  automatically hidden** after a child is pulled out of it — it stays
  visible (now partially "hollowed out" where that child used to render),
  so the user can immediately invoke the picker again on it, or on any other
  window, at any time. No special "re-summon" step is needed for this case.
- **Whole-window reparenting still hides the original entirely** (see §7) —
  there is nothing left of it to interact with, so hiding it is correct and
  unambiguous there.
- **"Open original for more picking" button** (see §6.7) exists for the
  whole-window-hidden case specifically, to temporarily bring the hidden
  original back so the user can pick more of *its* children without fully
  restoring/un-reparenting anything already embedded elsewhere.

### 6.7 Host-frame control strip (replaces PowerToys' native titlebar)

- Instead of PowerToys' always-visible native titlebar on the crop/host
  window, use a small **auto-hiding overlay control strip** — a topmost,
  layered sibling window rendered above the embedded content, hidden by
  default and revealed on hover near the top edge (or a corner) of the host
  frame, similar to fullscreen video-player controls.
- Because the embedded window fills the host frame's entire client area,
  this strip must be its own distinct overlay HWND layered on top — you
  cannot draw controls "on top of" someone else's window content from
  within that same window.
- Buttons on the strip:
  - **Close/Restore** — runs `RestoreOriginalState()` for this one
    reparented target: `SetParent(target, nullptr)`, restore original
    style/placement, close this host frame.
  - **"Open original for more picking"** — relevant when the *whole*
    original window is currently reparented/embedded (see §7): note that
    because whole-window mode makes the target itself a `WS_CHILD` of the
    host frame, there is no separate hidden copy to simply re-`ShowWindow` —
    the target **is** the embedded content. This action must therefore
    perform a **temporary full unparent**: `SetParent(target, nullptr)` +
    restore its original style/placement (same code path as
    `RestoreOriginalState()`, but without discarding the saved state or
    closing the host frame), then `SetForegroundWindow(target)`. While this
    is active, the host frame necessarily shows nothing/a placeholder for
    that slot (its content has been temporarily pulled back to top-level),
    which must be surfaced to the user (e.g. host frame shows "original
    temporarily reopened for picking" instead of going blank/confusing).
    Once the user either picks a child element from it or clicks "Hide
    original again," re-apply the exact same reparent steps used for the
    original pick (§8 steps 4–5) to re-embed it into the same host frame,
    so the user can invoke the picker on it again, without touching
    anything already reparented elsewhere.
    - **Toggle behavior:** once clicked, this button's label/icon changes to
      **"Hide original again"** for as long as the original is temporarily
      unparented/visible. Clicking it a second time (having picked nothing
      new) re-applies the same reparent steps (§8 steps 4–5) to re-embed it
      into the host frame — a clean, symmetric undo path with no side
      effects.
    - **Safety net:** if the user enters picker mode via this flow and then
      cancels (`Escape`) without picking anything, automatically re-embed the
      original (same re-reparent steps) as part of the cancel action too
      (treat "opened for picking" as a transient state that reverts if
      nothing was picked). Avoid relying on focus-loss (`WM_KILLFOCUS`) to
      auto-hide — too implicit, would surprise a user who just alt-tabs away
      briefly.
    - Track a simple per-reparented-window-group boolean (e.g.

      `IsOriginalTemporarilyVisible`) so the strip knows which label/action
      to display.
  - **Adjust crop** (only if this target was reparented via crop mode) —
    re-invokes the crop-rect drag UI scoped to the same target, without a
    full restore/redo cycle.
- This control strip can double as whatever minimal host-frame chrome
  WindowWorks needs anyway (resize handles, drag handle for repositioning
  within a layout) — resolves the "exact host-frame chrome design" open
  question from a fixed titlebar to an auto-hide overlay strip. **Resize
  handles specifically only apply to whole-top-level-window targets — see
  §6.5's three-way fixed-size finding (crop mode verified against real
  PowerToys source: `WS_THICKFRAME`/`WS_MAXIMIZEBOX` explicitly stripped
  for its crop-mode-only host window; ancestor-chain child-HWND picks are a
  separate, v1-conservative fixed-size default for a different reason —
  lack of a reliable self-relayout contract on arbitrary native child
  controls).** Both a crop-mode host frame's and an ancestor-chain
  child-HWND host frame's control strip should render a drag handle
  (repositioning within a layout doesn't affect either constraint) but
  omit resize grips/affordances entirely; only a whole-top-level-window
  host frame's strip shows both drag and resize affordances.

### 6.8 Multiple children pulled from the same original window

- Reparenting is technically possible for two or more **distinct native
  child HWNDs** of the same original parent (e.g. a splitter with two real
  child controls) — each `SetParent` call only affects that one HWND, so
  pulling out child A into host frame 1 and child B into host frame 2 is
  independent and non-conflicting.
- **Only applies to native Win32 apps with real child-HWND siblings** — in
  modern frameworks (WPF/UWP/Electron/Chromium) there is only ever one HWND
  regardless of which "control" the user hovers, so this scenario doesn't
  arise there (there is nothing to split apart).
- Consequence: the original parent window may end up progressively more
  "hollowed out" in appearance (blank space where each pulled child used to
  render) as the user picks more of its children over time — this is
  expected/accepted behavior for the phased model (§6.6), not a bug.
- The original parent's own layout code (resize handlers, splitters) may not
  know its children were removed, and could error/no-op if it later tries to
  reposition them — a known compatibility risk, consistent with §4's general
  compatibility warning, not something WindowWorks can fully prevent.
- **Per-parent hide/restore tracking:** for the whole-window-hide case, track
  how many children have been pulled out of a given original parent, and
  only make sense of "hide the original" as a concept once the user
  explicitly indicates they're done with it — see §7 for the distinction
  between whole-window hiding (always hidden while embedded) vs.
  partial/sibling-children hiding (stays visible; hiding is not automatic).

### 6.9 Crop + sibling-child overlap (known interaction risk)

- If a crop-mode target spatially overlaps the original bounding rect of a
  child control that is *separately* reparented elsewhere (from the same
  original window), the crop shows a live view — after the child is pulled
  out, the cropped view will show **blank/empty space** where that control
  used to render, since crop is a live clip of whatever currently renders,
  not a static snapshot.
- Because reparenting is phased (§6.6), this is somewhat self-mitigating —
  the user sees the live effect of each pick immediately, including any
  resulting blank space in an already-open crop view, rather than discovering
  it only after a delayed batch commit.
- Still worth a **soft warning** (not a hard block) if the picker detects
  that a newly-selected reparent target's rect overlaps a rect already shown
  in an active crop-mode host frame — e.g. "This overlaps a region shown in
  another reparented crop; that view may now show empty space."

## 7. User-facing visibility behavior (important — not just implementation detail)

- **Whole-window reparenting:** while the reparented window is embedded, it
  is NOT independently visible or interactable anymore. Because `SetParent`
  makes it a child of the host frame, it disappears from the desktop as a
  separate top-level window (no longer in Alt-Tab/taskbar as itself) and is
  only visible/usable *through* the host frame's view. This matches
  PowerToys Crop and Lock's actual behavior — the original window is hidden
  the moment reparenting occurs.
- **The only way to see/use the original window independently again is to
  close the host frame** (or use "Open original for more picking" per
  §6.7/§6.6 for a temporary peek), which triggers `RestoreOriginalState()`
  and `SetParent(target, nullptr)`, making it a top-level window again at
  its restored position/placement.
- **Sibling/child-control reparenting (§6.8) is different:** only the
  specific child HWND is reparented; the original parent window is **not**
  hidden and remains visible on the desktop the whole time, now missing
  whichever children were pulled out of it (visible as blank/hollowed-out
  space). Hiding it is not automatic in this case — it stays visible so the
  user can keep picking more children from it at any time (phased model,
  §6.6).
- This reinforces why crash cleanup (§8 step 9) matters: if WindowWorks
  exits abnormally while a window is reparented, that window is effectively
  "lost" (an invisible child of a dead/closing host) until the crash-recovery
  state file restores it on next launch.
- Worth surfacing this plainly in the picker/confirm UI (e.g., a short note
  like "The original window will be hidden until you close this frame" for
  whole-window mode) so users aren't surprised when the source window
  vanishes from the taskbar.

## 8. Reparent / restore mechanics (both whole-window and crop paths)

> **Source-verified against actual PowerToys code (not just secondhand
> description):** the sequences below were checked directly against the
> real, shipped `ReparentCropAndLockWindow.cpp` /
> `ChildWindow.cpp` / `OverlayWindow.cpp` (as of the `main` branch,
> `microsoft/PowerToys`, fetched and read in full during a later review
> pass). **An earlier draft of this section had "corrected" PowerToys'
> ordering based on generic Win32 theory (WS_POPUP/WS_CHILD mutual
> exclusivity, SetForegroundWindow-doesn't-affect-child-focus) without
> checking the real source first — those theoretical corrections turned out
> to contradict what Microsoft actually ships and has apparently tested in
> production.** The steps below now match the real source. If a future
> implementer still wants to deviate from PowerToys' order for a
> WindowWorks-specific reason, that's fine — but it should be a deliberate,
> tested decision, not an untested theoretical "fix" like the earlier draft.

1. `DisconnectTarget()` first if something is already reparented into this
   host slot.
2. `SaveOriginalState()` — `GWL_EXSTYLE`, `GWL_STYLE`, `WINDOWPLACEMENT`,
   `GetWindowRect`. **For ancestor-chain child-HWND picks specifically
   (not whole-top-level-window picks) — a gap found during a later review
   pass, prompted by a question about resize + restore interaction:** also
   save the **original parent HWND** via `GetParent(target)` at pick time.
   PowerToys itself never needs this because its only reparent mode always
   makes the *whole* target window a child of the crop host — it has no
   concept of picking a native child control that has a real, separate
   original parent to restore back into. This plan's ancestor-chain
   child-HWND picking is new scope beyond what PowerToys does, so this
   detail has no PowerToys precedent to copy — it must be designed here.
3. (Crop mode only) compute DPI-aware crop geometry, handling maximized
   windows specially via monitor work-area rect + per-monitor DPI (see the
   detailed maximized-window handling note below — verified against the
   real `CropAndLock()` implementation).
4. **Reparent, then add `WS_CHILD` (matches actual shipped order —
   `SetParent` first, style change second):**
   `SetParent(target, hostChildWindow)`, then
   `SetWindowLongPtrW(target, GWL_STYLE, GetWindowLongPtrW(target, GWL_STYLE) | WS_CHILD)`.
   **Note this is the opposite order from an earlier draft of this plan,
   which (incorrectly, based on untested Win32 theory about `WS_POPUP`
   exclusivity) called for setting styles before `SetParent`.** PowerToys
   does not clear `WS_POPUP` at all — it only ORs in `WS_CHILD`, leaving
   any pre-existing `WS_POPUP` bit as-is. Follow the real precedent here.
5. `SetWindowPos(target, nullptr, x, y, 0, 0, SWP_NOSIZE | SWP_FRAMECHANGED | SWP_NOZORDER)`
   to reposition the target within the host's client area (`x`/`y` are the
   negative crop-rect offset in whole-window mode this is just `0, 0`) —
   `SWP_FRAMECHANGED` is required to force Windows to re-evaluate the
   non-client area/frame after the `GWL_STYLE` change in step 4; omitting
   it can leave stale non-client rendering (e.g. a ghost title bar/border)
   even though the style bits themselves are correct. This matches the
   real `SetWindowPos(...)` call in `CropAndLock()`.
6. On failure (`SetWindowPos` returning 0), surface a warning (not
   necessarily a `MessageBox` — could be an inline status in the
   WindowWorks UI, per §9's preference for in-UI notices over popups)
   noting the target app might not handle reparenting well. PowerToys
   itself uses a blocking `MessageBoxW` with exactly this wording at this
   exact failure point — WindowWorks should keep the same trigger
   condition but present it via the app's own notice/status UI instead of
   a modal box, consistent with the Settings-level compatibility-notice
   approach in §4/§9.
7. **Activation/focus handling (verified against real source — differs
   from an earlier draft of this plan):** the actual shipped mechanism is
   **not** `SetFocus` on the target, and does **not** activate the host
   frame — it calls `SetForegroundWindow` **directly on the embedded child
   target itself**, from two places in the host frame's window procedure:
   - `WM_MOUSEACTIVATE`: if the target is set and not already the
     foreground window, call `SetForegroundWindow(target)`, then return
     `MA_NOACTIVATE` (tells Windows not to also activate the host frame
     itself — the child target becomes foreground instead).
   - `WM_ACTIVATE` with `wparam == WA_ACTIVE`: also calls
     `SetForegroundWindow(target)` again (a second, redundant-looking but
     apparently necessary trigger point, since activation can arrive via
     either path depending on how the host frame was brought to the
     foreground — e.g. clicked vs. Alt-Tabbed vs. programmatically shown).
   **This means `SetForegroundWindow` does work on a `WS_CHILD` window in
   practice for this purpose, contrary to the generic Win32 guidance an
   earlier draft of this plan relied on** — it's worth still verifying
   this manually against .NET/WPF's own message loop (WindowWorks' host
   frame will likely be a WPF `Window`, not a raw Win32 `HWND` class like
   PowerToys' — see `HwndSource`/`WndProc` hook needed to intercept
   `WM_MOUSEACTIVATE`/`WM_ACTIVATE` at the Win32 level, since WPF doesn't
   expose these directly), but the **target API call itself
   (`SetForegroundWindow` on the child) should be trusted as the correct,
   proven approach** rather than re-theorizing an alternative. Explicit
   manual test: after pop-out, click into the host frame and confirm
   keyboard input actually reaches the embedded target, not just mouse
   input.
8. **Restore path** (`RestoreOriginalState`/`DisconnectTarget`) — **verified
   order from real source (reverses an earlier draft of this plan, which
   had guessed the opposite order from Win32 first-principles reasoning
   without checking the real code):**
   1. `SetWindowPos(target, nullptr, originalRect.left, originalRect.top, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED)`
      — restore the original screen rect **while the window is still a
      child** (coordinates here are interpreted relative to the *current*
      parent, i.e. the host's child window, at this point in the
      sequence — this works in the real implementation because the
      values being restored are absolute screen coordinates and
      `SetWindowPos` on a child accepts parent-client-relative
      coordinates that, for this specific restore step, PowerToys treats
      as substitutable with the original screen rect; this detail should
      be tested carefully in WindowWorks' own implementation since it is
      easy to get backwards — trust the order, verify the coordinate
      interpretation manually).
   2. `SetParent(target, nullptr)` — make it top-level again. **Correction
      for ancestor-chain child-HWND picks (not whole-top-level-window
      picks) — this line is only correct for whole-window restores.** A
      child-HWND pick's original state was **not** top-level — it was a
      child of some other window (its original parent, saved per step 2 of
      the reparent sequence above). For this case, the restore call must
      instead be `SetParent(target, originalParentHwnd)` — reparenting
      back into its real original parent, not to the desktop. **Before
      doing so, verify `originalParentHwnd` is still valid** (`IsWindow`,
      plus the same PID/creation-time identity-verification protocol
      already specified for crash recovery below — the original parent
      could itself have been closed/recreated in the meantime, e.g. the
      whole app was closed and relaunched while the child was embedded
      elsewhere). If the original parent is no longer valid, fall back to
      making the target top-level (`SetParent(target, nullptr)`) instead
      of silently failing or leaving it parented to a stale handle — a
      recovered top-level window the user can see and deal with manually
      is strictly better than a `SetParent` call that either fails
      silently or (worse) succeeds against a recycled HWND that's now an
      unrelated window (see the HWND-reuse hazard already documented for
      crash recovery below — the same hazard applies here to
      `originalParentHwnd`, not just to the target's own saved HWND).
      PowerToys has no precedent for this branch at all (see the note on
      step 2 above) — this must be designed and tested specifically for
      WindowWorks, not copied from real source.
      - **New edge case found during this review pass — the original
        parent being independently reparented while the child is
        detached:** identity-verification alone (PID/creation-time/class
        match) confirms `originalParentHwnd` is still the *same window*,
        but does **not** confirm it is still in its *original state* —
        specifically, the original parent could itself have since been
        picked as a **whole-window** target and reparented into a
        different WindowWorks host frame (e.g. the user detached child A
        from window P, then later separately pop-out-reparented window P
        itself). If child A is then restored via `SetParent(target,
        originalParentHwnd)`, it becomes a grandchild — a `WS_CHILD` of
        `originalParentHwnd`, which is itself now a `WS_CHILD` of some
        other host frame — nested two levels deep, likely still
        functional (Win32 permits arbitrary `WS_CHILD` nesting depth) but
        an unexpected, untested topology and a confusing user-visible
        state (the restored child would appear to have vanished, since
        it's now only visible if the user also has that other host frame
        open). **Mitigation: before restoring, additionally check
        `originalParentHwnd` against WindowWorks' own reparented-windows
        tracking list** (the same in-memory list used everywhere else in
        this section) — if `originalParentHwnd` is itself currently a
        tracked/active reparent target, treat this the same as the
        already-invalid-parent case above (fall back to
        `SetParent(target, nullptr)`, making the restored child top-level
        instead of silently nesting it inside another host frame), and
        surface an inline notice explaining why (e.g. "The original
        window for this element has itself been reparented elsewhere; it
        was restored as a standalone window instead."). This is a cheap
        check (one lookup against an already-in-memory list) and avoids
        an untested, confusing nested-reparent topology.
   3. `SetWindowPlacement(target, &originalPlacement)` — restore the
      original placement (with `showCmd` forced to `SW_RESTORE` unless the
      original was `SW_SHOWMAXIMIZED`, in which case the maximized state is
      preserved as-is).
   4. `SetWindowLongPtr(target, GWL_EXSTYLE, originalExStyle)` and
      `SetWindowLongPtr(target, GWL_STYLE, originalStyle & ~WS_CHILD)` —
      styles restored **last**, after unparenting and placement.
    - **i.e. the real, tested order is: rect/`SetWindowPos` → unparent →
      placement → styles-last — not "styles → unparent → placement/rect" as
      an earlier draft of this plan claimed.** This ordering has been
      shipped and used in production PowerToys; treat it as the reliable
      default sequence for WindowWorks' own `RestoreOriginalState()`
      implementation, and mirror this exact order in the crash-recovery
      pass (§14 Phase 1) as well.
   - **Additional risk specific to resizable child-HWND picks (§9's "Allow
     resizing reparented child elements" setting) — a second, compounding
     risk found in response to a direct question, distinct from the
     structural original-parent gap fixed in step 2 above:** step 1's
     `SetWindowPos` restores the target to `originalRect` — its size
     **before** it was ever reparented, which is correct and unaffected by
     any resizing that happened while embedded (the saved original size is
     never overwritten by an in-session resize). So the target itself
     *does* end up back at its correct pre-reparent size. **The remaining
     risk is entirely on the original parent's side, not the target's:**
     the original parent's own layout/docking code (splitters, anchors,
     table-layout panels) was never notified about anything that happened
     while the child was gone — it has no idea the child was ever resized,
     and depending on how that layout code works, it may (a) correctly
     re-slot the restored child at its own expected size/position on its
     next layout pass (the good case — most docking/anchor layouts
     recompute from scratch on relayout), (b) leave stale gaps/overlaps if
     its layout is normally only triggered by its *own* `WM_SIZE`/child
     events rather than periodic recomputation (since none of those fired
     while the child was detached), or (c) in the worst case, especially
     for hand-rolled non-standard layout code, behave unpredictably if it
     cached the child's old size/rect somewhere and never expected it to
     change out from under it. **This is a strict superset of the
     already-documented §6.8 risk** ("the original parent's own layout
     code may not know its children were removed, and could error/no-op if
     it later tries to reposition them") — resizing while detached adds a
     *second* way the parent's assumptions can be violated (size changed,
     not just presence), on top of the existing removal-was-never-signaled
     risk. **No new mitigation beyond what's already in place is proposed
     here** (this remains covered by §4's general compatibility-risk
     warning and §9's dedicated resize-setting warning) — this is
     documented explicitly so a future implementer/tester knows to
     specifically test "resize child while embedded, then restore it, and
     check the original parent's layout recovers sensibly" as its own
     scenario, rather than only testing the target's own restored size
     (which will look correct) and missing that the *parent's* reaction is
     the actual open question.
9. **Crash recovery:** maintain a small on-disk **JSON state file** (plain

	file under `%APPDATA%\WindowWorks\`, following the exact same pattern as
	the existing `settings.json`/`presets.json` via `Persistence.cs` — this is
	**not** the Windows Registry, just a tracked list persisted to disk) of
	currently reparented windows (HWND/process identity + original state) so
	that on next WindowWorks launch, any orphaned reparented windows can be
	detected and offered for restoration/un-parenting, in case the app
	crashed before step 8 ran. See §14 Phase 1 for the detailed reliable-
	recovery design (atomic writes, HWND-liveness-check-first restore).
10. **"Reset All" integration:** WindowWorks already has an existing
	emergency-reset mechanism — the tray menu's **"Reset All"** item
	(`TrayController.cs`) calls `_auditLog.EmergencyReset(_windowManager)`,
	alongside a similarly-scoped `HotkeyEmergencyReset` global hotkey path
	(see `HotkeyManager.cs`/`HotkeyApplyService.cs`). Reparenting cleanup
	**must hook into this same existing mechanism** rather than introducing a
	separate reset path:
	- Extend `AuditLog.EmergencyReset` (or add a sibling call invoked
	  alongside it from the same tray menu handler / hotkey handler) to walk
	  the in-memory reparented-windows tracking list and call
	  `RestoreOriginalState()` for every currently-reparented window
	  (internally branching per step 8's per-pick-type logic —
	  `SetParent(target, nullptr)` for whole-window picks,
	  `SetParent(target, originalParentHwnd)` for ancestor-chain child-HWND
	  picks — found missing from this integration point during this review
	  pass; simply calling `SetParent(target, nullptr)` for every entry here
	  would incorrectly leave restored child controls top-level instead of
	  back in their original parent), symmetric with how it already restores
	  other recorded window-state snapshots.
	- This also naturally covers "user got into a broken state and just
	  wants everything back to normal" — consistent with the existing
	  "Are you sure you want to reset all window changes and snapshots?
	  This cannot be undone." confirmation dialog pattern already shown for
	  "Reset All".
	- Also un-hide any whole-window-hidden originals (§7) as part of this
	  reset, and close/destroy any host frames that were showing reparented
	  content.
11. **"Reset Reparenting" (scoped reset) — new tray menu item:** in addition
	to the global "Reset All" hook above, add a dedicated, narrower reset
	entry specific to this feature — mirroring the existing precedent of
	**"Reset Click-Through"** (`TrayController.cs`, a scoped sibling to
	"Reset All" for just the click-through subsystem, calling
	`_clickThroughManager.ResetAllClickThrough()`). "Reset Reparenting" would:
	- Restore every currently-reparented window via `RestoreOriginalState()`
	  (same per-pick-type branching as step 10 above — child-HWND entries
	  restore to their original parent HWND, not top-level) for each entry
	  in the reparented-windows
	  tracking list), un-hide any whole-window-hidden originals, and
	  close/destroy all reparent host frames — without touching unrelated
	  window-state changes (opacity, click-through, other snapshots) that
	  "Reset All" would also affect.
	- Show its own confirmation dialog, following the same
	  `MessageBox.Show(..., MessageBoxButtons.YesNo, MessageBoxIcon.Warning)`
	  pattern already used by both "Reset All" and "Reset Click-Through" in
	  `TrayController.cs`.
	- Useful when the user only wants to undo reparenting specifically (e.g.
	  after experimenting with "Pop Out and Reparent" / "Crop and Reparent")
	  without resetting other unrelated in-progress window customizations.
	- Should be disabled/grayed out (or simply omitted from the menu) when
	  no windows are currently reparented, to avoid a no-op confirmation
	  dialog.

## 9. Settings integration

- Add a Settings section/page for this feature named **"Window Reparenting"**
  (formal/technical name for the settings UI), containing:
  - **Master enable/disable toggle** for the whole feature.
  - **Two independent sub-toggles**, both **enabled by default**:
	- **"Pop Out and Reparent"** — the whole-window reparent action (§6, primary flow).
	- **"Crop and Reparent"** — the crop-refinement action (§6.5).
  - **New: "Allow resizing reparented child elements" — a third, narrower
	setting, added in response to a direct design-review question about
	child-HWND resize risk (§6.5's three-way fixed-size finding).**
	- **Default: OFF (unchecked)** — deliberately opt-in, not opt-out,
	  unlike the two sub-toggles above. This is the correct default because
	  the risk here is qualitatively different from the two toggles above:
	  those two gate *whether an action exists at all*; this one, if
	  enabled, actively removes a safety rail (§6.5's fixed-size default)
	  from content that's already been successfully reparented, on the bet
	  that a specific app's child control happens to tolerate external
	  resize — something we have no reliable way to verify in advance. An
	  opt-in-only default keeps v1's out-of-the-box behavior fully
	  conservative while still giving advanced users (who've personally
	  confirmed a given app handles it fine) an escape hatch, rather than
	  permanently hard-coding the fixed-size rule with no override at all.
	- **Scope: applies only to ancestor-chain child-HWND picks (§6.5),
	  never to crop-mode targets.** Crop-mode's fixed-size constraint is
	  for an entirely different, non-negotiable reason (the crop rect is a
	  fixed pixel-mapping — resizing it is geometrically meaningless, not
	  just "risky"), so this setting must not affect crop-mode host frames
	  at all — no wording or UI placement should imply it does. Whole-
	  top-level-window picks are already resizable unconditionally and are
	  also unaffected (nothing to toggle there).
	- **Effect when enabled:** an ancestor-chain child-HWND host frame
	  gains resize grips/maximize affordance (mirroring the whole-window
	  case) instead of being fixed-size; the existing Phase 1
	  `SetWindowPos`-re-application-on-resize mechanism (already built for
	  whole-window targets) is reused for these targets too, rather than
	  needing new resize-handling code — this setting only changes which
	  targets *opt into* using that already-built mechanism.
	- **Required accompanying warning (mirrors §4's existing compatibility
	  notice pattern, not a new UX pattern) — updated to also cover the
	  restore-time risk found via a later review question, not just the
	  live-resize risk:** when the user enables this toggle, show inline
	  warning text in the Settings UI — something like *"Resizing a
	  reparented UI element (not a whole window) may cause visual
	  corruption or clipped content while embedded, and may also cause the
	  original window's layout to look wrong when this element is restored
	  back to it, since the original window has no way to know the element
	  was resized while it was gone. Only enable this if you've confirmed
	  the specific app handles it correctly."* Consistent with this plan's
	  existing preference (§4) for an in-Settings persistent notice over a
	  per-action `MessageBox`. See §8's restore-path notes for the detailed
	  technical reasoning behind the restore-time half of this warning.
	- **Toggle-change timing:** follows the same rules already established
	  above for the two existing sub-toggles — disabling this setting
	  while a child-HWND host frame is already resizable does **not**
	  forcibly shrink/lock it retroactively (consistent with "toggle
	  changes never affect already-reparented content's basic
	  availability" below), it only affects newly reparented picks going
	  forward. (Retroactively toggling live window styles on an
	  already-open host frame is unnecessary complexity for a low-traffic
	  settings change — a full Close/Restore-then-repick already gives the
	  user a way to apply the new setting if they want it applied to an
	  existing pick.)
	- **Is this setting worth adding? Yes — reasoning:** it's low
	  implementation cost (only flips which of the two already-designed
	  per-pick-type code paths in §6.5/§6.7 applies, no new resize
	  mechanism to build) and it turns a hard-coded, occasionally
	  overly-conservative rule into a user-controllable one for the subset
	  of users who actually need it, without weakening the safe-by-default
	  behavior for everyone else. This is a meaningfully different
	  trade-off from adding a whole new *feature* — it is much closer to
	  "expose an existing internal decision as a setting," which is cheap
	  to justify.
  - **If a sub-toggle is disabled, its corresponding action must not appear
	anywhere in the UI** — specifically:
	- If "Pop Out and Reparent" is disabled: the picker's ancestor-chain yellow
	  **confirm boxes** (§6.3) must not be shown/clickable for direct
	  whole-window/child-HWND selection; if both sub-features end up disabled
	  this way, the picker shouldn't be invocable at all (its entry
	  point/hotkey should be inert or hidden — no empty picker with nothing
	  to do). **Clarification (avoids an edge-case gap when only "Crop and
	  Reparent" is enabled):** disabling "Pop Out and Reparent" only hides
	  the per-ancestor-level *confirm* boxes — it does **not** disable the
	  underlying hover/ancestor-chain *discovery* (§6.2) itself, since "Crop
	  a region instead" (§6.3) still needs to know which ancestor level was
	  most recently highlighted in order to scope the crop-rect mode (§6.5)
	  to the correct HWND. In this configuration the picker still highlights
	  ancestor levels on hover as normal, but the only clickable confirm box
	  shown is "Crop a region instead" — no per-level whole-window/child-HWND
	  boxes.
	- If "Crop and Reparent" is disabled: the distinct "Crop a region
	  instead" box (§6.3) must not appear at the bottom of the yellow-box
	  stack, and the crop-rect drag UI (§6.5) must not be reachable.
	- Host-frame control-strip buttons (§6.7) tied to a disabled action
	  (e.g. "Adjust crop" when "Crop and Reparent" is off) must also be
	  hidden for any already-reparented content, not just blocked at
	  pick-time.
  - **Toggle changed while a picker session is currently open:** if the
	user opens Settings and disables the master toggle or a sub-toggle
	while a picker overlay is active (e.g. via a keyboard shortcut to
	Settings, or a second monitor), immediately cancel the active picker
	session as if `Escape` had been pressed (§6.1) — do not let a picker
	session continue offering an action that Settings just disabled. This
	is a simple, low-risk rule: treat any settings change as invalidating
	the current picker session outright rather than trying to dynamically
	re-render the yellow-box stack mid-session.
  - **Toggle changed while content is already reparented:** disabling the
	master toggle or a sub-toggle **never** affects already-reparented
	content's ability to be closed/restored — Close/Restore (§6.6) on an
	existing host frame must always remain available regardless of current
	toggle state, so the user is never left with reparented content they
	can't get back short of "Reset All"/"Reset Reparenting". Only the
	*creation* of new reparented content (via the picker) and
	toggle-specific in-host-frame actions (e.g. "Adjust crop") are gated by
	the toggle — restoring existing content is not. This mirrors the
	general design principle already established for "Reset
	Reparenting"/"Reset All" (§8 step 11/10): undo paths must never be
	disableable.
  - A persistent compatibility-risk notice (replaces the PowerToys
	`MessageBox` warning pattern) explaining that some apps may not handle
	reparenting well.
  - Possibly a per-app opt-out/allow list in a later iteration.

## 10. Explicitly out of scope for v1

- Sibling/z-order occlusion picking (choosing a window hidden behind another
  unrelated window at the same screen point).
- Live/tracking crop regions that follow in-page content movement (browser
  scroll/resize/layout changes) — crop rect is a static snapshot, matching
  PowerToys' own limitation.
- True DOM/UIA sub-element reparenting (not possible via `SetParent`; only
  real HWNDs can be targeted).
- Reparenting the exact **same HWND** into more than one host simultaneously
  (a single window/control can only ever be embedded in one host frame at a
  time — this is a hard Win32 constraint, not a design choice). This does
  **not** exclude picking multiple *different* child HWNDs out of the same
  source parent window into separate hosts, which is explicitly supported
  (§6.8, Phase 3) for native apps with real child-control siblings.

## 11. Terminology / naming

- **"Window Reparenting"** — the formal feature name, used for the Settings
  section title and in technical documentation/design discussion.
- **"Pop Out and Reparent"** — user-facing action label for whole-window
  reparenting (the primary picker flow in §6.1–§6.4, §6.6–§6.8). Named
  symmetrically with "Crop and Reparent" so both actions visibly read as
  variants of the same underlying "Reparent" mechanism, differing only in
  scope (whole window vs. cropped region).
- **"Crop and Reparent"** — user-facing action label for the crop-refinement
  flow (§6.5), shown as the distinct bottom entry in the picker's yellow-box
  stack (§6.3).
- Internal code/docs may continue to use "reparent"/"reparenting" as the
  precise technical term for the underlying `SetParent`-based mechanism
  regardless of which user-facing label triggered it.

## 12. Additional considerations for planning

### Process/session lifecycle
- **Target app closes while reparented** — detect via a WinEvent hook
  (`EVENT_OBJECT_DESTROY`) on the target HWND so the host frame can clean up
  its slot gracefully instead of showing a blank/dead frame.
- **WindowWorks exits normally** — proactively call `RestoreOriginalState()`
  for all active reparents on graceful shutdown; don't rely solely on the
  crash-recovery state file for the happy path.
- **Host frame resize/move** — when the WindowWorks host frame is
  resized/dragged as part of a layout, re-apply `SetWindowPos` to the
  embedded child to fill the new client area; for crop mode, scale/reposition
  the crop offset accordingly.

### Presets/persistence
- HWNDs are not stable across restarts or app relaunches — a preset that
  references a reparented-window slot must key on something durable (process
  path + window title/class pattern) and re-run picker/matching logic on
  preset load, not store a raw HWND.
- Decide whether reparented-window slots are preset-persistable in v1 at
  all, or restricted to "live session only" (simpler, safer starting point).

### Permissions/security
- `SetParent` fails across integrity levels — reparenting an elevated target
  requires WindowWorks itself to run elevated, which has broader UX/security
  implications (UAC prompt every launch). Default posture should likely be
  "detect elevated targets and warn/skip" rather than requiring WindowWorks
  to always run elevated.

### DPI/multi-monitor correctness
- Both the picker overlay and host frame need per-monitor-DPI awareness
  (`PerMonitorV2`) so highlight rects and crop math stay accurate
  when the cursor/host crosses monitors with different scaling.
  - **Verified against the actual PowerToys source (new finding — corrects
    an earlier assumption in this plan that a manifest declaration is *the*
    mechanism):** PowerToys' CropAndLock does **not** declare DPI-awareness
    via its `app.manifest` at all — its `app.manifest`
    (`src/modules/CropAndLock/CropAndLock/app.manifest`) contains only a
    Windows-10-`supportedOS` compatibility entry, no `dpiAware`/
    `dpiAwareness` element whatsoever. Instead, `main.cpp` calls
    `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`
    **explicitly in code**, once, near the very start of `wWinMain`, before
    creating any windows. **This is a real, viable alternative to a
    manifest declaration** worth calling out explicitly: WindowWorks could
    either rely on the WPF SDK's default-generated manifest (if confirmed
    `PerMonitorV2`, see below) or call
    `SetProcessDpiAwarenessContext`/`SetProcessDpiAwareness` explicitly
    during startup (e.g. in `Program.cs`/`App.xaml.cs`, before any window
    is created) as a more explicit, self-documenting alternative that
    doesn't depend on inspecting a generated manifest to confirm — this is
    the pattern Microsoft's own PowerToys team chose for this exact
    feature, so it should be considered the **preferred, proven approach**
    rather than a fallback.
  - **The genuine source-code comment worth citing directly** (from
    `main.cpp`, immediately before the `SetProcessDpiAwarenessContext`
    call): *"reparenting a window with a different DPI context has
    consequences"* — linking to the official `SetParent` MSDN remarks. This
    directly corroborates a risk this plan had already flagged
    independently (target/host-frame DPI-awareness-context mismatch during
    `SetParent`), now with a citable, authoritative source confirming
    Microsoft's own reparenting utility considers this a real concern
    worth a code comment, not a theoretical worry.
  - **Still true from the earlier pass (unresolved, not superseded by the
    finding above):** `WindowWorks.App.UI.csproj` (the WPF UI project,
    `Microsoft.NET.Sdk.WindowsDesktop`, `UseWPF=true`) has **no explicit
    `app.manifest`/`ApplicationManifest` entry and no explicit DPI-awareness
    property**. Modern SDK-style WPF projects (`net10.0-windows`,
    `UseWPF=true`) generate a default manifest with `PerMonitorV2`
    DPI-awareness automatically at build time unless overridden — so this
    is **very likely already correct by default**, but it has **not been
    independently confirmed by inspecting the actual generated/embedded
    manifest** (e.g. via `mt.exe`/Manifest Tool or `Get-AppLockerFileInformation`
    on the built `.exe`, or simply checking `obj\<config>\...\app.manifest`
    after a build). **Action item for Phase 1 (cheap, do this before relying
    on it):** during Phase 1 implementation, explicitly verify the built
    `WindowWorks.App.UI.exe`'s effective DPI-awareness (e.g. via Task
    Manager's "DPI Awareness" column, or `GetDpiAwarenessContextForProcess`)
    is `Per Monitor v2` before trusting `GetDpiForWindow`-based math
    elsewhere in this plan (§8, §12). **If it is not** (or even if it is,
    for the extra explicitness/self-documentation value), prefer adding an
    explicit `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`
    call at startup (matching PowerToys' own approach, see above) over
    relying purely on manifest generation — it's one line, removes the
    ambiguity, and is exactly what Microsoft's own equivalent feature does.
  - **Separately flagged (needs its own verification during
    implementation, not resolved here):** `HighlightOverlay`'s existing DPI
    conversion logic was reported by a review pass as applying a single
    target DPI to absolute virtual-screen coordinates, which would be
    incorrect if different monitors have different scaling — the new
    picker overlay (§6.1/§6.2, built as a separate class per the decision
    in §12/§13) must **not** copy that pattern as-is; it needs genuinely
    per-monitor DPI conversion (query DPI per-monitor via
    `GetDpiForMonitor`/`GetDpiForWindow` at the specific screen point being
    converted, not a single global DPI value applied uniformly). Confirm
    this by reading `HighlightOverlay.xaml.cs`'s DPI conversion code before
    reusing any of its math in the new overlay class.
  - Also note: `SetParent` itself can fail or produce incorrect scaling on
    modern Windows if the target and the new parent (host frame) have
    different effective DPI-awareness contexts — verify both host frame and
    common target apps' awareness contexts don't produce a mismatch during
    Phase 1 manual testing (§14 Phase 1 checklist), since this is a
    real-world compatibility risk distinct from the picker/overlay's own
    DPI math above.
  - **New finding (host frame `WM_DPICHANGED` handling — not previously
    documented in this plan at all):** verified via PowerToys' own shared
    `DesktopWindow<T>` base class (`robmikh.common`, which
    `ReparentCropAndLockWindow`/`ChildWindow` both derive from) that its
    **default** `WM_DPICHANGED` handler does exactly this:
    `SetWindowPos(m_window, nullptr, rect->left, rect->top, rect->right -
    rect->left, rect->bottom - rect->top, SWP_NOZORDER | SWP_NOACTIVATE)`,
    where `rect` is the suggested new window rect Windows passes in
    `lParam` of `WM_DPICHANGED`. **The host frame must handle
    `WM_DPICHANGED` the same way** (resize/reposition itself to the
    OS-suggested rect when dragged to a monitor with different scaling) —
    this was not previously called out in this plan as an explicit
    required message handler, and it matters for the reparented target
    inside it too: after the host frame itself resizes in response to
    `WM_DPICHANGED`, it must **also** re-run the equivalent of §8 step 5's
    `SetWindowPos` on the embedded target to keep it correctly filling/
    aligned within the host's (now differently-scaled) client area —
    otherwise the embedded content will be left at its stale pre-DPI-change
    size/position while the host frame chrome resizes around it. Add this
    as an explicit Phase 1 requirement: host frame's `WM_DPICHANGED`
    handler must (1) resize itself per the OS-suggested rect, then (2)
    recompute and re-apply the target's position/size within the new
    client area (whole-window mode: fill; crop mode: re-run the crop-rect
    math from §6.5 against the new DPI). If the host frame is WPF (per the
    open question in §13), this still requires the `HwndSource.AddHook`
    bridge already needed for `WM_MOUSEACTIVATE`/`WM_ACTIVATE` (§8 step 7)
    to also intercept `WM_DPICHANGED` — one more reason that bridge is
    needed regardless of which host-frame technology is chosen.
- **Multi-monitor scenarios specifically (as opposed to single-monitor DPI
  awareness) cannot currently be validated** — no multi-monitor test
  available. Single-monitor DPI-awareness work proceeds
  normally in Phase 1 (picker overlay/host frame); the cross-monitor
  validation pass is deferred to `docs/ROADMAP.md` as a backlog item to pick
  up once test hardware is available, rather than blocking any phase in this
  plan.

### Accessibility & discoverability
- Picker is mouse-driven by default; consider a keyboard fallback (Tab to
  cycle ancestor levels, Enter to confirm) for accessibility. (This keyboard
  fallback itself is Phase 5 scope; it is separate from the hotkey that
  *invokes* the picker, which is required in Phase 1 — see §14 Phase 1.)
- ~~Invoke the picker via a hotkey~~ — **moved to §14 Phase 1** (required
  deliverable, not later polish): a dedicated global hotkey via the existing
  `HotkeyManager` infrastructure is how the picker is invoked from Phase 1
  onward, not only via a settings/menu action.

### Testing/rollout
- Correctness is highly app-dependent and hard to unit-test — plan a manual
  test matrix across representative app categories (native Win32/MFC,
  WinForms, WPF, Electron/Chromium, a DirectX game) before shipping.
- ~~Consider gating the whole feature behind an experimental/opt-in flag~~ —
  **superseded by §14 Phase 1 decision:** both the master toggle and the
  "Pop Out and Reparent" sub-toggle ship **enabled by default**, not gated
  behind opt-in. The mitigation for compatibility risk is the persistent
  Settings-page notice (§9), not an opt-in gate.

## 13. Open questions for later

**Reviewed and reorganized — most items previously listed here were already
decided.** Of the original 14 entries: **4 are genuinely open** (no decision
made yet, listed below), **9 were resolved** during design (kept in a
separate reference list for traceability), and **1 was a testing reminder
mislabeled as an open question** (a cross-reference to an already-required
Phase 1 test case, not a decision at all — also moved to the reference list
below, kept distinct from the actual resolved decisions).

### Genuinely open (need a decision before/at the relevant phase)

- **Inline error/status surface for Phase 1 — needed before implementing
  the elevation-check and failure-handling paths.** The plan specifies
  several places needing a "clear inline message, not a `MessageBox`"
  (elevation-blocked attempt, §8 step 6's generic reparent-failure warning)
  but WindowWorks doesn't yet have an established non-modal
  inline-notification UI pattern to reuse (verified: no existing
  toast/banner/status-bar component found in the codebase). Decide whether
  to introduce a small reusable notification/toast component as part of
  Phase 1, or use something simpler (e.g. a temporary label on the picker
  overlay itself).
- **Detailed on-disk JSON schema for the crash-recovery state file**
  (exact field names, versioning strategy for future format changes) — the
  *location* is already decided (`%APPDATA%\WindowWorks\`, per §14 Phase 1),
  but the precise schema hasn't been drafted. Needed before Phase 1's
  crash-recovery code is written (small, mechanical decision, low risk).
- **Whether/how this integrates with the existing preset system**
  (`PresetManager`, `presets.json`) — e.g. can a preset reference a
  reparented-window layout slot, or is v1 restricted to live-session-only
  (§12 "Presets/persistence" leans toward live-session-only as the simpler
  starting point, but this hasn't been explicitly confirmed as final).
  Needed before Phase 5 (or earlier, if preset interaction turns out to
  affect Phase 1's tracking-list design — worth a sanity check at that
  point too).
- **Settings-page UI layout detail** — whether the master toggle + two
  sub-toggles (§9) should be rendered as three independent checkboxes or a
  single toggle with two visually-nested dependent checkboxes, plus exact
  wording/placement of the compatibility-risk notice. Purely a visual
  layout decision, needed before Phase 1's Settings integration work.

### Already resolved during design (kept for traceability only — not open)

- ~~Host frame window technology choice~~ — resolved: **WPF `Window`, with
  a `HwndSource.AddHook` bridge for the low-level `WM_MOUSEACTIVATE`/
  `WM_ACTIVATE`/`WM_DPICHANGED` interop (§8, §12).** Reasoning: the rest of
  `WindowWorks.App.UI` is already WPF (`HighlightOverlay`, `SettingsWindow`),
  so a WPF host frame keeps the control strip, drag/resize chrome, and
  Settings/tracking-list data binding consistent with the rest of the app
  and avoids re-implementing all of that in raw GDI/Win32. The interop cost
  is small and well-precedented — `HwndSource.FromHwnd(hwnd).AddHook(...)`
  is a standard pattern for exactly this kind of message interception, on
  the order of a few dozen lines, not a per-feature tax. A raw native HWND
  host frame's only real advantage (getting `WM_MOUSEACTIVATE` "for free"
  in its own `WndProc`) does not offset having to hand-build the rest of
  the host frame's chrome outside WPF. This was blocking Phase 0 coding
  start; it is no longer a blocker.
- ~~Exact host-frame chrome design~~ — resolved by §6.7 (auto-hiding overlay
  control strip, not a persistent native titlebar).
- ~~Whether the reparented host frame should be user-resizable~~ — resolved:
  **conditional on which of three pick types was used, not a binary
  cropped-vs-not split (corrected during a later review pass).**
  **Whole top-level window:** **yes, resizable** — same as any normal
  top-level window, since there's no fixed crop mapping to invalidate and
  the app already has its own `WM_SIZE` relayout (§6.5, §12 Phase 1 resize
  task). **Ancestor-chain native child HWND** (a nested control picked via
  §6.2, not the whole window): **no, fixed-size by v1-conservative
  default** — unlike a top-level window, a child control was typically
  only ever resized by its original parent's own layout/docking code and
  often has no self-relayout logic of its own; forcing a resize risks
  clipped/stretched/corrupted content with no reliable way to predict
  which controls tolerate it, so all such picks default to fixed-size for
  now (§6.5). **Crop-mode targets:** **no, fixed-size** — matches
  PowerToys' own verified behavior (`WS_THICKFRAME`/`WS_MAXIMIZEBOX`
  explicitly stripped in `ReparentCropAndLockWindow`'s constructor) because
  the crop rect is a fixed pixel-mapping computed once at crop time;
  resizing would either distort the captured region or require
  re-deriving the crop live, which PowerToys avoids by disabling resize
  entirely — WindowWorks does the same (§6.5/§6.7, Phase 2 task).
- ~~Where crop-mode targets by default when multiple ancestor levels
  exist~~ — resolved: whichever ancestor was last highlighted before
  clicking "Crop a region instead," not always the root.
- ~~Whether "Reset Reparenting" should offer per-window granularity~~ —
  resolved: no, single all-or-nothing action only (§8 step 11).
- ~~Whether Pop Out/Crop actions should be exposed via the command
  palette~~ — resolved: yes, in addition to a dedicated hotkey (Phase 5).
- ~~How to handle a target app with multiple top-level windows~~ —
  resolved: only the exact HWND picked is hidden/reparented; no automatic
  cascading to sibling windows of the same process.
- ~~Separate host process for crash isolation~~ — resolved: deferred, not
  adopted for v1 (the single-process crash-recovery design in §14 Phase 1
  is sufficient; see that section for the full reasoning).
- ~~Whether Pop Out/Crop should appear in a titlebar right-click context
  menu~~ — resolved: deferred to Phase 5 as an optional additional entry
  point, not required for v1.
- ~~Multi-monitor DPI mismatch for the host frame~~ — resolved: deferred to
  `docs/ROADMAP.md` as a backlog item (no test hardware available), not a
  blocker for any phase in this plan.
- **Reminder, not actually an open question:** verify as its own explicit
  test case (not just assumed-covered) that when the *target's own*
  process crashes (as opposed to WindowWorks crashing), the WinEvent hook's
  cleanup path correctly frees/closes the host frame too, rather than
  leaving it lingering as an empty topmost window. This is already required
  by Phase 1's acceptance checklist item 8 — listed here only as a
  cross-reference, not a separate open decision.

## 14. Phased implementation plan

Given the overall scope, implementation is broken into phases. Each phase
must be manually verified and explicitly confirmed by the project owner
before being committed (per the rollout rule at the top of this document) —
no phase auto-advances to the next without that sign-off.

**Phasing philosophy (revised):** front-load every correctness/safety
essential into Phase 1 — even at the cost of Phase 1 being larger — so that
foundational problems (elevation, crash recovery, restore fidelity, picker
correctness) surface as early as possible instead of being discovered after
multiple phases are already built on top of a shaky base. Phases are then
ordered to match the intended manual test sequence: **(1) pick and reparent
a highlighted whole/ancestor element → (2) crop-and-reparent → (3) further/
multi-element reparenting from the same source window.** Nothing here is
deferred for being "hard"; the only things deferred out of the phased plan
entirely are items with an external blocker (no multi-monitor test hardware
— tracked in `docs/ROADMAP.md`) or genuinely optional stretch scope
(Phase 5).

### Phase 0 — Foundation/plumbing (no user-visible UI yet) — **STATUS: CODE COMPLETE**

> **Implementation status (updated for continuity across chat sessions):**
> Phase 0 code is written and builds successfully — see the "Execution
> note" below for exactly what was built (`ReparentEngine.cs`,
> `ReparentHostWindow.xaml(.cs)`) and two regression-audit fixes already
> applied (`RestoreOriginalState` failure-propagation,
> `s_wndProcDelegate` GC-rooting). **Not yet manually verified end-to-end**
> against this section's own acceptance criteria below, because there is
> still no invocation path (no hotkey, no picker) — that invocation path
> is Phase 1's first slice (Parts 1+4, see the Execution note), and manual
> verification of Phase 0's mechanics is expected to happen naturally once
> that first slice exists. Proceed directly to Phase 1.

- Core `SetParent`/`WS_CHILD` save-restore mechanics (§8) proven against a
  single hardcoded/test HWND — save state, reparent, restore — with no
  picker UI yet.
- Minimal, unstyled host frame window that a window can be embedded into.
- Start the manual test matrix early (native Win32/MFC app first), since
  this is the highest-risk core mechanism in the whole feature.
- **Acceptance criteria:** a hardcoded target window can be reparented into
  the test host frame and restored to its original top-level state, with no
  visible corruption of style/placement, verified manually.

- **Execution note (implementation-session decomposition, recorded for
  continuity across chat sessions — not a design change, purely a
  sequencing/labeling clarification):** since hardcoding a literal test HWND
  in shipped code was explicitly ruled out during implementation planning,
  Phase 0 was carried out as two independently-reviewable pieces instead of
  one, with a real (not fake) manual invocation path replacing the
  hardcoded HWND the plan text above describes:
  - **Part 2 — `ReparentEngine` core mechanics** (`WindowWorks.App/ReparentEngine.cs`):
    `SaveOriginalState`/`Reparent`/`RestoreOriginalState`, implementing the
    exact §8 ordering (reparent: `SetParent` → OR in `WS_CHILD` →
    `SetWindowPos`; restore: rect → unparent → placement → styles-last).
    Whole-window case only — the child-HWND original-parent restore branch
    remains deferred to Phase 1, unchanged from the plan.
  - **Part 3 — minimal WPF host frame** (`WindowWorks.App.UI/ReparentHostWindow.xaml(.cs)`):
    unstyled `Window`, one socket area, a Close/Restore button, and the
    `HwndSource.AddHook` bridge for `WM_MOUSEACTIVATE`/`WM_ACTIVATE` (§8
    step 7), per the resolved host-frame-technology decision above.
  - These two parts together are what satisfies this Phase 0 section's own
    acceptance criteria above — verified via a manually-invoked test path
    (a developer-driven call into `ReparentEngine` against a window picked
    by hand during testing), not a hardcoded HWND literal in shipped code.
  - **Explicitly NOT part of Phase 0** (deferred, see Phase 1 below instead):
    a hotkey entry point and any real cursor-based window picking. Two
    further pieces originally considered for this same work session —
    **Part 1** (hotkey plumbing: `HotkeyWindowReparent` setting, default
    `Ctrl+Alt+P` placeholder, mirroring the existing per-hotkey pattern in
    `AppSettings.cs`/`HotkeyManager.cs`/`HotkeyApplyService.cs`/`Program.cs`)
    and **Part 4** (naive `WindowFromPoint` + `GetAncestor(..., GA_ROOT)`
    root-only picking, wired end-to-end to the hotkey) — were identified as
    scope that actually belongs to Phase 1's required picker-entry-point
    deliverable (see Phase 1 below), not Phase 0, even though they were
    designed alongside Phase 0 for a demoable end-to-end path. If/when
    Parts 1 and 4 are implemented, they are explicitly a **placeholder**
    picking mechanism — naive root-only resolution, no ancestor-chain
    discovery, no yellow-box confirm UI — that the real picker (§6.2/§6.3)
    is expected to supersede later within Phase 1, not a permanent
    substitute for it.

> **Implementation status — first slice CODE COMPLETE and manually verified
> (updated for continuity across chat sessions):** Parts 1+4 (see the
> Execution note above) have been implemented and manually tested
> end-to-end: hotkey (`Ctrl+Alt+P`, `HotkeyWindowReparent`) → naive
> `WindowFromPoint`+`GetAncestor(GA_ROOT)` whole-window-only pick →
> `ReparentEngine.Reparent`/`RestoreOriginalState` → `ReparentHostWindow`
> (WPF host frame with native socket, activation forwarding, resizable host
> with debounced target-resize re-application, `WM_DPICHANGED` handling).
> Bugs found and fixed during manual testing: `RegisterClass` ANSI/Unicode
> P/Invoke mismatch (root cause of a blank host frame), `PositionSocket()`
> running before WPF layout committed, a target-resize-on-every-call
> regression (reverted in favor of resizing the host frame once at attach
> time), an `AttachThreadInput`/`SetFocus` experiment that regressed host
> sizing (reverted to `SetForegroundWindow`-only per §8 step 7), and a
> drag/resize-ghosting issue on the target's own native chrome (fixed by
> stripping `WS_CAPTION`/`WS_THICKFRAME`/`WS_MINIMIZEBOX`/`WS_MAXIMIZEBOX`/
> `WS_SYSMENU` from the target in `Reparent()` — see the comment in
> `ReparentEngine.Reparent` for full rationale; this is a deliberate,
> confirmed-working deviation from §8's literal "only OR in `WS_CHILD`"
> wording). A follow-up bug (resizing the host frame didn't resize the
> embedded target, causing painting artifacts) was found and fixed:
> `PositionSocket()` now conditionally resizes the target (debounced ~80ms)
> only when the socket's size actually changed, leaving mere repositions as
> `SWP_NOSIZE`-only as before. Modern Windows 11 (WinUI3/DirectComposition)
> Notepad remains a known, accepted app-compatibility limitation (not a bug)
> — see `docs/KNOWN_OPEN_FINDINGS.md`. **What this slice does NOT yet
> cover** (remaining Phase 1 scope, still open): the real ancestor-chain
> picker UI (yellow-box stack, adaptive positioning), the separate
> `PickerOverlay` class, restore-to-original-parent for child-HWND picks,
> conditional resizability + the "allow resizing reparented child elements"
> setting, the in-memory tracking list + state machine, "Reset
> Reparenting", elevation mismatch detection, the full `SetWinEventHook`
> hook, graceful-shutdown restore, crash recovery, and the Settings section
> toggles. Proceed with the remaining Phase 1 checklist below.

### Phase 1 — Full picker + whole/ancestor-element "Pop Out and Reparent," fully hardened

This is intentionally the largest phase: it now includes **everything**
needed for a single, real, end-to-end "pick a highlighted element under the
cursor and reparent it" flow to be correct, safe, and recoverable — nothing
essential is left for later phases. Crop mode (Phase 2) and multi-element/
further reparenting (Phase 3) build on top of this foundation, but this
phase must stand on its own as production-quality for the single-pick case.

**Picker UX (pulled forward from the old Phase 2 — this is the first thing
you'll manually test, so it needs to be real, not a placeholder):**
- Ancestor-chain discovery (`WindowFromPoint` + repeated
  `GetAncestor(hwnd, GA_PARENT)` up to `GA_ROOT`), filtered to only
  genuinely reparentable HWNDs (§5/§6.2).
- Yellow-box stack UI (§6.3) with adaptive positioning near screen edges
  (§6.4) — this is the actual confirm mechanism, not a stripped-down
  temporary picker.
- **`HighlightOverlay` reuse — decided approach (verified against actual
  code, `WindowWorks.App.UI/HighlightOverlay.xaml.cs`):** the existing
  component IS DPI-aware (`GetDpiForWindow`) and renders a topmost,
  rounded-border highlight rect, so its **visual rendering logic** (rounded
  border via `SetWindowRgn`/`CreateRoundRectRgn`, DPI-correct rect
  placement) is genuinely reusable for the picker's highlight rect.
  **However, two of its current behaviors are load-bearing for its
  existing callers and must not be changed on the shared class:**
  1. It sets `WS_EX_TRANSPARENT` (click-through), needed for its existing
     passive-indicator use case. The picker overlay in §6.1 needs the
     **opposite** — it must own mouse input.
  2. It has a built-in `System.Timers.Timer`-driven auto-close tuned for
     "flash briefly then disappear." The picker overlay must instead stay
     open for the full duration of an interactive picking session.
  - **Decision: do NOT modify `HighlightOverlay` itself or add mode flags
    to it.** Instead, extract the shared rendering logic (DPI lookup,
    rounded-rect region calculation, topmost/layered window setup) into a
    small shared helper (e.g. a static/internal
    `RoundedHighlightRenderer` used by both), and create a **new, separate
    class** (e.g. `PickerOverlay` or `PickerHighlightOverlay`) for the
    picker's own overlay — input-owning (no `WS_EX_TRANSPARENT`), no
    auto-close timer, driven by explicit mouse-move/click/Escape instead.
    This avoids any risk of regressing `HighlightOverlay`'s existing
    callers elsewhere in WindowWorks, at the cost of a small amount of
    extra (but low-risk) new code in Phase 1 to extract the shared
    rendering helper.
- Collapses correctly to a single whole-window entry for single-HWND apps
  (browser/WPF/Electron) with no extra clutter, and shows the real
  ancestor chain for native Win32/MFC apps with nested child controls.
- **Picker entry point/hotkey (moved up from the "Accessibility &
  discoverability" note in §12 — required for Phase 1, not later polish):**
  a single dedicated global hotkey to invoke the picker, following the same
  overall pattern as the existing `HotkeyCommandPalette`/
  `HotkeyEmergencyReset` hotkeys (`AppSettings.cs`, `HotkeyManager.cs`,
  `HotkeyApplyService.cs`, wired up in `Program.cs`). **Verified against the
  actual code — important implementation note, not just a settings-add:**
  these two existing hotkeys are currently **hardcoded, individually-named
  properties and register/parse calls** (`AppSettings.HotkeyCommandPalette`
  / `HotkeyEmergencyReset` string properties; `HotkeyManager` and
  `HotkeyApplyService` each have separate, repeated code paths per hotkey,
  not a generic "list of named hotkeys" registry). Adding a third
  (`HotkeyWindowReparentPicker` or similar) means **duplicating the same
  per-hotkey pattern a third time** in each of those three files — it is
  not a drop-in "just add to a list" change. This is a small, mechanical
  but real piece of implementation work; budget for it explicitly rather
  than assuming the infrastructure is already generic. (Also note:
  `HotkeyCommandPalette`'s current handler in `Program.cs` opens
  `OnboardingOverlay`, not an actual command palette UI — the §13
  "resolved: yes, expose via command palette" item for Phase 5 will need a
  real command-palette surface to exist first; that's a Phase 5 concern,
  not Phase 1, but worth knowing now.)
  This is the only entry point required in Phase 1 — a tray-menu item is
  optional/nice-to-have here, and the command-palette entry (§13,
  resolved-yes) and titlebar context-menu entry (§13 — resolved as an
  optional, deferred Phase 5 candidate; whether to build it at all is what
  remains undecided, not which phase it belongs to) are both explicitly
  Phase 5 scope, not Phase 1.

**Reparent/restore mechanics + safety (unchanged from the prior Phase 1
scope, still here):**
- Hide-original behavior (§7) for the whole-window case.
- **Restore-to-original-parent for ancestor-chain child-HWND picks (§8 step
  8, sub-item 2) — explicit Phase 1 task, found via a later review
  question, not previously called out as concrete implementation work.**
  `SaveOriginalState()` must additionally capture the original parent HWND
  (`GetParent(target)`) for child-HWND picks (not needed for whole-window
  picks, whose original parent is implicitly the desktop). On restore,
  branch: whole-window picks still call `SetParent(target, nullptr)` as
  before; child-HWND picks must instead call `SetParent(target,
  originalParentHwnd)`, after verifying the original parent HWND is still
  valid via the same identity-verification approach used for crash
  recovery (falling back to `SetParent(target, nullptr)` if the original
  parent is gone/recycled, rather than silently failing or risking a
  recycled-HWND hazard). Corresponding acceptance check: item 16 below.
- **Activation/focus forwarding (§8 step 7) — explicit Phase 1 task, not
  just design rationale.** This was fully worked out in §8/§13 (real
  PowerToys mechanism: `WM_MOUSEACTIVATE` → `SetForegroundWindow(target)` +
  `MA_NOACTIVATE`, and `WM_ACTIVATE` with `WA_ACTIVE` → `SetForegroundWindow
  (target)` again) but was never previously called out as a concrete Phase 1
  deliverable or acceptance-checklist item — without it, the embedded
  target simply won't receive keyboard/mouse focus correctly when the host
  frame is clicked/activated, which would make the whole feature feel
  broken even though the reparent itself "succeeded." Required work: (1) if
  the host frame is a WPF `Window` (per §13's still-open host-frame-tech
  question), wire up `HwndSource.FromHwnd(...).AddHook(...)` to intercept
  `WM_MOUSEACTIVATE`/`WM_ACTIVATE` (WPF does not expose these through its
  normal event model); if the host frame is a raw Win32 `HWND` window
  instead, handle them directly in its `WndProc`. (2) Add an explicit
  manual test to this phase's acceptance checklist (see item 13 below).
- **Host frame resize/move → embedded-target `SetWindowPos` re-application
  — moved up from Phase 4 (sequencing correction, see Phase 4 notes for
  full rationale). NOT unconditional within Phase 1 itself — corrected
  during a later review pass** (an earlier draft of this task incorrectly
  said "Phase 1 has no crop mode yet, so this is unconditional for now,"
  which missed that Phase 1 already includes **ancestor-chain child-HWND**
  picks per §6.2, not just whole-top-level-window picks). Per §6.5's
  three-way fixed-size finding: **resizable only for whole-top-level-window
  targets; fixed-size (no resize grips, no `WM_SIZE` re-application needed)
  for ancestor-chain child-HWND targets**, since arbitrary native child
  controls have no reliable self-relayout contract and forcing a resize
  risks corrupting their content — this distinction must be built into
  Phase 1 itself, not just noted as a heads-up for Phase 2. Phase 2 then
  adds the third case (crop-mode targets, also fixed-size, for the
  separate fixed-pixel-mapping reason) on top of this already-conditional
  Phase 1 behavior — Phase 2 does not introduce the *concept* of
  conditional resizability, only its second application. Since §6.7
  already gives the Phase 1 control strip resize/drag chrome, this
  whole-window-only resize/move handling for the embedded child must ship
  in Phase 1, not be deferred as later hardening. Corresponding acceptance
  check: item 14 below.
- Basic host-frame control strip: Close/Restore button (§6.7). **"Open
  original for more picking" is entirely deferred to Phase 3** (it only
  matters once further/multi-element reparenting from the same source is
  in scope) — Phase 1's control strip does not need to include this button
  at all. Phase 1's whole-window flow only needs Close/Restore: reparent →
  interact via host frame → Close/Restore to fully undo. Reopening the
  original for more picking is not a Phase 1 capability.
- **In-memory reparented-windows tracking list** (a simple `List<...>` of
  currently-reparented targets + their saved original state, held in
  WindowWorks' own process memory) — this is the shared data structure that
  "Reset Reparenting", the settings toggle logic, and crash recovery all
  depend on; must be built now, not deferred. This is a plain in-memory
  collection, not the Windows Registry.
  - **Concurrency / session-state protocol (must be explicit, not an
    unstructured list):** all mutations to this list, and all Win32 calls
    that affect entries in it (`SetParent`, style changes, restore), happen
    **only on the UI thread** (the same thread that owns the picker overlay,
    host frames, and the WinEvent hook's `WINEVENT_OUTOFCONTEXT` callback —
    see §14 Phase 1 WinEvent notes above, which already runs the callback
    via the normal message loop, i.e. already on the UI thread). This
    single-threaded-owner rule removes the need for locks and closes the
    main class of races by construction, but the **transition rules**
    between states still need to be explicit so overlapping operations
    (hotkey re-invocation, Reset Reparenting, a WinEvent destroy
    notification, and a normal Close/Restore click) resolve predictably
    even though they can't literally run concurrently:
    - Each tracking-list entry has a state: **Active** (currently
      reparented and embedded) → **Restoring** (a restore/cleanup
      operation has started for this entry) → *(removed from the list once
      restore completes)*. There is no separate "Pending" state (per the
      idempotency reasoning below — a crash before the mutation completes
      leaves the target in a state indistinguishable from "nothing to do"
      once identity-verified).
    - **Re-entrancy guard:** before starting a restore for a given entry
      (whether triggered by a WinEvent destroy callback, a Close/Restore
      button click, "Reset Reparenting", or "Reset All"), check the entry
      is still present and not already `Restoring`. If it's already
      `Restoring` (e.g. a WinEvent callback fires while a user-initiated
      restore for the same entry is already in progress), no-op — do not
      run the restore sequence twice for the same entry.
    - **Hotkey re-invocation while a picker session is already active:**
      treat as "cancel the current picker session, start a new one" (same
      as pressing `Escape` then re-invoking) rather than stacking two
      overlays — only one picker session may be open at a time.
    - **Reset Reparenting / Reset All racing an in-flight WinEvent
      callback:** since both run on the UI thread, they cannot literally
      interleave mid-operation; the re-entrancy guard above is what
      prevents a WinEvent callback that was already *queued* (posted to the
      message loop, e.g. the target was destroyed a moment before Reset was
      clicked) from double-processing an entry that Reset already restored
      first.
- **"Reset Reparenting" tray menu item** (§8 step 11) — single, all-or-
  nothing action; no per-window granularity (confirmed). Walks the
  in-memory tracking list and restores every currently-reparented window.
- **Elevation mismatch detection:** before attempting `SetParent`, compare
  the target process's elevation/integrity level against WindowWorks' own
  process. If the target is elevated and WindowWorks is not, **block the
  action with a clear inline message** (e.g. "This window is running with
  administrator privileges and can't be reparented unless WindowWorks is
  also run as administrator") rather than attempting `SetParent` and risking
  a silent/unpredictable failure. WindowWorks itself does **not** run
  elevated by default just to support this feature — elevation is only
  relevant if the user specifically tries to reparent an already-elevated
  target.
- **Target-app-closes-while-reparented handling (full WinEvent hook — not a
  polling stopgap):** register a
  `SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, ...)` hook.
  **Important implementation details often missed with `SetWinEventHook`
  (make explicit, not left implicit):**
  - `SetWinEventHook` is **not itself HWND-filtered** — registering it only
    filters by event range and (optionally) process/thread ID, not by a
    specific HWND. The callback **must manually check** that the `hwnd`
    parameter matches the specific target being tracked, **and** that
    `idObject == OBJID_WINDOW` and `idChild == 0` (the destroy event fires
    for many object types/children, not just the top-level window object;
    without this filter the callback will fire spuriously for unrelated
    child objects/controls being destroyed inside the target).
  - Use `WINEVENT_OUTOFCONTEXT` (the callback runs on WindowWorks' own
    thread via its message loop, not injected into the target process) —
    this is simpler and avoids cross-process DLL-injection complexity, at
    the cost of slightly higher latency (fine for this use case; no
    real-time requirement here).
  - **Delegate lifetime:** the callback delegate passed to
    `SetWinEventHook` must be kept alive (rooted, e.g. as a field) for as
    long as the hook is registered — a delegate that gets garbage
    collected while the native hook still references it will crash or
    silently stop firing. Call `UnhookWinEvent` explicitly when the target
    is restored/removed (both normal restore and the shared cleanup path),
    not just left to be GC'd.
  - **Race with normal Close/Restore:** if the user clicks Close/Restore at
    nearly the same moment the target process is independently destroying
    the window, ensure the cleanup path is idempotent/re-entrant-safe (e.g.
    check the tracking-list entry still exists before acting) so a
    WinEvent callback arriving just after a normal restore already
    completed doesn't double-free/double-restore the same host frame slot.
  So WindowWorks is notified immediately (not on a polling delay) if the
  target process destroys that window while embedded. On notification,
  clean up that host frame's slot gracefully instead of showing a
  blank/dead frame. Explicitly test this as **its own distinct case** from
  "WindowWorks itself crashes" below — verify the host frame is actually
  closed/freed and does not linger as an empty topmost window.
- **Graceful-shutdown restore:** on normal WindowWorks exit (not just
  crash), proactively call `RestoreOriginalState()` for every entry in the
  in-memory tracking list before the process closes — reparenting should
  never strand a window even in this first phase.
- **Crash recovery — reliable design, not best-effort:**
  - **Confirmed via direct source review: PowerToys' CropAndLock implements
    NO crash recovery whatsoever** (verified by reading the real
    `main.cpp` in full — `croppedWindows` is a plain in-memory
    `std::vector<std::shared_ptr<CropAndLockWindow>>` with zero disk
    persistence anywhere in the module; `RestoreOriginalState()` only ever
    runs via graceful paths — window-close callbacks or the normal
    `WM_QUIT` message-loop exit at the end of `wWinMain`). If the
    CropAndLock process is killed abruptly (crash, forced termination,
    debugger detach) rather than exiting its message loop normally, any
    currently-reparented target windows are left orphaned with **no
    recovery path at all** — this is almost certainly the root cause of
    the known GitHub issue already cited in §2 (#28275, imperfect
    restoration of maximized windows after un-reparenting: if the
    "graceful" restore path itself has edge cases, and there's no
    fallback safety net at all, any interruption is unrecoverable).
    **This means WindowWorks' crash-recovery design below is a genuine
    improvement over the PowerToys precedent, not a redundant
    reinvention** — there is no existing pattern to borrow here; the
    design must be original and should be held to a higher bar than "do
    what PowerToys does," since PowerToys simply doesn't solve this
    problem.
  - **What actually happens on crash:** the target window's own process
    keeps running (it doesn't die with WindowWorks), but its Win32 parent
    (the now-destroyed host frame HWND) is gone, leaving it as a `WS_CHILD`
    with an invalid parent — effectively invisible and unreachable via
    normal UI (not in Alt-Tab/taskbar) until something calls
    `SetParent(target, nullptr)`.
  - **Persistence mechanism:** a plain **JSON file** on disk under
    `%APPDATA%\WindowWorks\reparented-windows.json` — **same folder/location
    convention** as the existing `settings.json`/`presets.json` (verified:
    `Persistence.cs` line 18 resolves
    `%APPDATA%\WindowWorks\` via `Environment.SpecialFolder.ApplicationData`)
    (this is a normal file, explicitly **not** the Windows Registry).
    **Note the durability mechanism itself is new, not reused:** the
    existing `Persistence.SaveSettings`/preset-save methods currently write
    via plain `File.WriteAllText` (verified: `Persistence.cs` line 56,
    similarly for presets) — **not** atomic temp-file-then-replace. The
    reparent state file's synchronous-atomic-write requirement (below) is a
    **new, stricter persistence helper specific to this feature**, not
    something to copy from the existing `Persistence` class as-is. Written
    **synchronously and atomically** (write to a temp file, then
    `File.Replace`/rename) — never batched or deferred.
  - **Write-ordering to close the crash window around the mutation itself:**
    write the entry to the state file **before** calling `SetParent`/
    changing styles (§8 steps 2–5), not only after — i.e. persist "about to
    reparent this HWND, here is its original state" first, then perform the
    actual `SetParent`/style/position calls. This closes the narrow gap
    where a crash occurs *between* `SetParent` succeeding and the state
    file being written, which would otherwise leave the target orphaned
    with no recovery record at all. Symmetrically, on restore, remove (or
    mark inactive) the entry only **after** `RestoreOriginalState()`
    completes successfully — so a crash mid-restore still leaves a valid
    recovery record pointing at the pre-restore saved state, and the next
    launch's recovery pass (idempotent, see below) simply finishes the
    restore. Net effect: the state file is always a superset of "what's
    actually reparented right now" (it may occasionally contain an entry
    for a restore that actually already completed, which is harmless due to
    idempotency), never a subset that could miss an orphaned window.
  - **What's persisted per entry:** the live target HWND value (fast path,
    since the target process itself is usually still alive/unchanged), plus
    fallback identity (executable path, process name, window class, window
    title) for best-effort re-matching, plus **process ID and process
    creation time** (`GetWindowThreadProcessId` + `Process.StartTime`) as
    the primary identity-verification fields, plus the original saved state
    (`GWL_EXSTYLE`, `GWL_STYLE`, `WINDOWPLACEMENT`, original rect). **For
    ancestor-chain child-HWND entries specifically, also persist the
    original parent HWND (`GetParent(target)` at pick time) plus its own
    identity fields (PID + creation time) — found missing during this
    review pass.** This mirrors the §8 step 8 fix for the live
    Close/Restore path (child-HWND picks must restore to their real
    original parent, not to top-level) — that fix was previously applied
    only to the live-session restore path and had not yet been propagated
    to this separate crash-recovery code path, which would otherwise still
    incorrectly make a recovered child control top-level instead of
    restoring it into its original parent.
  - **HWND-reuse hazard — `IsWindow` alone is not sufficient:** Win32 HWND
    values are recycled by the OS after a window is destroyed, so a bare
    `IsWindow(savedHwnd)` check can return true for a **different, unrelated
    window** that happens to reuse the same numeric handle value (e.g. after
    the target process closed and some other app happened to create a new
    window that got the same HWND). Blindly applying saved styles/placement
    to that unrelated window would corrupt it. **Verification protocol:**
    1. `IsWindow(savedHwnd)` — if false, nothing to restore (target and any
       reused handle are both gone); log and drop the entry.
    2. If true, fetch the live window's PID via
       `GetWindowThreadProcessId(savedHwnd)`, then compare against the
       saved PID **and** the saved process-creation-time. If either
       mismatches, the HWND has been recycled — this is **not** the
       original target; log and drop the entry **without touching it**
       (do not restore/mutate a window that fails identity verification).
    3. As a secondary sanity check (defense in depth, not a replacement for
       step 2), also compare window class name against the saved value —
       mismatches here are a strong additional signal of a recycled handle
       even in the rare case PID got reused too (PID reuse combined with
       HWND reuse is astronomically unlikely, but cheap to also check).
    4. Only if PID + creation-time (+ class name) all match does the
       recovery pass proceed to the ordered restore sequence below.
  - **Recovery pass on next launch:** read the file; for each entry, run the
    verification protocol above. If it passes (the common case: only
    WindowWorks died, the target process is still running and its handle
    is still valid and verified), restore it using the **same ordered restore
    sequence as §8 step 8** (rect/placement → unparent → styles-last,
    verified against real PowerToys source — not the reverse):
    first restore the original screen rect via `SetWindowPos(savedHwnd, ...)`
    while still (possibly) parented, then `SetParent(savedHwnd, nullptr)`
    (this works even though the old host frame HWND is long invalid —
    `SetParent` only needs the target and the new parent, and `nullptr`
    means "make it top-level again"), then `SetWindowPlacement`, then
    restore `GWL_EXSTYLE`/`GWL_STYLE` (clearing `WS_CHILD`) **last**. If the
    verification protocol fails at any step, the target app also closed (or
    its HWND was recycled) — there is nothing safe to restore; log and drop
    the entry without mutating anything.
    - **Child-HWND entries: `SetParent(savedHwnd, nullptr)` above is wrong
      for this case, same correction as §8 step 8** — must instead be
      `SetParent(savedHwnd, originalParentHwnd)`, after running the same
      identity-verification protocol (steps 1–4 above) against the saved
      `originalParentHwnd` too (it can itself have been closed/recycled
      independently of the target during the crash/relaunch gap). If
      `originalParentHwnd` fails verification, fall back to
      `SetParent(savedHwnd, nullptr)` (top-level) rather than leaving the
      target un-restored or risking a recycled-parent-HWND hazard — same
      fallback rule as the live-session restore path.
  - **Idempotency:** `SetParent(hwnd, nullptr)` on an already-top-level
    window is a harmless no-op, so re-running the recovery pass (e.g. if
    WindowWorks crashes again immediately after a partial recovery) is
    always safe without extra guards. **This idempotency also covers the
    "mutation never actually completed" case:** if a crash (or a mid-flow
    failure at §8 step 4/5) occurs after the entry is written but before
    `SetParent`/style changes actually succeeded, the target window is
    still in its original top-level state — the recovery pass's restore
    sequence (rect/placement → unparent → styles-last) applied to an
    already-original-state window is a harmless no-op for each step, so no
    separate "pending vs. active" entry state is required. The entry
    should still be logged/dropped normally once recovery confirms
    `GetWindowLong`/`GetParent` already match the saved original values.
  - **Why this removes the need for a separate host process:** the target
    window's HWND remains valid as long as its own process keeps running,
    independent of WindowWorks crashing — so recovery, once triggered by a
    relaunch, is deterministic and correct. The only residual gap is UX,
    not reliability: the window is invisible during the interval between
    the crash and the user relaunching WindowWorks. A separate host process
    would only close that narrower "must relaunch" gap, at the cost of
    significant added IPC/process-lifecycle complexity — not worth it for
    v1; revisit only if real usage shows this gap matters in practice.
  - Clear/archive the state file after a successful recovery pass.
- **Settings integration:** add the "Window Reparenting" settings section
  shell with the master toggle and the "Pop Out and Reparent" sub-toggle,
  plus the compatibility-risk notice text (§9). **Both the master toggle and
  the "Pop Out and Reparent" sub-toggle default to enabled** (confirmed) —
  ship on by default, not gated behind an extra opt-in step.
  - **Required test case:** explicitly verify the *disabled* path too — with
    the master toggle (or the "Pop Out and Reparent" sub-toggle) turned off,
    confirm the picker/hotkey is correctly inert (no picker invocable, no
    stray UI) and does not silently still allow reparenting. This must be
    checked and confirmed working before Phase 1 is considered done.
  - **Also add the new "Allow resizing reparented child elements" setting
    (§9) as part of this same Phase 1 Settings work** — it applies to
    ancestor-chain child-HWND picks, which are already Phase 1 scope, so
    this setting is not deferrable to a later phase even though crop mode
    (the other fixed-size case) doesn't exist until Phase 2. **Default:
    OFF**, with the compatibility-risk warning text (§9) shown when
    enabled. Corresponding acceptance check: item 15 below.
- **Acceptance criteria (manual, before commit) — this is the "test
  reparenting highlighted elements" milestone.** All of the following must be
  explicitly walked through and confirmed, **including the Settings toggle
  and Reset Reparenting checks — these are part of the first testing pass,
  not deferred to a later phase:**
  1. The ancestor-chain picker correctly highlights and lists reparentable
     elements under the cursor (yellow-box stack, adaptive positioning).
  2. Picking a highlighted whole window or ancestor element pops it out and
     hides the original correctly.
  3. Close/Restore (host-frame control strip button) returns the window to
     normal top-level state correctly.
  4. **Settings toggle — enabled path:** with the master toggle and "Pop Out
     and Reparent" sub-toggle both on (their shipped default), confirm the
     picker/hotkey is invocable and reparenting works end-to-end.
  5. **Settings toggle — disabled path:** with the master toggle (and
     separately, just the "Pop Out and Reparent" sub-toggle) turned off,
     confirm the picker/hotkey is correctly inert (not invocable, no stray
     UI) and does not silently still allow reparenting.
  6. **"Reset Reparenting" tray menu item:** with one or more windows
     currently reparented, trigger "Reset Reparenting" and confirm every
     tracked window is restored to its correct original state (top-level
     for whole-window picks; back into their original parent HWND, per
     step 8/item 16, for ancestor-chain child-HWND picks — not
     unconditionally top-level, corrected during this review pass) (test
     this as an explicit alternate path to Close/Restore, not just
     incidentally).
  7. Elevated-target attempt is blocked with a clear inline message (no
     silent failure, no attempted `SetParent`).
  8. Killing the target process while embedded doesn't crash WindowWorks or
     leave a broken/lingering host frame (tested as its own distinct case
     from WindowWorks crashing, per the WinEvent hook item above).
  9. WindowWorks crashing/being killed while a window is embedded is
     recovered correctly on next launch, via the state-file recovery pass.
  10. Normal WindowWorks exit (not a crash) leaves no stray reparented
      windows — graceful-shutdown restore runs correctly.
  11. Single-HWND apps (browser/WPF/Electron) correctly collapse to one
      whole-window picker entry with no ancestor-chain clutter.
  12. **Host frame `WM_DPICHANGED` handling (new checklist item — see §12
      DPI section for the underlying finding from PowerToys'
      `DesktopWindow<T>` base class):** drag a host frame with an embedded
      target from one monitor to another with different DPI scaling;
      confirm the host frame itself resizes correctly per the OS-suggested
      rect, **and** the embedded target is correctly re-positioned/resized
      to fill the host's new client area afterward (not left at its stale
      pre-move size). This specific sub-case does not require multi-monitor
      *validation infrastructure* to test (per the ROADMAP deferral) if at
      least one machine with two differently-scaled monitors is available
      for a quick manual check; if genuinely no such hardware is available
      at all, defer this specific check to the same ROADMAP multi-monitor
      item and note it explicitly as an added scope item there.
  13. **Activation/focus forwarding (new checklist item, corresponds to the
      new Phase 1 task above):** after pop-out, click into the embedded
      target inside the host frame and confirm it visibly receives
      keyboard focus/caret correctly (e.g. can type into a text field in
      the embedded app); switch away (click another app) and back, and
      confirm activation is re-forwarded correctly each time, not just on
      the first click after reparenting.
  14. **Host frame resize/move (moved up from Phase 4 — sequencing
      correction, see Phase 4 notes) — now two distinct sub-cases per the
      three-way fixed-size correction above, not a single check:**
      (a) for a **whole top-level window** pick, resize and drag the host
      frame within a layout (its control strip includes resize/drag chrome
      per §6.7) and confirm the embedded target correctly resizes/
      repositions to fill the new client area each time, with no
      stale/mismatched content; (b) for an **ancestor-chain child-HWND**
      pick, confirm the host frame's control strip correctly omits resize
      grips/maximize affordance (drag-to-reposition still works) — i.e.
      confirm the fixed-size default is actually applied to this pick type
      too, not just to crop mode (which isn't tested until Phase 2).
  15. **"Allow resizing reparented child elements" setting (§9, new):**
      with the setting at its default (OFF), confirm an ancestor-chain
      child-HWND host frame is fixed-size (already covered by item 14b,
      cross-referenced here for completeness). Enable the setting, confirm
      the compatibility-risk warning text is shown, then confirm a
      **newly** reparented child-HWND pick now gets resize grips/maximize
      affordance and correctly resizes the embedded control (spot-check
      against a real MFC/WinForms child control, not just that the grips
      appear). Confirm an **already-reparented** child-HWND host frame
      from before the toggle was enabled is unaffected (per §9's
      toggle-change-timing rule) — only new picks after the toggle change
      pick up the new behavior.
  16. **Child-HWND restore-to-original-parent (new checklist item — covers
      the §8 restore-path gap found via a later review question, not just
      the resize-specific angle):** pick a native child control (not the
      whole window) via the ancestor-chain picker, Close/Restore it, and
      confirm it is correctly reparented back into its **original parent
      window** (not left top-level/floating, and not left orphaned) at its
      original position within that parent. Additionally, with the new
      resize setting (item 15) enabled: resize the child while embedded,
      restore it, and confirm (a) the child itself ends up back at its
      original pre-reparent size (should always be correct, since the
      saved original rect is never affected by an in-session resize), and
      (b) inspect whether the original parent's own layout visibly
      recovers sensibly or shows stale gaps/overlap — per §8's notes, some
      degree of parent-layout awkwardness here is an accepted, documented
      risk (not necessarily a bug to fix), but it must be observed and
      recorded during this test, not silently skipped.
  17. **Child-HWND crash recovery restores to original parent, not
      top-level (new checklist item, found during this review pass —
      crash recovery is a separate code path from item 16's live
      Close/Restore and needs its own explicit test):** pick a native
      child control, then force-kill WindowWorks (not a graceful exit)
      while it's embedded, then relaunch WindowWorks and confirm the
      recovery pass correctly restores the child into its **original
      parent window** (not top-level) — this exercises the crash-recovery
      state file's `originalParentHwnd` field and its own identity
      re-verification, not just the live in-session restore path already
      covered by item 16.

### Phase 2 — Crop-and-Reparent mode

This is the second thing you'll manually test, once whole/ancestor-element
picking (Phase 1) is confirmed solid.

- Crop-rect drag UI (§6.5), DPI-aware crop geometry (maximized-window
  handling via monitor work-area rect), "Crop a region instead" box in the
  picker (appended to the same yellow-box stack built in Phase 1, not a
  separate picker).
- **"Crop and Reparent" settings sub-toggle** added to the existing "Window
  Reparenting" settings section (§9) — **enabled by default**, consistent
  with "Pop Out and Reparent." Same required disabled-path test case as
  Phase 1: verify that disabling this sub-toggle correctly removes the
  "Crop a region instead" picker entry and blocks the crop-rect flow, without
  affecting "Pop Out and Reparent."
- Crop+sibling-overlap soft warning (§6.9).
- **Fixed-size (non-resizable) host frame for crop-mode targets** — per
  §6.5/§6.7's finding (verified against real PowerToys source:
  `WS_THICKFRAME`/`WS_MAXIMIZEBOX` explicitly stripped for its crop-mode
  host window), the host frame's resize grips/maximize affordance added in
  Phase 1 (for whole-window targets) must be **disabled specifically for
  crop-mode targets** — a crop-mode host frame keeps its drag handle
  (repositioning is fine) but is otherwise fixed-size; "Adjust crop" is the
  only supported way to change what's visible. This is new work in Phase 2
  (Phase 1 had no crop mode to need this distinction), not a regression fix.
- Crop targets get the same Phase 1 safety net for free (tracking list,
  elevation check, WinEvent hook, crash recovery, graceful shutdown) since
  they reuse the same underlying reparent/restore mechanics — no separate
  hardening pass needed here.
- **Acceptance criteria:** crop-and-reparent works correctly against the
  browser/video-PiP scenario discussed in this design (§5); disabling the
  sub-toggle is verified to fully remove the crop entry point; a cropped
  target survives the same crash/target-close/elevation checks as Phase 1
  (spot-check, not a full re-run of every Phase 1 case); **attempt to
  resize a crop-mode host frame and confirm it is correctly fixed-size (no
  resize grips, cannot be dragged to a new size) while a whole-window host
  frame from the same session remains resizable as normal** — this
  contrast check is the key new Phase 2 acceptance item distinguishing it
  from Phase 1's behavior.

### Phase 3 — Further/multi-element reparenting from the same source

This is the third thing you'll manually test: picking additional elements
out of a source window/original that's already had one element reparented,
via the phased/incremental picking model.

- Phased/incremental picking model for sibling-children (§6.6, §6.8) —
  multi-child pull from the same original window, with the original staying
  visible (not hidden) throughout, consistent with §7's distinction between
  whole-window and sibling-child visibility behavior.
- Full auto-hide overlay control strip (§6.7) polish, including the "Open
  original for more picking" / "Hide original again" toggle button and its
  Escape-cancel safety net (the simple Close/Restore-only button shipped in
  Phase 1 is upgraded here to the full toggle behavior).
- **Acceptance criteria:** multi-child pull-out from the same parent leaves
  it visibly hollowed but otherwise stable and interactable; "Open original
  for more picking" correctly re-surfaces the original for another pick and
  "Hide original again" correctly re-hides it; Escape cleanly cancels an
  in-progress pick without side effects; each additionally-picked element
  gets full Phase 1 safety coverage (tracking list entry, crash recovery,
  etc.) automatically since it goes through the same mechanics.
  - **New test case (found during a later review pass, §8 step 8's
    nested-reparent mitigation):** pick a native child HWND out of window
    P (leaving P visible per §6.8), then separately pop-out-reparent
    window P itself (whole-window) into a different host frame, then
    Close/Restore the original child pick. Confirm the child is restored
    as a **standalone top-level window** (not nested inside P's own host
    frame) and that an inline notice explains why, per §8 step 8's
    tracking-list-based mitigation — this is the concrete scenario that
    mitigation exists for, and Phase 3 (multi-element-from-same-source) is
    the first phase where it becomes reachable.

### Phase 4 — Robustness/production-readiness hardening

- Harden/extend the Phase 1 `EVENT_OBJECT_DESTROY` WinEvent hook if edge
  cases are found in practice (e.g. hook not firing for certain elevated or
  cross-session targets).
- ~~DPI/multi-monitor correctness pass~~ — **deferred to roadmap, not part of
  this phased plan.** No multi-monitor test hardware is currently available
  to validate this reliably; tracked as a backlog item in
  `docs/ROADMAP.md` instead of blocking any phase here. Single-monitor DPI
  awareness (`GetDpiForWindow`, via the shared rounded-highlight rendering
  helper described in Phase 1) still applies and is covered in Phase 1 as
  normal.
- Finalize compatibility-risk notice wording/placement in Settings (§9's
  "exact wording/placement" open item).
- Full manual test matrix across app categories (native Win32/MFC,
  WinForms, WPF, Electron/Chromium, a DirectX game) — broader and more
  systematic than the smoke tests done per-phase so far.
- ~~Host frame resize/move re-application of `SetWindowPos` to embedded
  content (§12 Process/session lifecycle).~~ **Moved to Phase 1 — sequencing
  correction found during phasing review.** §6.7 already specifies the host
  frame's control strip "can double as whatever minimal host-frame chrome
  WindowWorks needs anyway (resize handles, drag handle for repositioning
  within a layout)" — i.e. the host frame is resizable/movable starting in
  Phase 1, not introduced later. If the `SetWindowPos`-on-resize
  re-application to the embedded child isn't implemented until Phase 4, a
  Phase 1 user resizing or dragging the host frame within a layout would see
  the embedded content visibly break (stale size/position, not filling the
  resized client area) — this is a basic Phase 1 correctness requirement,
  not later hardening. **Action: implement basic host-frame
  resize/move → re-apply `SetWindowPos` to the embedded target as part of
  Phase 1** (add as acceptance checklist item 14: resize/drag the host
  frame within a layout and confirm the embedded target correctly resizes/
  repositions to match, with no stale content). What legitimately remains
  Phase 4 scope is only the *broader* robustness pass (edge cases found
  under real multi-app testing), not the baseline mechanism itself.

### Phase 5 — Integration/extras (optional, may slip to a later release)

- Command palette exposure (§13, resolved-yes item).
- Preset system integration decision + implementation if pursued (§12/§13,
  currently open/leaning live-session-only for v1).
- Accessibility keyboard fallback (§12).
- Titlebar right-click context-menu entry point (§13, still open/undecided).


