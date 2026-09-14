# Design Decisions

## 2026-09-12 — Native layered overlay window: two hard-won Win32/WPF gotchas

- **Decision:** For the Phase 3 overlay scaffold (`ReparentHostWindow.xaml.cs`, `CreateOwnedOverlay`/`PositionOwnedOverlay`), the overlay's paint color must be set via a real `WNDCLASS.hbrBackground` brush (`CreateSolidBrush`), not via `SetLayeredWindowAttributes`'s `crKey` parameter. Its screen-space geometry (origin/size) must be computed via native `GetClientRect` + `ClientToScreen` on the host window's own HWND, not via WPF's `Window.PointToScreen`/`ActualWidth`/`ActualHeight`.
- **Rationale:**
  1. `SetLayeredWindowAttributes`'s `crKey` (color) argument is only honored when the `LWA_COLORKEY` flag is set. With `LWA_ALPHA` alone (used here for a semi-transparent tint, not full transparency), `crKey` is silently ignored for painting purposes — it only affects per-window alpha blending. Passing a "yellow" `crKey` with `LWA_ALPHA` produced a black overlay (default/unpainted `hbrBackground`) despite the color argument looking correct at the call site. The fix was to give the overlay's window class an actual background brush.
  2. A WPF `Window`'s own `PointToScreen`/`ActualWidth`/`ActualHeight` do not reliably correspond to the window's true native client-area screen rect in all cases relevant to a sibling native HWND's positioning (this was root-caused via the `SetWindowPos`/`GetClientRect`/`ClientToScreen` comparison approach below, after a `PointToScreen`+DPI-scale-based formula appeared internally consistent on static analysis alone but still produced a visible right-edge overhang in live testing). Switching to `GetClientRect` (client-relative pixel rect, always `Left=0,Top=0`) + `ClientToScreen` (exact native mapping for a specific HWND) on the host's own real HWND eliminates any WPF-vs-native coordinate-space ambiguity entirely, and keeps all values in device pixels with no DPI scaling needed.
- **Alternatives considered:** Kept iterating on `PointToScreen`/DPI-scale formulas across several rounds (fixing a genuine DPI-double-scaling bug first) before concluding the WPF-coordinate approach itself was the wrong tool for native sibling-HWND geometry; switched to native Win32 APIs on the host's own HWND instead once static analysis of the WPF-based formula couldn't explain a live, reproducible symptom.

## Shortcut settings persistence: hardcoded defaults vs. user overrides

- **Decision:** `AppSettings.HotkeyCommandPalette` / `HotkeyEmergencyReset` (and other gesture defaults) in `WindowWorks.App/Models/AppSettings.cs` are fallback defaults only, used when `settings.json` doesn't yet contain a value (first run or missing key).
- **Rationale:** Once a user edits a shortcut via Settings → Shortcuts (popup capture), `ShortcutsSettingsViewModel.ToDictionary()` writes the new value into the saved settings dictionary on Save, which `SettingsWindow.BtnSave_Click` persists to `settings.json`. On next load, `Persistence.LoadSettings()` deserializes the saved JSON, so the user's value takes precedence over the hardcoded default. `HotkeyApplyService`/`HotkeyManager` re-register the live hotkey immediately after Save.
- **Alternatives considered:** None; this is the intended and already-implemented behavior — no code changes were required, only confirmation of the flow.

## 2026-09-14 — Hotkey apply must be selective and thread-marshaled

- **Decision:** `HotkeyManager.ApplyHotkeySettings` no longer unconditionally unregisters and
  re-registers all three hotkeys (Command Palette, Emergency Reset, Window Reparenting) on every
  Save. It now tracks the last-applied string per hotkey id (`_appliedHotkeyStrings`) and only
  touches an id via `ApplyOneHotkey` if its configured value actually changed. The whole apply
  path is also marshaled onto the `HotkeyManager`'s owning thread's `SynchronizationContext`
  (`_syncContext.Send(...)`) before any `RegisterHotKey`/`UnregisterHotKey` call.
- **Rationale:** `RegisterHotKey`/`UnregisterHotKey` are Win32 APIs that are thread-affine to the
  message-only window's owning thread. `HotkeyManager.Start()` runs on the app's main WinForms
  thread; the Settings dialog runs on its own separate STA thread
  (`SettingsWindow.ShowDialogModalAsync`). Calling these APIs directly from the Settings thread
  silently failed with a spurious "hotkey already in use" error, even for hotkeys nobody else
  held — and separately, the previous unconditional unregister-all-then-reregister-all pattern
  could transiently fail an untouched hotkey's re-registration just because the user only meant
  to change a different one. Both bugs were independently reproduced and fixed.
- **Alternatives considered:** None seriously considered — both root causes were structural bugs
  in the existing apply path, not a design tradeoff; the fix restores the originally-intended
  "only touch what changed, on the right thread" behavior.

