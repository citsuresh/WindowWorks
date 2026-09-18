# Property Inspector Feature — Implementation Plan

## Development progress

> Keep this ledger current as implementation progresses. Each increment records its code status,
> regression-audit result, and the user's manual-test confirmation before the next increment starts.

### Phase A — UIA (native controls) proof of concept — ✅ COMPLETE

- **Status:** Done and superseded by Phase C (see below) — the full concrete implementation
  landed well beyond this POC's original scope. Kept here only as a historical record.

### Phase B — Browser DOM element (UIA-only) proof of concept — ✅ COMPLETE

- **Status:** Done. DOM elements are pickable via the Element Tree window (top-level window only
  at first; child-element picking inside a browser now also works — user confirmed testing with a
  second, different browser control and the picker/tree both worked). UIA read/write for DOM
  elements confirmed working for the same property set as native controls where the underlying
  UIA pattern is present.

### Phase C — UIA (native controls) concrete implementation — ✅ COMPLETE (design changed from original plan)

- **Status:** Done, but the original Apply/Cancel staged-workflow design (§2 below) was
  **superseded per explicit user request during implementation**: edits now commit immediately —
  text/range fields commit on Enter key press or on focus loss (not just focus loss), and other
  editor kinds (Toggle, SelectionItem, Win32Bool, ExpandCollapse, WindowVisualState) commit
  immediately on change. A per-property **Apply** button was added next to text/range editors
  (`ApplyTextButton_Click`) as an additional explicit-commit affordance alongside Enter/focus-loss,
  rather than a single end-of-session Apply/Cancel gate over all staged edits. This is a real,
  confirmed design deviation from §2's original "Apply/Cancel workflow" bullet — §2 is left as
  historical rationale below but no longer describes current behavior; see the in-app hint text
  ("Inline edits commit on Enter/focus loss (text) or immediately on change (checkbox/combo)")
  for the authoritative current behavior.
- **Editor kinds implemented** (`PropertyInspectorEditorKind`): ReadOnly, Text, Toggle (now a
  toggle-switch control per user request, not a checkbox), Range, SelectionItem, ExpandCollapse,
  WindowVisualState, Win32Bool (with a real Win32 fallback write path, including surfacing
  Access Denied errors for elevated target windows — confirmed as expected/correct behavior via
  manual testing), LegacyText (MSAA/LegacyIAccessiblePattern.SetValue bridge), Invoke
  (LegacyIAccessiblePattern.DoDefaultAction), and Transform (bounding-rectangle X/Y/Width/Height
  editing via TransformPattern) — i.e. the full property surface from §2's scope decision,
  including position/size editing, which the original phase breakdown had deferred.
- **Property grid navigation:** Up/Down arrow-key row navigation implemented via a
  `WH_KEYBOARD_LL` low-level keyboard hook (arrow-key `WM_KEYDOWN` does not reach this window's
  normal WPF `PreviewKeyDown` routing at all — same class of issue already solved in
  `PickerElementTreeWindow`), with `DataGrid.SelectedIndex` kept in sync so the visible row
  highlight follows keyboard focus. Selected-row highlight uses a translucent overlay + border
  (not a solid fill) so value text stays readable regardless of the editor control's own
  foreground color. Both fixed and manually confirmed by the user.
- **Entry point:** Permanent tray-menu command **Inspect UI Element...**, plus a **View Element
  Tree** button inside the inspector for navigating to and inspecting any descendant node (native
  or DOM), not just the initially-picked top-level element.

### Phase D — Browser DOM element concrete implementation — ✅ COMPLETE

