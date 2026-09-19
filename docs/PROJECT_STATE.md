# Project State

> This file is overwritten, not appended, at the end of each working session.

## Current Focus
- Property Inspector Phase E (DevTools/CDP bridge) sub-phases 5 and 6, including the DevTools
  relaunch auto-handoff follow-up, were completed, live-tested, reviewed, and committed/pushed
  this session as `d6c4a88` ("Property Inspector Phase E: DevTools attribute read/write +
  relaunch assist + auto-handoff").
- Sub-phase 5 (arbitrary DOM attribute read/write): `CdpPropertyReader` enumerates all HTML
  attributes as `attr.*` grid rows; `CdpPropertyWriter.WriteAttributeAsync` writes via
  `DOM.setAttributeValue`, using `DOM.pushNodesByBackendIdsToFrontend` to convert a
  `backendNodeId` into the frontend `nodeId` that command actually requires. Wired through the
  same full-re-fetch sync rule used by the style write path. Verified end-to-end against a live
  Edge instance.
- Sub-phase 6 (DevTools Relaunch Assist) + its auto-handoff follow-up: `CdpBrowserRelauncher`
  launches a second, independent browser process with `--remote-debugging-port` (shared temp
  profile, original window/process untouched), matching the original window's geometry with a
  DPI-aware conversion, bounded-polling for a live DevTools endpoint before reporting success.
  `CdpBrowserRelaunchHandoff` then automatically re-finds the same DOM element in the newly
  launched window: a CDP-fingerprint-based match (`CdpNodeFingerprint`, when the original had a
  live CDP correlation) or a strict, Document-anchored UIA-only fallback (the common case, since
  lacking CDP is the whole reason for relaunching) — falling back to the normal manual picker
  when the match is unavailable/ambiguous. Both the identity-capture step and the UIA-only search
  run bounded and off the WPF UI thread so an unresponsive UIA provider can't freeze the
  inspector or defeat the picker fallback. **Manually confirmed working live by the user**,
  including the UIA-only fallback path.
- Iterated on the relaunch's browser-profile behavior at the user's explicit direction: tried a
  unique-per-launch temp profile to stop stale tabs reappearing, then reverted to the original
  shared fixed temp profile because the user preferred keeping the existing (accepted) sign-in
  tradeoff over the new-tabs annoyance. Confirmed a true "new window on the real signed-in
  profile while the original stays open" is not achievable — Chromium's single-instance-per-
  profile lock means a second process pointed at the real profile just hands off to the already-
  running one and silently drops `--remote-debugging-port`.
- An independent `code-review` sub-agent pass over the full diff (12 modified + 3 new files)
  found one real, in-scope issue: two `_dispatcher.InvokeAsync` calls in
  `PropertyInspectorController.RelaunchDevToolsAsync` were unguarded against
  `InvalidOperationException` during app shutdown, unlike this file's established defensive
  pattern elsewhere. Fixed by wrapping both calls in `try/catch`. The reviewer's other finding
  (shared, non-unique relaunch profile directory) is the user's explicitly accepted tradeoff, not
  actioned.
- Fixed an unrelated stray-paste syntax error a user's IDE context had introduced into
  `CdpPropertyWriter.WriteAttributeAsync` (literal text `want you to open in ` accidentally
  inserted mid-statement) — removed, build confirmed green afterward.

## Open Tasks / Known Issues
- None outstanding for Phase E sub-phases 5-6 — confirmed working live, reviewed, committed
  (`d6c4a88`), and pushed to `origin/main`.
- Explicitly deferred/out of scope (per the feature plan, lower priority, not started): real DOM
  event dispatching (click/input/change via CDP) and full computed CSS style reads.
- The relaunch assist's shared temp browser profile does not preserve the original window's
  sign-in state — a known, user-accepted tradeoff (a profile-data-copy alternative was proposed
  and explicitly declined "we will see if I can live with it").

## Recently Changed Files
- `WindowWorks/src/WindowWorks.App/Cdp/CdpBrowserRelaunchHandoff.cs` (new),
  `CdpBrowserRelauncher.cs` (new), `CdpNodeFingerprint.cs` (new) — relaunch assist + auto-handoff.
- `WindowWorks/src/WindowWorks.App/Cdp/CdpPropertyReader.cs`, `CdpPropertyWriter.cs`,
  `CdpBridgeAttempt.cs`, `CdpCorrelationCache.cs`, `CdpEndpointDiscovery.cs` — attribute
  read/write path + fingerprint caching + endpoint readiness polling.
- `WindowWorks/src/WindowWorks.App/PropertyInspectorController.cs` — relaunch/handoff
  orchestration, dispatcher-guard fix.
- `WindowWorks/src/WindowWorks.App/WindowPickerSession.cs`,
  `WindowWorks.App.UI/PickerElementTreeWindow.xaml.cs`,
  `WindowWorks.App.UI/PropertyInspectorWindow.xaml(.cs)` — picker bug fixes blocking live testing.
- `docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md`, `docs/ROADMAP.md` — Phase E marked substantially
  complete.
- Committed together as `d6c4a88`, pushed to `origin/main`.
