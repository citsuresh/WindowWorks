# Known Open Findings

This file is a user-curated reference list of open, unresolved findings that were explicitly
chosen not to act on immediately. It is not a task/bug tracker and is not automatically
maintained — entries are only added, edited, or removed when explicitly requested.

## Modern Windows 11 Notepad renders as cascaded/ghosted frames when reparented

- **First seen:** 2026-09-10
- **Last seen:** 2026-09-10
- **Occurrences:** 2
- **Description:** When using the Window Reparenting feature (docs/REPARENT_FEATURE_PLAN.md) to
  reparent modern Windows 11 Notepad (the packaged/WinUI3-based Notepad, tabs UI) into a
  ReparentHostWindow socket, clicking inside the embedded window produces a cascade of duplicated
  Notepad title-bar/frame artifacts (each showing its own min/max/close glyphs), and the host
  window itself sometimes briefly snaps to near-full-screen size. Confirmed via manual testing
  that this does **not** happen with classic Win32 apps — it is specific to modern Notepad.
  **Recurrence (2nd occurrence, same day):** after later fixes (content-only reparenting —
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
  PowerToys-verified sequence in the plan's §8) appears to corrupt/duplicate that composition
  surface — an app-compatibility limitation explicitly anticipated by the plan's §4 ("many apps
  ... custom-rendered UI ... don't tolerate WS_CHILD + SetParent and may glitch"), not a bug in
  the reparent/host-frame code itself. `SetWindowPos`/`SWP_FRAMECHANGED` sequencing was verified
  correct against the plan and real PowerToys source during this investigation. The 2nd-occurrence
  symptoms are consistent with this same root cause: modern Notepad's visible "title bar" (with its
  tabs) is drawn by the app itself in client-space via DirectComposition, not a real OS non-client
  caption — so `Reparent()`'s later fix of clearing `WS_CAPTION`/`WS_THICKFRAME` (which correctly
  removes real OS-drawn chrome for classic apps) cannot remove it, and its own composition surface
  continues to desync/duplicate whenever the host window moves or the app's own tab strip repaints.
- **Suggested handling (not yet implemented):** use classic Win32 apps (e.g. mspaint, WordPad,
  classic notepad.exe) for manual testing of the reparent mechanics; consider whether Phase 1
  should detect/warn about packaged/WinUI3 targets specifically (in addition to the general
  compatibility-warning toggle already planned in §4/§9), or simply document this as a known
  limitation for v1.
