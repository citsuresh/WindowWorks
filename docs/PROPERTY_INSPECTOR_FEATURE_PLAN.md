# Property Inspector Feature — Implementation Plan

## Development progress

> Keep this ledger current as implementation progresses. Each increment records its code status,
> regression-audit result, and the user's manual-test confirmation before the next increment starts.

### Phase A — UIA (native controls) proof of concept

- **Status:** Increments 1 and 2 implementation complete (including native Element Tree selection
  for non-HWND UIA controls and UIA-first Name/Text writes with a SetWindowText fallback);
  automated review and manual-test evidence pending.
- **Regression fixes:** Keyboard-only shortcut confirmation/cancellation, failed hotkey
  registration reporting/restoration, and selection-time UIA identity freezing are fixed;
  automated review and manual-test evidence remain pending.
- **Entry point:** Permanent tray-menu command: **Inspect UI Element...**
- **Planned increments:**
  1. Inspector-mode picker invocation, selection handoff, and read-only property display.
  2. UIA-first text/name write, with `SetWindowText` fallback distinguished in the confirmation.
  3. Enabled/toggle writes, staged Apply/Cancel behavior, and pre-Apply identity revalidation.
- **Manual testing:** Required after every meaningfully testable increment per §6a.
- **Regression auditing:** Required after every increment and once after Phase A is complete.

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
- **Apply/Cancel workflow**: the property grid has explicit **Apply** and **Cancel** buttons —
  edits are staged locally in the grid and never pushed to the live element until Apply is
  clicked. Clicking Apply shows a confirmation prompt (summarizing what will change) before the
  writes are actually issued, since some edits (resizing a child control unexpected by its
  parent's layout logic, disabling a critical control) can visibly break the target app.
- **Build order**: proof-of-concept first for *both* paths before either gets hardened into a
  real feature — see the phase breakdown in §4. Do not skip straight to a "concrete
  implementation" phase for one path while the other path's POC hasn't even been attempted; the
  point of doing both POCs first is to surface path-specific blockers early (e.g. discovering a
  UIA pattern doesn't behave as expected, or a DOM property can't be safely written) before
  committing to deeper investment in either.
- **CDP is out of scope** (per the user's standing decision recorded in
  `REPARENT_FEATURE_PLAN.md` Phase 6 — "more complex, we will see other options"). Browser DOM
  element property editing in Phase B/D uses **UIA only** — this constrains what's actually
  editable for DOM elements (UIA's write surface for Chromium's accessibility tree is narrower
  than full CDP DOM/CSS access; see §6 for what's realistically achievable).

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

## 5. Explicitly out of scope (for all phases, unless the user reopens them)

- CDP-based DOM property/attribute editing (`DOM.setAttributeValue`, `CSS.setStyleTexts`, etc.) —
  standing decision, see §2.
- Crop-and-reparent of the picked element — that is Phase 6 of `REPARENT_FEATURE_PLAN.md`, a
  separate feature this doc's picker reuse does not trigger.
- Persistence/presets of edited property values across sessions — not discussed, not assumed.
- Any element type beyond native Win32/WPF controls and browser DOM elements (e.g. UWP/XAML
  islands, other rendering engines) — not discussed, not assumed.

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
