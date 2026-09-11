# Design Decisions

## Shortcut settings persistence: hardcoded defaults vs. user overrides

- **Decision:** `AppSettings.HotkeyCommandPalette` / `HotkeyEmergencyReset` (and other gesture defaults) in `WindowWorks.App/Models/AppSettings.cs` are fallback defaults only, used when `settings.json` doesn't yet contain a value (first run or missing key).
- **Rationale:** Once a user edits a shortcut via Settings → Shortcuts (popup capture), `ShortcutsSettingsViewModel.ToDictionary()` writes the new value into the saved settings dictionary on Save, which `SettingsWindow.BtnSave_Click` persists to `settings.json`. On next load, `Persistence.LoadSettings()` deserializes the saved JSON, so the user's value takes precedence over the hardcoded default. `HotkeyApplyService`/`HotkeyManager` re-register the live hotkey immediately after Save.
- **Alternatives considered:** None; this is the intended and already-implemented behavior — no code changes were required, only confirmation of the flow.

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
