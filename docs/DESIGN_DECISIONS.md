# Design Decisions

## Shortcut settings persistence: hardcoded defaults vs. user overrides

- **Decision:** `AppSettings.HotkeyCommandPalette` / `HotkeyEmergencyReset` (and other gesture defaults) in `WindowWorks.App/Models/AppSettings.cs` are fallback defaults only, used when `settings.json` doesn't yet contain a value (first run or missing key).
- **Rationale:** Once a user edits a shortcut via Settings → Shortcuts (popup capture), `ShortcutsSettingsViewModel.ToDictionary()` writes the new value into the saved settings dictionary on Save, which `SettingsWindow.BtnSave_Click` persists to `settings.json`. On next load, `Persistence.LoadSettings()` deserializes the saved JSON, so the user's value takes precedence over the hardcoded default. `HotkeyApplyService`/`HotkeyManager` re-register the live hotkey immediately after Save.
- **Alternatives considered:** None; this is the intended and already-implemented behavior — no code changes were required, only confirmation of the flow.

## 2026-09-09 — Hybrid WinForms and WPF desktop UI

- **Decision:** Use a WinForms tray/bootstrap application with a referenced WPF UI project for settings and overlays.
- **Rationale:** The tray lifecycle and native window integration remain in the application project while WPF provides the settings and overlay presentation layer.
- **Alternatives considered:** A single UI framework; the existing split is retained.
