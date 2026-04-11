WindowWorks - Phase 1 skeleton

Overview

This repository contains a Phase 1 skeleton for WindowWorks — a tray-first Windows utility to allow quick window-level transformations (opacity, topmost, click-through) with minimal friction.

Build / Run

Requirements: .NET 10 SDK, Windows 10/11, Visual Studio 2022+ or VS 2026.

1. Open the solution WindowWorks.sln in Visual Studio or run from the command line:
   dotnet build src/WindowWorks.App/WindowWorks.App.csproj
   dotnet run --project src/WindowWorks.App/WindowWorks.App.csproj

Phase 1 Acceptance Criteria

- Tray-only app with NotifyIcon and compact context menu
- Global hotkeys: Win+` opens command palette placeholder; Win+Shift+R triggers Emergency Reset
- Gestures: Ctrl+MouseWheel adjusts opacity; Ctrl+Alt+Click toggles topmost; Ctrl+Shift+Click toggles click-through
- Transient HUD shows after actions with an Undo button that reverts last change
- Presets load from embedded JSON seed and can be applied to active window
- Emergency Reset restores all modified windows
- No admin required; no code injection into other processes

QA Checklist

- Start the app and confirm NotifyIcon appears
- Apply a preset from the tray menu to the active window
- Use Ctrl+MouseWheel on a window and observe HUD and opacity change
- Use Ctrl+Alt+Click and Ctrl+Shift+Click and observe toggles
- Press Win+Shift+R to restore modified windows
- Confirm presets are loaded from docs/default_presets.json

Files of interest

- src/WindowWorks.App/* - main app code
- docs/default_presets.json - seed presets
- docs/presets.schema.json - JSON schema for presets

Notes

This is a minimal, heavily-commented skeleton. Many TODOs remain (command palette UI, robust persistence, presentation detection/suppression). Use this as a starting point for Phase 1 implementation.