## 2026-09-09 — Hybrid WinForms and WPF desktop UI

- **Decision:** Use a WinForms tray/bootstrap application with a referenced WPF UI project for settings and overlays.
- **Rationale:** The tray lifecycle and native window integration remain in the application project while WPF provides the settings and overlay presentation layer.
- **Alternatives considered:** A single UI framework; the existing split is retained.

## 2026-09-11 — Split UIA identity checks into a cheap "hot path" vs. a full "decision path"

- **Decision:** `ReparentHostWindow`'s hot/frequent code paths (`WndProc`, debounced resize,
  `PositionSocket`) must never call `AutomationElement.FromHandle` or any other live UI
  Automation (UIA) API. They now use only cheap native checks (`IsChild`/`GetParent` plus a
  saved PID/process-start-time/class-name comparison) via `HasExpectedTargetIdentity()`.
  `ReparentEngine.VerifyWindowIdentity` — the only place still allowed to make a live UIA call
  (`AutomationRuntimeId` check) — is reserved exclusively for actual mutating decisions: restore,
  close, and crash-recovery. A destroy-triggered invalidation that happens after a host attaches
  is still observed by the hot path via `ReparentEngine.CreateIdentitySnapshot(identity)`, which
  returns a closure (`() => identity.IsInvalidated`) reading the live `CapturedWindowIdentity`
  reference rather than a one-time-copied bool.
- **Rationale:** `AutomationElement.FromHandle(hwnd)` is a synchronous, cross-process UIA/COM
  call that requires the *target* window's own UI thread to respond. Calling it from a hot path
  like `WndProc`/resize handling can produce a true bidirectional deadlock — both WindowWorks and
  the reparented target application freeze solid, requiring the process to be killed. This was
  confirmed as a real, reproduced defect via a live `dotnet-dump collect` + `clrstack -all`
  analysis (not just static code reading) showing the main UI thread blocked inside
  `WndProc -> IsCurrentTargetAttachedToSocket -> VerifyWindowIdentity -> AutomationElement.FromHandle`.
  This was a regression from an earlier UIA-identity-hardening change that added the stronger
  check without accounting for its synchronous cost on a hot path.
- **Alternatives considered:** Debouncing/throttling the UIA call further (rejected — any
  synchronous cross-process UIA call on a path that can run while the target's own thread is
  busy is still a deadlock risk, no matter how infrequent); making the UIA call asynchronous
  (rejected as unnecessary complexity — the hot path doesn't need UIA-level identity strength at
  all, only the full decision path does).

## 2026-09-11 — `dotnet-dump` live process inspection as a required diagnostic step for hangs/state bugs, before further static-code speculation

- **Decision:** For hang/freeze defects and for "why doesn't X fire" state bugs where two rounds
  of code-only reasoning (including independent sub-agent code review) failed to match a user's
  live-reproduced behavior, capture a live `dotnet-dump collect -p <pid>` snapshot of the running
  WindowWorks.App process and inspect it directly (`dotnet-dump analyze` + `clrstack -all` for
  hangs; `dumpheap -type <T>` / `dumpobj` / `dumpvc` for object-state bugs) rather than continuing
  to iterate on static code reading or further sub-agent round-trips alone.
- **Rationale:** This was the decisive diagnostic step twice this session. For the UIA deadlock,
  static reading alone would not have found the exact blocking call stack — `clrstack -all` did.
  For the crop-overlap-warning bug, two independent sub-agent code reviews both concluded the fix
  was logically correct, yet the user's live retest showed it still didn't work; a live heap
  inspection (`dumpheap -type ReparentedWindowEntry` + `dumpobj`/`dumpvc` on the actual tracked
  entries) proved the underlying tracked state (HWNDs, top-level roots, crop rects) was exactly
  correct at the moment of the bug — ruling out several plausible theories in one step and
  redirecting investigation away from a wrong path (assuming a data/state bug) toward the
  remaining code-path itself.
- **Alternatives considered:** Continuing to send the same bug back to a sub-agent for further
  "self-review" (this was already tried twice for the overlap-warning bug and both times produced
  an unproven, purely-reasoned conclusion that didn't match live behavior — see the Regression
  Auditor Protocol's "Recurrence Escalation" rule, which this pattern independently reinforces:
  stop iterating blindly once the same class of contradiction repeats, and get harder evidence
  instead).
- **Tooling note:** `dotnet-dump` had to be installed via
  `dotnet tool install --global dotnet-dump --add-source https://api.nuget.org/v3/index.json
  --ignore-failed-sources` in this environment, because the machine's default configured NuGet
  feed (an internal Azure DevOps feed) failed to resolve/authenticate for this tool.

## 2026-09-11 — Fixed-size crop host suppresses resize instead of introducing a new UI mode

