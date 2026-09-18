# Roadmap

> Edit this file deliberately when priorities change; it is not overwritten automatically each session.

## Near-term
- [ ] No planned work recorded.

## Backlog
- [ ] Window Reparenting feature: multi-monitor validation pass (picker
      overlay, crop math, and host-frame DPI-change handling when a
      reparented window/host frame crosses monitors with different DPI
      scaling). Deferred from the phased plan in
      `docs/REPARENT_FEATURE_PLAN.md` (§12 "DPI/multi-monitor correctness",
      §13 open items, Phase 4) because no multi-monitor test hardware is
      currently available. Pick up once such hardware is available;
      single-monitor DPI awareness is unaffected and proceeds normally in
      the phased plan.
- [ ] Window Reparenting feature: expose settings for the reparented-window
      overlay (`ReparentHostWindow.xaml.cs` — the floating minimize/
      maximize/close/reopen-original button strip). Currently all
      hardcoded with no Settings UI. Candidates identified but not yet
      scoped/prioritized: auto-hide delay (currently fixed 2.5s idle,
      `OverlayIdleHideDelay`), hover trigger zone size (currently top 20%
      of window height, `OverlayTriggerZoneHeightFraction`), an
      always-show vs. hover-only toggle, and overlay appearance (background
      tint/opacity, button size).
- [ ] Property Inspector feature: Phase E — DevTools (CDP) bridge as an
      additive second "DevTools Properties" grid section (alongside the
      existing always-present "UIA Properties" section), for Chromium-family
      browser DOM elements. Enables real hide/show (`style.display`/
      `style.visibility`) and other DOM-only properties UIA can't reach.
      Full design (element correlation, full-re-fetch sync rule after any
      write on either side, browser remote-debugging-port requirement,
      Chromium-only scope) is written up in full in
      `docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md` §4 Phase E. **In progress:**
      sub-phases 1-4 (CDP bridge PoC, element correlation, read-only display,
      write path + hide/show via `style.display`/`style.visibility`) complete
      and verified end-to-end against live Brave/Edge instances
      (correlation confidence 0.98-0.99; DevTools write confirmed refreshing
      both property sections). Three follow-up architectural bugs found via
      live self-testing (re-correlation after a `display:none` write losing
      its UIA anchor; a cache-fallback read-back priming bug; the UIA section
      not reappearing after show/hide because Chromium creates a new
      accessibility node) were all found and fixed — see
      `docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md` §4 Phase E "Sub-phase 4
      follow-up". A manual Refresh button was also added to the inspector
      window's title bar. Sub-phase 5 (broader DevTools property/action
      set: arbitrary attribute read/write, real DOM events, full computed
      CSS) not yet started — lower priority, deferred.
