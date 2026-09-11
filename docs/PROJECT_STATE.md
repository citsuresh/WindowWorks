# Project State

> This file is overwritten, not appended, at the end of each working session.

## Current Focus
- Completed Window Reparenting (Phase 2) functional work and fixed several defects found
  during manual testing, via background sub-agent delegation (launch -> self-review ->
  independent code-review -> user manual confirmation -> stop agent). Nothing has been
  committed yet (standing instruction: no auto-commit, manual review required first).
- Phase 2's 3 originally-missing items are implemented and code-reviewed: `EnableCropAndReparent`
  settings toggle, fixed-size crop host (drag-repositionable, non-resizable), and a
  sibling-overlap warning (`ShowCropOverlapWarningIfNeeded` in `ReparentController.cs`) — the
  last one is implemented but user-confirmed **not actually firing** in its intended
  same-top-level-window overlap scenario (see Known Issues).
- Fixed a real cross-process UI-thread deadlock: `AutomationElement.FromHandle` (a synchronous
  UIA COM call) was being invoked from hot paths (`WndProc`, resize, socket positioning) in
  `ReparentHostWindow.xaml.cs`, causing both WindowWorks and the reparented target app to freeze
  solid (required killing the process). Root-caused via a live `dotnet-dump` capture + `clrstack
  -all` analysis. Fixed by splitting identity checks into a cheap native-only "hot path" (PID/
  process-start-time/class-name via `IsChild`/`GetParent`) vs. the full UIA-inclusive path
  (`ReparentEngine.VerifyWindowIdentity`), reserved only for actual restore/close/crash-recovery
  decisions. Added `ReparentEngine.CreateIdentitySnapshot(...)` (closure-based live invalidation
  reader) so hot-path checks still observe destroy-triggered invalidation without a live UIA call.
  User-confirmed fixed.
- Fixed a reparent host sizing/clipping bug (VS docked Copilot Chat pane reparented with a large
  blank region) — host was sized from a pick-time rect instead of the target's live rect
  post-`SetParent`; now captures `LiveRectAtReparent`. User-confirmed fixed.
- Investigated the crop-overlap warning failure via live memory-dump inspection of the running
  process (`dotnet-dump collect` + `dumpheap`/`dumpobj`/`dumpvc`) during a user-reproduced overlap
  scenario: confirmed the tracking list holds correct data (two genuinely different HWNDs,
  same top-level root via `GetAncestor(..., GA_ROOT)`, overlapping stored crop rects) — the
  warning still doesn't fire despite the underlying data looking exactly like the case it should
  handle. Root cause NOT yet found. Added temporary `Debug.WriteLine` diagnostic tracing to
  `ShowCropOverlapWarningIfNeeded` in `ReparentController.cs` (not yet reviewed via a captured VS
  Debug Output trace). User confirmed this is non-critical (informational-only feature) and
  chose to defer further investigation — logged to `docs/KNOWN_OPEN_FINDINGS.md`.
- Established and refined a sub-agent delegation workflow this session (saved as a 9-point
  policy in the session's SQL `notes` table, id `phase3-subagent-workflow`) for future sessions/
  a Phase 3 handoff prompt: one fresh background `general-purpose` agent per independent task,
  self-review before reporting done, independent `code-review` sub-agent verification against
  actual source (not the report text), resume the SAME agent via `write_agent` for follow-ups
  rather than spawning new ones, only stop an agent after both independent review passes AND
  user manual confirmation (when testing is needed), post percent-complete status after each
  approved item, and don't stop to ask permission before continuing to the next item by default.

## Open Tasks / Known Issues
- **Crop-overlap warning does not fire** for sibling/overlapping crop picks of the same
  top-level window, despite the underlying tracking-list data (HWNDs, roots, rects) all
  checking out correctly via live dump inspection. Root cause unknown. Non-critical
  (informational MessageBox only, not a safety mechanism) — deferred; see
  `docs/KNOWN_OPEN_FINDINGS.md` for full details and the temporary diagnostic logging left in
  `ReparentController.ShowCropOverlapWarningIfNeeded`.
- **Intermittent blank/black crop-reparent host window**, especially when cropping video regions
  (e.g. YouTube in a browser) — recurs roughly 1-in-2/3 attempts, confirmed NOT a one-off (an
  earlier session's downgrade to "non-reproducible" was wrong). No diagnostic data captured yet;
  two live-inspection attempts failed because the reproduced blank window couldn't be kept open
  long enough. User will notify immediately on next repro so live HWND/style/paint state can be
  inspected before closing. Tracked in SQL `todos` as `crop-blank-window-diagnosis` (pending).
- A separate, distinct transient hang (~10+ seconds, self-recovers without requiring a process
  kill) was observed once after a crop action, after the confirmed UIA-deadlock fix was already
  in place — not yet root-caused, user explicitly deferred further live investigation of this one
  for now (SQL `todos` id `residual-hang-10s`, blocked).
- No preset editor UI exists yet; `PresetManager.SavePresets` is currently only exercised
  on first-run seed persistence.
- Shortcut editing MVVM migration (popup-based capture flow) is complete for keyboard
  shortcuts; gesture edit buttons were intentionally removed (no popup editor for gestures).
- Nothing from this session has been committed yet — awaiting explicit user go-ahead per
  standing "no auto-commit" instruction.

## Recently Changed Files
- `WindowWorks/src/WindowWorks.App/ReparentEngine.cs` — `LiveRectAtReparent` capture;
  `CreateIdentitySnapshot(...)` live-invalidation-reading closure helper.
- `WindowWorks/src/WindowWorks.App/ReparentController.cs` — live-rect sizing plumbing; crop-mode
  forced fixed-size; `ShowCropOverlapWarningIfNeeded`/`IsSameOriginalWindow`/
  `GetTopLevelAncestor`/`RectanglesIntersect` (overlap-warning feature, still buggy — see Known
  Issues, includes temporary diagnostic `Debug.WriteLine` tracing); `EnableCropAndReparent`
  gating; identity-snapshot wiring at both `AttachTarget` call sites.
- `WindowWorks/src/WindowWorks.App.UI/ReparentHostWindow.xaml.cs` — removed all live UIA
  (`AutomationElement.FromHandle`) calls from hot paths; `HasExpectedTargetIdentity()` now
  native-only; consumes the live `CapturedIdentitySnapshot` closure.
- `WindowWorks/src/WindowWorks.App/Models/AppSettings.cs` — added `EnableCropAndReparent`
  (default true).
- `WindowWorks/src/WindowWorks.App.UI/ViewModels/WindowReparentingSettingsViewModel.cs`,
  `WindowReparentingSettingsControl.xaml(.cs)`, `SettingsWindow.xaml.cs` — Settings UI wiring
  for the new toggle.
- `WindowWorks/src/WindowWorks.App/TrayController.cs` — extended disable-while-picker-open
  cancel condition to include the new toggle.
- `WindowWorks/src/WindowWorks.App/WindowPickerSession.cs` — gates "Crop a region instead"
  entry on `EnableCropAndReparent`.
- `docs/KNOWN_OPEN_FINDINGS.md` — added the crop-overlap-warning finding.