- **Decision:** When a reparent originates from a crop selection (`cropGeometry is not null`),
  the host window is forced non-resizable (`resizable = cropGeometry is null && ...` in
  `ReparentController.cs`), while still allowing the user to drag-reposition it via
  `ConfigureResizability`'s `ResizeMode.CanMinimize`.
- **Rationale:** A cropped region has no well-defined way to "resize" without either distorting
  the source content or requiring new scaling/clipping logic; suppressing resize entirely for
  crop-originated hosts avoids that class of bug for v1 rather than attempting partial support.
  Drag-repositioning is unaffected since it doesn't depend on resize grips.
- **Alternatives considered:** Allowing resize with scaled/clipped content (deferred — adds
  meaningful complexity for a v1 feature; not attempted this session).

## 2026 (Phase 3) — Reversed "tracked original parent forces standalone restore" mitigation

- **Decision:** When restoring a child-HWND pick (§8 step 8), always restore into the original
  parent (`SetParent(target, originalParentHwnd)`) as long as that parent still passes identity
  verification (`ReparentEngine.VerifyWindowIdentity`) — regardless of whether that parent is
  itself currently tracked as an active reparent target elsewhere in WindowWorks. The
  `ShouldRestoreChildPickAsStandalone` method in `ReparentController.cs`, which forced
  standalone/top-level restore whenever the tracking list contained the original parent (or any
  ancestor of it), has been removed entirely, along with its call sites in
  `OpenOriginalForMorePicking`, `RestoreEntry`, and `RunCrashRecoveryPass`. Standalone/top-level
  restore is now triggered **only** by `ReparentEngine.RestoreOriginalState`'s own pre-existing
  `parentValid` identity-verification fallback — i.e. only when the original parent is genuinely
  gone, recycled, or otherwise fails identity verification. As a follow-up cleanup pass, the
  `ReparentEngine`/`ReparentController`/`ReparentHostWindow` mechanism that supported the removed
  mitigation (the `forceTopLevelRestore` parameter on `TemporarilyRestoreToOriginalState`/
  `RestoreOriginalState`, the `RestoreOutcome.RestoredAsStandaloneDueToTrackedOriginalParent` enum
  value in all three of its declarations, its associated notice message, and the now-orphaned
  `GA_PARENT` constant in `ReparentController`'s private `NativeMethods`) was deleted entirely
  rather than left dormant, since unused code/parameters with no live caller were judged more
  likely to confuse future readers than to save future effort.
- **Rationale:** This reverses a mitigation added earlier in Phase 3 (Piece 1) for the scenario:
  pick a native child HWND out of window P (leaving P visible), separately pop-out-reparent
  window P itself (whole-window) into a different host frame, then restore the original
  child pick. The original plan assumed nesting the child back into P in this state was unsafe
  ("confusing", "untested topology") and should be avoided by forcing a standalone restore
  instead. The user manually tested this exact scenario using File Explorer (picking the
  navigation pane as the child-HWND target, then the whole Explorer window as a second,
  separate pick) and found nesting works correctly: the child renders in its normal, fully
  chromed place inside P, and P's own host frame continues to function normally afterward.
  Identity-verification (PID/creation-time/class match) is what actually matters for restore
  safety — it already correctly confirms `originalParentHwnd` is still the *same, live* window;
  whether that window also happens to be tracked/embedded elsewhere is irrelevant to whether
  restoring into it is safe. The prior mitigation was also independently found to be buggy in
  its own terms before this reversal was decided: `OriginalParentHwnd` is only the *immediate*
  parent, while whole-window entries are tracked by *root* ancestor HWND, so the original
  single-HWND `IsTracked` check silently never fired for deeply-nested child picks (e.g.
  Explorer's nav pane) — a walk-up-to-root fix was drafted first, then abandoned once the
  design goal itself was found to be wrong, in favor of removing the mitigation altogether.
- **Alternatives considered:** (1) Keep the mitigation but fix its root/immediate-parent
  ancestor-walk bug (drafted, then discarded once the underlying design goal was itself found to
  be incorrect, not just its implementation). (2) Leave the now-unused `ReparentEngine`
  `forceTopLevelRestore` parameter and `RestoredAsStandaloneDueToTrackedOriginalParent` enum
  value in place as dormant infrastructure for a possible future need — tried first, then
  reversed on explicit request: dead parameters/enum values with no caller were judged a
  confusion risk, so they were deleted outright instead. (3) One acknowledged, accepted residual
  risk of the new behavior: if the other
  host frame that P ends up nested in is later torn down *abnormally* (crash, or any teardown
  path that destroys the host's socket without first unparenting its children), the renested
  child would be destroyed along with it rather than surviving independently as a standalone
  top-level window — judged an acceptable, narrow tradeoff against the much more common
  successful-restore case, and not something to code around now.
