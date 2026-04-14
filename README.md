# WindowWorks
WindowWorks is a lightweight tray-first Windows utility for quick window-level transformations (opacity, topmost, click-through) with a compact HUD and presets.

# Requirements
•	Windows 10/11

•	.NET 10 SDK

•	Visual Studio 2026 (recommended) or dotnet CLI

# Quick start
From repository root:
•	Build: dotnet build src/WindowWorks.App/WindowWorks.App.csproj

•	Run: dotnet run --project src/WindowWorks.App/WindowWorks.App.csproj Or open WindowWorks.sln in Visual Studio, set WindowWorks.App as startup project, F5 to run.

# Features
•	Tray icon with context menu and presets

•	Global hotkeys and mouse gestures for opacity / topmost / click-through

•	Transient HUD with undo

•	Presets loaded from docs/default_presets.json

•	Settings UI: HUD and Highlight colors support embedded alpha (#AARRGGBB) and legacy percent values for compatibility

