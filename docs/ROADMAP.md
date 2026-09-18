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
      sub-phase 1 (CDP bridge proof of concept) and sub-phase 2 (element
      correlation) complete and manually verified (correlation confidence
      0.98-0.99 against a live Brave instance); sub-phases 3-5 (read-only
      display, write path/hide-show, broader property set) not yet started.
