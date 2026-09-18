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
- [ ] Property Inspector feature: element-picker + property editing for
      non-top-level elements (e.g., browser/DOM child elements). Currently
      the Property Inspector only shows properties for the top-level window;
      user wants to pick and inspect/edit a specific child element inside it
      too (e.g. a DOM node in a browser window). Explicitly deferred by the
      user until after the current Property Inspector increment (Enter/Apply
      commit fixes, ComboBox theming, toggle-switch UI, elevated-write
      handling) is verified and committed.
