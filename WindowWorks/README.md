WindowWorks

Overview

This repository contains WindowWorks — a tray-first Windows utility to allow quick window-level transformations (opacity, topmost, click-through) with minimal friction.

Build / Run

Requirements: .NET 10 SDK, Windows 10/11, Visual Studio Professional 2026 (recommended).

Usage / Build

Short commands (from repository root C:\MyFiles\Git\WindowWorks\WindowWorks):

1) Build from command line:

   dotnet build src/WindowWorks.App/WindowWorks.App.csproj

2) Run from command line:

   dotnet run --project src/WindowWorks.App/WindowWorks.App.csproj

Visual Studio (recommended for development):

- Open WindowWorks.sln in Visual Studio Professional 2026.
- Set 'WindowWorks.App' as the startup project.
- Press F5 (Start Debugging) or Ctrl+F5 (Start Without Debugging) to run.

The app targets Windows and requires the .NET 10 Windows Desktop workload to be installed.

Acceptance Criteria

- Tray-only app with NotifyIcon and compact context menu
- Global hotkeys: Win+` opens command palette placeholder; Win+Shift+R triggers Emergency Reset
- Gestures: Ctrl+MouseWheel adjusts opacity; Ctrl+Alt+Click toggles topmost; Ctrl+Shift+Click toggles click-through
- Transient HUD shows after actions with an Undo button that reverts last change
- Presets load from embedded JSON seed and can be applied to active window
- Emergency Reset restores all modified windows
- No admin required; no code injection into other processes

Recent UI / Behavior Improvements

- Settings: HUD color supports alpha embedded in the color string (#AARRGGBB). The transparency slider continues to be saved as HudTransparencyPercent for compatibility.
- Highlight settings: added a Border transparency slider (Border transparency (%) ) and live preview. HighlightBorderColor is saved as #AARRGGBB and HighlightBorderTransparencyPercent is also persisted.
- Settings window content now aligns top-left and individual pages update previews on load via deferred initialization (Loaded event) to avoid timing issues.
- ShortcutsSettingsControl: simplified event model (CLR event) and a ShortcutsSettingsViewModel type was added to begin MVVM migration.

QA Checklist (updated)

- Open Settings -> HUD and verify background color + transparency slider update the embedded preview
- Open Settings -> Highlight and verify border color, transparency slider and thickness update the preview
- Save settings and confirm returned JSON contains both the #AARRGGBB color value and the corresponding transparency percent keys for HUD and Highlight

Files of interest

- src/WindowWorks.App/* - main app code
- src/WindowWorks.App.UI/* - WPF UI controls and settings pages
- src/WindowWorks.App.UI/ViewModels/* - initial ViewModel(s) used for MVVM migration (ShortcutsSettingsViewModel)
- SeedData/default_presets.json - seed presets
- SeedData/presets.schema.json - JSON schema for presets

Notes

This repository is actively being migrated toward MVVM; some controls still use code-behind for dialog interactions and immediate UI previews. The current changes maintain backward compatibility by saving both new (#AARRGGBB) and legacy (percent) formats for colors/transparency.
If you want to consolidate on a single storage format (only #AARRGGBB), we can migrate and remove the legacy percent keys in a follow-up change.
