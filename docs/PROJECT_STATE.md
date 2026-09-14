# Project State

> This file is overwritten, not appended, at the end of each working session.

## Current Focus
- This session was a round of live-testing-driven UX polish on the Window Reparenting feature,
  fixing several bugs the user found by hand-testing each change immediately. All fixes are
  committed (`f93a9df`, `6388fe1`); the feature is confirmed working end-to-end by the user.
- Fixes this session:
  - Picker gating: whole-window/ancestor-chain picks now correctly gated on
    `EnablePopOutAndReparent` (previously only crop was gated); `ReparentController`/
    `TrayController` cancellation/guard logic fixed to use AND (not OR) between the two
    sub-toggles.
  - Crop-only mode (Pop Out off, Crop on) now jumps straight into the crop drag-select overlay
    on hotkey press instead of requiring an extra click through a box list first.
  - Removed the white border on fixed-size (crop-mode / non-resizable) reparent hosts by
    disabling the custom resize-grip band entirely for those hosts; resizable hosts keep the
    grip band, reduced from 8dip to 4dip (`ReparentHostWindow.xaml.cs`,
    `CustomResizeGripThicknessDip`).
  - Escape now reliably cancels the crop drag-select overlay: `CropRectSelectionWindow` polls
    `GetAsyncKeyState(VK_ESCAPE)` via a `DispatcherTimer` (same pattern as
    `WindowPickerSession`) instead of relying on WPF `KeyDown`/focus, since a window shown via a
    global hotkey isn't guaranteed real keyboard focus.
  - Reordered "Allow resizing reparented child elements" to sit under "Pop Out and Reparent"
    (was previously, confusingly, under "Crop and Reparent").
  - Exposed the previously-hidden Window Reparenting hotkey in Shortcuts settings (editable,
    same as Command Palette/Reset All) — backend support already existed.
  - Fixed two hotkey-save bugs in `HotkeyManager.cs`: (1) saving one hotkey no longer
    unregisters/re-registers the other two (selective per-id apply via `_appliedHotkeyStrings` +
    `ApplyOneHotkey`), which previously could spuriously fail with a false "already in use"
    error; (2) `ApplyHotkeySettings` now marshals onto the `HotkeyManager`'s owning thread's
    `SynchronizationContext` before calling `RegisterHotKey`/`UnregisterHotKey`, since those
    Win32 APIs are thread-affine and the Settings dialog runs on its own separate STA thread.
- Cleaned up leftover untracked scratch/coordination files from an earlier session (diff*.txt,
  probe_reparent_style.cs, `.github/prompts/{check-agent-report,continue-agent-assignment,
  main-coordinator,sub-agent}.prompt.md`, `docs/agent-{handoff,to-coordinator}.md`,
  `docs/coordinator-to-agent.md`) — deleted per user confirmation, not part of this feature.

## Open Tasks / Known Issues
- None outstanding for Window Reparenting as of this session's close — user confirmed the
  feature works fine end-to-end after this round of fixes.
- Deferred/backlog only (see `docs/ROADMAP.md`): reparented-window overlay settings (auto-hide
  delay, hover trigger zone size, always-show toggle, appearance) — explicitly deferred per user
  request, not scheduled.
- Older known-open findings (crop-overlap warning not firing, intermittent blank/black
  crop-reparent host window, a ~10s transient hang) were logged in a prior session — see
  `docs/KNOWN_OPEN_FINDINGS.md`; not touched or re-investigated this session.

## Recently Changed Files
- `WindowWorks/src/WindowWorks.App/HotkeyManager.cs` — committed `f93a9df`: selective per-id
  hotkey re-registration, thread-marshaling fix.
- `WindowWorks/src/WindowWorks.App.UI/ReparentHostWindow.xaml(.cs)` — grip band reduced to
  4dip; fixed-size hosts skip the grip band entirely (no white border).
- `WindowWorks/src/WindowWorks.App.UI/CropRectSelectionWindow.xaml.cs` — Escape-key polling via
  `DispatcherTimer` + `GetAsyncKeyState`.
- `WindowWorks/src/WindowWorks.App.UI/WindowReparentingSettingsControl.xaml` — checkbox
  reordering, stale doc-text fix.
- `WindowWorks/src/WindowWorks.App.UI/ShortcutsSettingsControl.xaml`,
  `ViewModels/ShortcutsSettingsViewModel.cs`, `SettingsWindow.xaml.cs` — Window Reparenting
  hotkey exposed in Shortcuts settings.
- `WindowWorks/src/WindowWorks.App/ReparentController.cs`, `TrayController.cs`,
  `WindowPickerSession.cs` — picker gating fix, crop-only direct-entry UX.
- `docs/ROADMAP.md` — added reparented-window overlay settings backlog entry.
- Committed together as `6388fe1` (all except `HotkeyManager.cs`, committed separately as
  `f93a9df`).