- **Status:** Done. DOM element properties use the same Phase C property grid, editor kinds, and
  write paths as native controls (UIA-pattern-first; no Win32 fallback is possible for DOM
  elements per §6's known constraint, since they have no real HWND). Manually confirmed by the
  user across multiple real browser instances/pages.

### Phase E — DevTools (CDP) bridge as an additive second property section — IN PROGRESS

- **Status:** Sub-phase 1 (CDP bridge proof of concept) ✅ COMPLETE, manually verified.
  Sub-phase 2 (element correlation) ✅ COMPLETE, manually verified against a real Brave instance
  with confidence 0.98-0.99. Sub-phase 3 (read-only DevTools property display) ✅ COMPLETE, manually
  verified. Sub-phase 4 (DevTools write path + hide/show action) ✅ COMPLETE, verified end-to-end
  against a live Edge instance. Three follow-up architectural bugs (re-correlation after a
  `display:none` write, cache-fallback property read priming, UIA-section reappearance after
  show/hide) found and fixed via live self-testing — see "Sub-phase 4 follow-up" below. A Refresh
  button was also added to the inspector window. Sub-phase 5 (broader DevTools property/action
  set) not started — deferred as lower priority per the plan. Full Pre-Build Decomposition
  confirmed with the user before each sub-phase's implementation began, per this project's
  standing workflow.
- **Sub-phase 1 — CDP bridge proof of concept:** Hand-rolled WebSocket + JSON-RPC client
  (`WindowWorks.App/Cdp/CdpClient.cs`, `CdpTarget.cs`) chosen over a NuGet CDP library, since the
  project deliberately keeps external dependencies minimal (only one existing NuGet package,
  `Interop.UIAutomationClient`) and the wire protocol needed is small — one HTTP GET for target
  discovery (`/json/list`) plus a handful of JSON-RPC-style WebSocket round-trips. Manually
  verified end-to-end against a real running Brave instance launched with
  `--remote-debugging-port=9222`: target discovery found the open tab, WebSocket connect
  succeeded, and `Runtime.evaluate("1 + 1")` round-tripped correctly (`{"result":{"type":"number",
  "value":2,"description":"2"}}`). A temporary manual-verification harness
  (`WindowWorks.App/Cdp/CdpBridgePoc.cs`, invoked via `WindowWorks.App.exe --cdp-poc`, writes its
  report to `%TEMP%\cdp-poc-report.txt`) is being **kept intentionally** (not removed after
  sub-phase 1, per explicit user request) since sub-phase 2 (element correlation) will also need a
  similar live-browser manual-verification harness — expect it to be extended/repurposed there
  rather than deleted.
  - Regression Audit (code-review subagent, independent of implementation rationale) found and
    the following were fixed before this sub-phase was considered done:
    - A race where a `SendCommandAsync` call issued concurrently with (or immediately after) the
      receive loop faulting could hang forever, since the fault handler only failed requests
      already present in `_pending` at the moment it ran. Fixed by adding a `_faultException`
      field checked both before and immediately after registering a new pending request, plus a
      `finally` block in the receive loop that always drains and fails any remaining pending
      requests on exit (covers disposal, cancellation, and error paths uniformly).
    - `DisposeAsync` did not fail any requests still pending at disposal time, so a caller awaiting
      `SendCommandAsync` with a `default` cancellation token could hang indefinitely across
      disposal. Fixed as a side effect of the `finally`-block change above (disposal cancels the
      receive loop, which now always drains `_pending` on any exit path).
    - Unbounded `MemoryStream` growth when reassembling a multi-frame WebSocket message (no size
      cap). Fixed with a 32 MB safety cap that aborts the connection if exceeded.
  - No other issues found: build succeeds; no existing CDP/WebSocket/JSON-RPC mechanism elsewhere
    in the codebase is duplicated; the new `--cdp-poc` command-line branch in `Program.cs` does not
    alter normal (no-args) startup behavior.
- **Sub-phase 2 — Element correlation:** `CdpDomCorrelator.CorrelateAsync`
  (`WindowWorks.App/Cdp/CdpDomCorrelator.cs`, `CdpDomCorrelationResult.cs`) maps a UIA-picked
  browser DOM element to its corresponding CDP DOM node via a two-step heuristic: (1) calibrate a
  screen-px-to-CSS-px affine transform using the UIA "Document" ancestor's own bounding rect
  (physical screen pixels — the visible content area) against `Page.getLayoutMetrics`'s
  `cssVisualViewport`; (2) transform the picked element's rect to a CSS-pixel point, hit-test via
  `DOM.getNodeForLocation`, then verify the match by transforming the matched node's CDP box model
  back to screen pixels and computing intersection-over-union (IoU) against the original UIA rect
  as a confidence score.
  - Two real bugs were found and fixed during manual testing against a live Brave instance:
    (a) **DPI-awareness gap** — WindowWorks.App has no DPI manifest, so on a scaled display (tested
    at 150%) all Win32/UIA calls run in a virtualized 96-DPI coordinate space while CDP/Chromium
    reports true physical pixels, producing a spurious ~1.5x "calibration scale" that looked like a
    correlator bug. Fixed by scoping `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` to just
    the correlation call in `CdpBridgePoc.RunCorrelationAsync` (restored in a `finally` block) —
    deliberately scoped to this one code path rather than making the whole app DPI-aware via
    manifest, which is deferred as a larger, separately-tested change per explicit user decision.
    (b) Two independent hit-test calls (`Discover()` and a separate `TryFindDocumentRoot()` call)
    could resolve slightly different rects for what should be the same Document element. Fixed by
    deriving the Document anchor rect from the same `Discover()` chain instead of a second
    independent hit-test.
  - Manually verified end-to-end against a real Brave instance: correctly matched an `<h1>` heading
    (confidence 0.98) and an `<a>` hyperlink (confidence 0.99) hovered on `https://example.com/`.
  - Regression Audit (code-review subagent, independent of implementation rationale) found two
    in-scope diagnostic-quality issues, both fixed before this sub-phase was considered done:
    - The initial `SetThreadDpiAwarenessContext` call's own success wasn't checked, so a failure
      (e.g. an OS predating Windows 10 1703's PMv2 support) would silently proceed in the wrong
      coordinate space with no diagnostic distinguishing it from a genuine non-match. Fixed by
      appending a warning line to the harness report when the initial call fails.
    - `CorrelateAsync`'s single broad `catch (Exception)` could mask a real CDP-response-shape bug
      (e.g. an unexpected JSON field type causing `JsonNode.GetValue<T>()` to throw) as an ordinary
      "no match found" result. Fixed by narrowing the catch to the specific exception types that
      represent legitimate heuristic-can-fail cases (JSON-shape/type mismatches, WebSocket
      timeouts/cancellation), letting any other exception type propagate as a genuine defect.
  - No other issues found: build succeeds with no new warnings; `BrowserDomTreeWalker`/
    `RectClipHelper` conventions are reused consistently, not duplicated; the new
    `--cdp-poc-correlate` command-line branch in `Program.cs` does not alter normal (no-args)
    startup behavior; the DPI-awareness context switch is thread-local only and does not leak to
    other threads or persist after the correlation call returns.
- **Sub-phase 3 — Read-only DevTools property display:** `CdpPropertyReader.ReadAsync`
  (`WindowWorks.App/Cdp/CdpPropertyReader.cs`) fetches `style.display`, `style.visibility`,
  `class`, `id`, `innerText`, and computed bounding box (page coordinates) for the correlated node
  via `Runtime.callFunctionOn` (reading `getComputedStyle`/direct property access on the resolved
  JS object) plus `DOM.getBoxModel`. `CdpBridgeAttempt.TryReadAsync`
  (`WindowWorks.App/Cdp/CdpBridgeAttempt.cs`) orchestrates the full discover → connect → correlate
  → read flow per selection, and `PropertyInspectorController.TryAppendDevToolsPropertiesAsync`
  wires this into the existing property-read pipeline: a new "DevTools Properties" grid section
  (`PropertyInspectorPropertySource.DevTools`) is appended only when a Chromium-family top-level
  window is detected, a live CDP endpoint is found, and correlation succeeds — otherwise the
  section is silently absent, never shown as an error, per the plan's requirement. All rows were
  initially read-only. Manually verified: the section appeared with correct live values for a real
  `https://example.com/` `<h1>` element in a running Edge instance.
- **Sub-phase 4 — DevTools write path + hide/show action:** `CdpPropertyWriter.WriteStyleAsync`
  (`WindowWorks.App/Cdp/CdpPropertyWriter.cs`, new) writes a single inline style property via
  `Runtime.callFunctionOn` calling `this.style.setProperty(property, value)` against the resolved
  DOM object — chosen over `DOM.setAttributeValue` on the raw `style` attribute string (which would
  risk clobbering other existing inline styles) or `CSS.setStyleTexts` (a heavier stylesheet-edit
  round trip). `CdpBridgeAttempt` gained `TryWriteStyleAsync`, sharing a common
  `TryReadOrWriteAsync` orchestration with the read path via an optional `writeStep` callback — the
  write path re-discovers the endpoint and re-correlates fresh per write (a resolved
  `BackendNodeId`/CDP `objectId` cannot be safely reused across a new WebSocket connection), then
  reads back properties in the same connection for the sync-rule refresh.
  `style.display`/`style.visibility` rows are now editable (`EditorKind.Text`) using the existing
  per-property commit model (Enter/focus-loss/Apply), matching Phase C's behavior — not the
  superseded Apply/Cancel staging design. `PropertyInspectorController.TryWriteProperty` routes
  DevTools-sourced writes to a new `TryWriteDevToolsStyle` method; all other DevTools-sourced
  properties remain read-only. Per the sync rule, every successful write (UIA or DevTools)
  re-fetches both full property sets and refreshes the whole grid.
  - Manually verified end-to-end against a real Edge instance (launched with
    `--remote-debugging-port=9222`, navigated to `https://example.com/`): editing `style.visibility`
    produced the status message "style.visibility updated through DevTools (CDP). Live properties
    were refreshed," confirming the write → CDP call → re-fetch → UI update pipeline works
    correctly.
  - Regression Audit (code-review subagent, independent of implementation rationale) found and the
    following were fixed before this sub-phase was considered done:
    - DevTools CDP operations (read-append during `ReadAndShowAsync`/`CompleteWrite`, and writes via
      `TryWriteDevToolsStyle`) were not serialized against each other, so a concurrently-triggered
      read and write (or two overlapping reads) against the same element could open independent CDP
      connections with no mutual exclusion. Fixed by adding a dedicated
      `_devToolsOperationGate` (`SemaphoreSlim(1, 1)`) that all DevTools CDP round trips now acquire.
    - `CompleteWrite` (the shared post-write refresh helper for UIA writers) re-checked selection
      identity once before its DevTools re-append, but not afterward — since that append can take up
      to `DevToolsOperationTimeout` (8s), a selection change or teardown during that window could
      result in a stale DevTools property set being reported as a successful refresh. Fixed by
      adding the same post-append identity re-check already present in `TryWriteDevToolsStyle`.
    - `CdpPropertyWriter.WriteStyleAsync` never inspected `Runtime.callFunctionOn`'s response for
      `exceptionDetails` — a JS-level exception (e.g. an invalid CSS value) is reported by CDP as a
      normal response rather than a protocol error, so the write was unconditionally reported as
      successful even when the style was never actually changed. Fixed by checking for
      `exceptionDetails` and returning failure when present.
  - No other issues found: `CdpEndpointDiscovery`/`CdpDomCorrelator`/`CdpPropertyReader` correctly
    avoid caching backend node ids or CDP object ids across connections; `CdpClient` disposal and
    fault-handling paths are sound; `CdpBridgePoc.cs` remains in active use via the
    `--cdp-poc`/`--cdp-poc-correlate` manual-verification harnesses, not dead code.
- **Sub-phase 4 follow-up — post-hide/show re-correlation bugs (found and fixed via live
  self-testing, not manual user testing):** after sub-phase 4 landed, driving a real
  `display:none` → `display:block` round trip on a live Edge instance (using the AgentDebugToolkit
  `set-grid-cell` verb to edit the grid directly) surfaced three related bugs, all now fixed:
  - **Bug #3 (core):** once an element's `display` is set to `none`, Chromium removes it from the
    accessibility tree, so the original `AutomationElement` becomes permanently unusable
    (`ElementNotAvailableException`) — the *next* write (e.g. restoring `display:block`) had no way
    to re-locate the DOM node at all, since normal correlation depends on a live UIA rect. Fixed by
    adding `Cdp/CdpCorrelationCache.cs`: on every successful fresh correlation, `CdpBridgeAttempt`
    now caches `(WebSocketDebuggerUrl, BackendNodeId, LastKnownScreenPoint)` on the selection. When
    fresh correlation fails (rect/element unavailable, no match found, connection error), a new
    `TryFallbackToCacheAsync` reconnects directly to the cached WebSocket target and re-uses the
    cached `BackendNodeId` — CDP backend node ids are stable across the DOM node's lifetime and
    reusable on a fresh connection to the same page target, confirmed empirically.
  - **Bug #3b:** the cache-fallback path's post-write property read-back came back completely empty
    (`display=` `visibility=`) even though the write itself succeeded. Root cause: a bare/fresh
    `CdpClient` connection that skips `CdpDomCorrelator`'s normal `DOM.enable`/`DOM.getDocument`
    priming leaves `DOM.pushNodesByBackendIdsToFrontend` (used internally by
    `CdpPropertyReader.ResolveNodeIdAsync`) silently unable to resolve anything — no exception, just
    nulls. Fixed by calling `DOM.enable` + `DOM.getDocument(depth:1)` at the start of
    `TryFallbackToCacheAsync`, exactly as the normal correlation path already does.
  - **Bug #3c:** after bugs #3/#3b were fixed, the DevTools section correctly reappeared after
    restoring `display:block`, but the **UIA Properties section did not** — because Chromium
    creates an entirely new accessibility node when a previously-hidden element becomes visible
    again, so the original cached `AutomationElement` reference can never become valid again, even
    once the underlying DOM node is visible. Fixed by caching the center point of the picked
    element's screen rect (`LastKnownScreenPoint`) alongside the CDP correlation cache entry, and
    adding `PropertyInspectorSelection.TryRebindSelectedElementAtPoint(x, y)` (calls
    `AutomationElement.FromPoint` at that cached point). `PropertyInspectorController.
    TryWriteDevToolsStyle` now retries the UIA property read once via this rebind when the first
    post-write read comes back null, before falling back to a DevTools-only result.
  - All three verified end-to-end via live self-testing against a real Edge instance
    (`--remote-debugging-port=9222`) using AgentDebugToolkit's `set-grid-cell` verb to drive
    `style.display` between `none`/`block`: final verification showed both "UIA Properties" and
    "DevTools Properties" groups present after the round trip, with status message "style.display
    updated through DevTools (CDP). Live properties were refreshed."
  - Temporary diagnostic logging added during this investigation (in `CdpBridgeAttempt.cs` and
    `CdpPropertyWriter.cs`, writing to `%TEMP%\cdp-write-diag.log`) was fully removed once the fixes
    were confirmed working; no diagnostic-only code remains.
- **Refresh button:** a manual Refresh button (`AutomationId=RefreshButton`, mirroring
  `PickerElementTreeWindow`'s existing Refresh button styling/icon) was added to the Property
  Inspector window's title bar, since the UIA-reappearance fix above is a heuristic
  (screen-point-based) that could miss in edge cases (e.g. scroll/reflow moving the element), and
  because DOM changes made outside WindowWorks (e.g. directly in browser DevTools, or by page
  script) are otherwise never picked up without re-picking the element. `PropertyInspectorWindow`
  gained a `RefreshRequested` event; `PropertyInspectorController.BeginRefresh`/`RefreshAsync` (in
  `PropertyInspectorController.cs`) re-runs the full UIA+DevTools property read for the current
  selection on demand, reusing the same full re-fetch sync rule as any write, gated by the same
  single-in-flight-operation semaphore used for writes so a refresh can't race a concurrent write.
  Verified working via live self-testing: clicking the button produced status message "Properties
  refreshed." with the grid correctly repopulated.


## 1. Goal

Let the user pick any UI element — a native Win32/WPF control, or (later) a browser DOM
element — the same way they already pick elements for reparenting (yellow hollow-border
highlight boxes, hover + click-to-select), then view that element's properties in an editable
grid and apply changes back to the live element. This is a **read/inspect + write/tamper** tool,
Inspect.exe-style, not a reparenting feature — it does not move, crop, or embed anything.

Relationship to `REPARENT_FEATURE_PLAN.md`: this is a sibling feature. It reuses the same
picker infrastructure (`WindowPickerSession`, `PickerHighlightWindow`, the yellow-box hover/list
UX) but does **not** reuse the crop-and-reparent mechanics (§6.5/§8 of that doc) — there is no
region-crop step here, only "select one element, inspect/edit it."

## 2. Scope decisions already made (do not re-litigate without user request)

- **Both native controls and browser DOM elements** are in scope, built in that order (native
  first) as separate phases — see §4.
- **Selection UX**: reuse the existing yellow hollow-border hover-highlight + click-to-select
  picker flow (same visual style as `PickerHighlightWindow`), but with **no crop-region step** —
  selecting a box goes straight to the property grid for that element.
- **Editable property set (native controls, Phase A/C)**: broader than the "safe" minimal set —
  includes Name/Text, Enabled, Visible, Value (`ValuePattern`), Toggle state (`TogglePattern`),
  **and** raw position/size/bounds (Left/Top/Width/Height).
- **Write strategy**: try the matching UIA control pattern first (e.g. `ValuePattern.SetValue`,
  `TogglePattern.Toggle`, `ExpandCollapsePattern`, etc.) since this routes through the target
  app's own message handling and keeps internal state/visual state in sync. Fall back to raw
  Win32 calls (`SetWindowText`, `EnableWindow`, `ShowWindow`, `SetWindowPos`/`MoveWindow`) only
  when UIA reports the corresponding pattern unsupported. Position/size edits will almost always
  go through the Win32 fallback path, since there is no dedicated UIA "resize" pattern.
- **Apply/Cancel workflow — SUPERSEDED, see Phase C status above.** Originally: the property grid
  would have explicit **Apply** and **Cancel** buttons, with edits staged locally and never
  pushed until Apply is clicked (with a pre-Apply confirmation prompt for risky edits). This was
  **changed during implementation per explicit user request**: edits now commit immediately
  (Enter/focus-loss for text/range, immediately-on-change for toggle/combo/checkbox-style
  editors), with a per-property Apply button as an additional explicit-commit affordance for
  text/range fields rather than a single end-of-session gate. Left here as historical rationale
  only — do not treat this bullet as current behavior.
- **Build order**: proof-of-concept first for *both* paths before either gets hardened into a
  real feature — see the phase breakdown in §4. Do not skip straight to a "concrete
  implementation" phase for one path while the other path's POC hasn't even been attempted; the
  point of doing both POCs first is to surface path-specific blockers early (e.g. discovering a
  UIA pattern doesn't behave as expected, or a DOM property can't be safely written) before
  committing to deeper investment in either.
- **CDP was originally out of scope** (per the user's standing decision recorded in
  `REPARENT_FEATURE_PLAN.md` Phase 6 — "more complex, we will see other options"). This has since
  been **reopened**: Phase E (§4, below) adds an optional Chrome DevTools Protocol (CDP) bridge as
  a second, additive property section alongside UIA — see Phase E for the full design. Phases A-D
  below (UIA-only) are otherwise unaffected and remain the baseline that always works, with or
  without a DevTools bridge connection.

## 3. Why UIA-first / Win32-fallback (rationale, for future reference)

- UIA patterns (`ValuePattern.SetValue`, `TogglePattern.Toggle`, etc.) go through the control's
  own internal handling, so the app's own event handlers fire and internal state stays
  consistent with what's displayed — fewer side effects.
- Raw Win32 calls bypass the app's own logic entirely. `SetWindowText` only updates the caption
  the OS painted; many apps don't hook `WM_SETTEXT` for business logic, so text can visually
  change while the app's internal model silently disagrees. `SetWindowPos`/`MoveWindow` on a
  child control can break a parent's own layout/anchoring logic if the parent isn't expecting an
  externally-triggered resize (no complete, framework-agnostic layout-invalidation guarantee).
- Net effect: prefer UIA when available; treat Win32 fallback as inherently higher-risk, and make
  sure the Apply-time confirmation prompt (§2) reflects that distinction (e.g. flag Win32-fallback
  edits distinctly from UIA-pattern edits in the confirmation summary).

## 4. Phased implementation plan

### Phase A — UIA (native controls) proof of concept

Goal: prove the full pick → inspect → edit → apply loop works end-to-end for native Win32/WPF
controls, with the bare minimum needed to validate the approach — not production-ready code.

Scope:
1. Reuse `WindowPickerSession`'s existing hover/click picker (`Confirmed` event, giving an
   `AncestorChainEntry`/equivalent element handle) as the selection source — no new picker UI
   needed for this phase; only remove/bypass the crop-entry step so a click goes straight to
   selection.
2. On selection, resolve the `AutomationElement` for the picked HWND/control and read a small
   fixed set of properties via UIA (Name, IsEnabled, BoundingRectangle, and whichever of
   `ValuePattern`/`TogglePattern` the element supports) — display them in a bare-minimum grid
   (even an unstyled `DataGrid` is fine for the POC).
3. Implement Apply for just 2-3 property types end-to-end (e.g. Name/Text via
   `ValuePattern.SetValue` with `SetWindowText` fallback, Enabled via `EnableWindow`, Toggle via
   `TogglePattern.Toggle`) to prove both the UIA-first and Win32-fallback write paths actually
   work and are distinguishable in the Apply confirmation prompt.
4. Confirm Cancel correctly discards staged edits without touching the live element.
5. Explicitly **not** in scope for this POC: position/size editing, a polished property grid
   UI, error/rollback handling beyond a basic try/catch, or persistence of any kind. Those move
   to Phase C.

Exit criteria: pick a real running app's control, see accurate live property values, edit at
least a text/name property and a toggle/enabled property, apply, and observe the live app
actually reflect the change — proving the mechanism, not the polish.

### Phase B — Browser DOM element (UIA-only) proof of concept

Goal: same end-to-end loop, but for a DOM element inside a Chromium-family browser window,
using UIA only (no CDP).

Scope:
1. Reuse the browser-window detection + DOM-tree walk validated during the Phase 6 (reparenting)
   feasibility spike (class-name match against a Chromium/Chrome family allowlist, UIA
   `Document`-rooted `TreeWalker`/`FindAll` traversal) to enumerate selectable DOM elements under
   the cursor.
2. Reuse the same yellow-box hover/click picker visuals as Phase A, fed DOM element rects instead
   of native control rects (this rect-source swap was already validated as visually compatible
   during the Phase 6 spike).
3. On selection, read whatever properties/patterns the DOM element's UIA representation actually
   exposes (Name, IsEnabled, BoundingRectangle; check for `ValuePattern`/`TogglePattern`/
   `InvokePattern` support — these were observed present on some YouTube elements during the
   Phase 6 spike, e.g. `InvokePattern` on a button, `ScrollItemPattern` on the video player group).
4. Attempt a write via whatever pattern is available (e.g. `ValuePattern.SetValue` on an editable
   text field, `TogglePattern.Toggle` on a checkbox-like element) and observe whether the live
   page actually reflects it. **This is the key open question this POC must answer**: how much of
   a DOM element's state is actually writable through UIA alone (no CDP), since Chromium's
   accessibility-tree bridge historically has much more read support than write support. Record
   findings plainly (which property types worked, which didn't, any exceptions/pattern-not-
   supported errors) — this determines how much Phase D can actually promise.
5. Explicitly **not** in scope for this POC: position/size editing for DOM elements (DOM layout
   is governed by CSS the browser recomputes; a raw UIA/Win32-style resize doesn't map onto DOM
   elements the way it does onto native child HWNDs — this needs its own design discussion before
   Phase D, not an assumption baked in here), polished UI, or persistence.

Exit criteria: pick a real DOM element on a real page, see accurate property values, and have a
documented, evidence-based answer to "what subset of DOM element properties can actually be
written via UIA alone" — even if that answer turns out to be "very little" or "none reliably."
That answer is itself the deliverable of this phase, not just a successful demo.

### Phase C — UIA (native controls) concrete implementation

Only starts once Phase A's POC has proven the mechanism works. Scope (to be broken down further,
with an explicit Pre-Build Decomposition confirmation from the user before implementation
starts, per this project's standing sub-agent/regression-auditor workflow):
- Full editable property set from §2 (Name/Text, Enabled, Visible, Value, Toggle, and
  position/size/bounds), each with a defined UIA-pattern-first + Win32-fallback write path.
- A real, styled property grid UI (likely a new WPF window/panel in `WindowWorks.App.UI`,
  following the existing project-reference direction: `WindowWorks.App -> WindowWorks.App.UI`).
- Apply/Cancel workflow with the confirmation prompt (§2), including a per-property "this will
  use UIA / this will use a Win32 fallback (higher risk)" indicator in the confirmation summary.
- Error handling: what happens if a write throws, partially succeeds (some properties applied,
  others failed), or the target element/window has gone away between pick and apply (matches the
  same kind of identity/invalidation concerns already handled by `CapturedIdentitySnapshot` in
  the reparenting feature — reuse that concept rather than inventing a new one).
- Decide (open question, not yet resolved): does editing require the picker to stay "live" (element
  reference held for the duration of editing), or does the user pick once and the property grid
  becomes a static snapshot with its own re-validation before Apply? Needs a decision before
  implementation, not assumed.

### Phase D — Browser DOM element concrete implementation

Only starts once Phase B's POC has produced its evidence-based answer on what's actually
writable via UIA for DOM elements, and Phase C has landed (so the property grid UI/Apply-Cancel
infrastructure already exists to extend rather than re-build). Scope depends directly on Phase
B's findings:
- If Phase B found a meaningful writable subset: extend the Phase C property grid to also accept
  DOM elements as a selection source (reusing the picker extension from Phase B's POC), scoped to
  only the properties/patterns Phase B proved actually work.
- If Phase B found little/nothing reliably writable via UIA: this phase's scope shrinks to
  **inspection-only** for DOM elements (read the property grid, no Apply capability, or Apply
  disabled/greyed out with an explanation) — do not force a write capability that Phase B's
  evidence didn't support. This must be discussed with the user again at that point rather than
  assumed either way right now.
- Revisit whether the standing CDP-deferral decision should be reopened at this point, if Phase
  B's findings are too limited to be useful — but only if the user raises it; do not reopen it
  proactively.

### Phase E — DevTools (CDP) bridge as an additive second property section

Only starts once Phase D has landed (so the two-section grid has a real UIA-only baseline to
extend, not replace). Reopens the CDP-deferral decision from §2/Phase B — user has confirmed this
is worth doing, framed specifically as **additive**, not a replacement for the UIA path.

**Goal:** For browser DOM elements only, show a second, clearly-separated "DevTools Properties"
section in the same property grid, alongside the existing "UIA Properties" section (which is
always present and unaffected). The DevTools section surfaces DOM/CSS-level properties/actions
that UIA cannot reach (`style.display`, `style.visibility`, `class`, `innerText`, arbitrary
attributes, computed styles, dispatching real DOM events) and is the mechanism for real
element-hiding (§ROADMAP's now-removed "hide element" backlog item — this phase is what
"remove it from backlog" meant: it becomes a concretely planned phase instead of a vague future
idea).

**Design decisions (confirmed with the user during planning):**
- **Two sections, not a merged/unified list.** The DataGrid groups properties by a `Source`
  field (`PropertyInspectorPropertySource.Uia` vs `.DevTools`), using WPF's
  `CollectionViewSource.GroupDescriptions` (or an equivalent grouped-`ItemsSource` approach) with
  a group header per section. UIA section always renders (existing Phase A/C behavior,
  completely unchanged). DevTools section only renders when a live CDP connection was
  successfully established AND the picked element was successfully correlated to a DOM node
  (§Element correlation below) — otherwise it's simply absent (not shown empty, not shown as an
  error state, unless the user explicitly attempted a DevTools-only action).
- **Synchronization model: full re-fetch after every write, regardless of which section wrote
  it.** There is no dependency-tracking or partial-invalidation logic between the two sections —
  after *any* successful Apply (UIA or DevTools), re-read both the full UIA property set and the
  full DevTools property set for the same element and refresh the entire grid. This mirrors the
  existing "commit → verify → redisplay" cycle Phase A/C already uses for UIA writes, just
  extended to always refresh both sections together rather than only the section that changed.
  Rationale (from planning discussion): CDP is one-directional (WindowWorks drives the browser;
  the browser does not proactively notify WindowWorks of arbitrary UIA-side effects of a DOM
  change, or vice versa), so there's no reliable event-driven alternative — blanket re-fetch is
  the only dependable mechanism, not a limitation to engineer around. A future optional
  enhancement (not required for this phase) could additionally subscribe to UIA
  `AutomationPropertyChangedEventHandler` for faster UIA-section refresh after a DevTools write,
  but this is a supplement, not a replacement, for the full re-fetch and can be deferred.
  Known residual limitation (same as today's UIA-only inspector): if the page's own JS changes the
  DOM independently of any WindowWorks-initiated write, the grid will look stale until the next
  manual re-fetch or re-selection — not solved by this phase, consistent with existing behavior.
- **Browser/session requirements:** CDP requires the target browser process to have been launched
  with (or relaunched with) `--remote-debugging-port=<port>` — WindowWorks cannot enable this on
  an already-running browser instance without relaunching it. Phase E must therefore detect
  whether the target browser is already debuggable (attempt a connection to the well-known
  debugging port / discover it) and, if not, clearly tell the user in the UI that the DevTools
  section is unavailable for this browser instance and why (not a silent absence) — this is a
  real, expected, common case, not an error to hide.
- **Scope: Chromium-family only.** CDP is a Chromium-specific protocol (Chrome, Edge, Brave,
  etc.); Firefox has no CDP support (it uses a different remote protocol, WebDriver BiDi, which is
  explicitly out of scope for this phase — see §5). The existing Chromium-family class-name
  allowlist detection (already used for the DOM Element Tree feature) is reused to decide whether
  to even attempt a CDP connection.

**Scope breakdown (sub-phases within Phase E; full Pre-Build Decomposition confirmation with the
user still required before implementation starts, per this project's standing workflow):**

1. **CDP bridge proof of concept.** Establish a WebSocket connection to a Chromium browser's
   DevTools endpoint (via an existing .NET CDP client library, or a minimal hand-rolled
   WebSocket + JSON-RPC client if a suitable library isn't available/desired), enumerate open
   tabs/targets, and successfully execute one trivial round-trip command (e.g.
   `Runtime.evaluate` on a simple expression) against a real running browser tab launched with
   `--remote-debugging-port`. Exit criteria: prove the connection/command mechanism works at all,
   nothing UI-facing yet.
2. **Element correlation.** Given a UIA-picked DOM element (bounding rect + whatever
   Name/ControlType/attributes UIA already exposes), find the corresponding CDP DOM node. Likely
   approach: use CDP's `DOM.getDocument` + `DOM.getBoxModel`/`DOM.querySelector`-style traversal
   and match by bounding-rect overlap plus tag name/attribute similarity, since UIA and CDP node
   identities are unrelated. This is explicitly a heuristic, not a guaranteed 1:1 mapping — record
   how often/reliably it succeeds during this sub-phase, since that directly determines Phase E's
   real-world usefulness. If correlation fails for a given element, the DevTools section for that
   element is simply absent (same "not shown, not an error" rule as above).
3. **Read-only DevTools property display.** Once correlated, fetch and display a first real
   property set in the new "DevTools Properties" grid section: `style.display`,
   `style.visibility`, `class`, `id`, `innerText`, and computed bounding box in page coordinates.
   Read-only at this sub-phase — no Apply/write yet. Exit criteria: real DOM properties visibly
   populate the second section for a real picked element.
4. **DevTools write path + hide/show action.** Add editable versions of `style.display`/
   `style.visibility` (the actual "hide element" capability), routed through CDP's
   `DOM.setAttributeValue`/`CSS.setStyleTexts` (or `Runtime.callFunctionOn` executing a small JS
   snippet against the correlated node, whichever proves more reliable in sub-phase 1-2's
   findings). Wire this into the current immediate-commit editing model (Phase C's superseded-§2
   note above) exactly like a UIA write — commit on Enter/focus-loss or an Apply button, per the
   editor kind — with the full-re-fetch synchronization rule above applied after each commit. Exit
   criteria: toggling `style.display` on a real picked DOM element actually hides/shows it in the
   live browser window, and both grid sections refresh correctly afterward.
5. **Broader DevTools property/action set (time-permitting, lower priority than 1-4):** arbitrary
   attribute read/write, dispatching real DOM events (`click`/`input`/`change`) via
   `Input.dispatchMouseEvent`/`Runtime.callFunctionOn`, and reading full computed CSS styles. Only
   pursue after 1-4 are solid and manually confirmed — do not front-load this before the core
   hide/show capability is proven end-to-end.

**Exit criteria for Phase E overall:** pick a real DOM element in a Chromium-family browser
launched with remote debugging enabled, see both a "UIA Properties" and a "DevTools Properties"
section in the same grid, edit `style.display` in the DevTools section, click Apply, and observe
the element actually disappear from the live rendered page — with the UIA section's own values
(e.g. `IsOffscreen`) refreshing afterward to reflect the change, proving the full-re-fetch
synchronization rule works in practice, not just in theory.

## 5. Explicitly out of scope (for all phases, unless the user reopens them)

- Firefox/WebDriver BiDi support for the DevTools section — Phase E is Chromium/CDP-only; a
  Firefox equivalent would need its own separate design and is not assumed.
- Persistence/presets of edited property values across sessions — not discussed, not assumed.
- Any element type beyond native Win32/WPF controls and browser DOM elements (e.g. UWP/XAML
  islands, other rendering engines) — not discussed, not assumed.
- Automatically launching/relaunching a browser instance with `--remote-debugging-port` on the
  user's behalf — Phase E only attempts to connect to an already-debuggable instance and clearly
  reports when one isn't available; auto-relaunching a user's existing browser session (closing
  their tabs/session to add a flag) is a much more invasive behavior not assumed in scope here.

## 6a. Manual testing checkpoints (process note, not a technical decision)

The user wants to be prompted for manual testing/confirmation as early and as often as
reasonably possible while implementing this feature — not just once at the end of a phase.
Concretely: after each meaningfully-testable increment within a phase (e.g. "picker now selects
a real element and reads its properties", "first property write via UIA succeeds", "Win32
fallback write succeeds", "Apply/Cancel staging works", "confirmation prompt appears correctly"),
stop and ask the user to manually verify before proceeding to the next increment, rather than
batching several increments together and asking once at the end of a whole phase. This is in
addition to (not a replacement for) the existing Regression Auditor Protocol's own review
checkpoints.

## 6. Known constraints carried over from the reparenting feasibility spike

- DOM elements always report `NativeWindowHandle == 0` — confirmed during the Phase 6 spike.
  This is irrelevant to property editing itself (UIA doesn't need a real HWND to read/write
  patterns on an element), but is a reminder that Win32-fallback writes (§3) are **not possible
  at all** for DOM elements — there is no HWND to call `SetWindowText`/`EnableWindow`/
  `SetWindowPos` against. For DOM elements, if the UIA pattern isn't supported, there is no
  fallback; the property is simply not editable through this tool. This must be surfaced clearly
  in the Phase B/D UI (e.g. grey out non-UIA-writable properties) rather than silently failing.
- Chromium exposes DOM content via UIA without any `--remote-debugging-port` flag — already
  proven; Phase B's POC does not need to re-validate this, only build on it.
