# Project State

> This file is overwritten, not appended, at the end of each working session.

## Current Focus
- Cleaned up unconditional debug persistence artifacts (`settings-emitted.json`,
  `settings-saved-debug.json`) and finished the seed-data/preset persistence rework:
  app-level preset seed folder renamed from `docs` to `SeedData`, preset loading now
  prefers `%APPDATA%\WindowWorks\presets.json` with fallback to embedded seed data.
  Commit `dd76278` pushed to `origin/main`.

## Open Tasks / Known Issues
- No preset editor UI exists yet; `PresetManager.SavePresets` is currently only exercised
  on first-run seed persistence.
- Shortcut editing MVVM migration (popup-based capture flow) is complete for keyboard
  shortcuts; gesture edit buttons were intentionally removed (no popup editor for gestures).

## Recently Changed Files
- `WindowWorks/src/WindowWorks.App.UI/SettingsWindow.xaml.cs`
- `WindowWorks/src/WindowWorks.App/Persistence.cs`
- `WindowWorks/src/WindowWorks.App/PresetManager.cs`
- `WindowWorks/src/WindowWorks.App/WindowWorks.App.csproj`
- `WindowWorks/SeedData/default_presets.json` (renamed from `WindowWorks/docs/default_presets.json`)
- `WindowWorks/SeedData/presets.schema.json` (renamed from `WindowWorks/docs/presets.schema.json`)
- `WindowWorks/README.md`
- `docs/DESIGN_DECISIONS.md`
