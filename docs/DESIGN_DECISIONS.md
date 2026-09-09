# Design Decisions

## 2026-09-09 — Hybrid WinForms and WPF desktop UI

- **Decision:** Use a WinForms tray/bootstrap application with a referenced WPF UI project for settings and overlays.
- **Rationale:** The tray lifecycle and native window integration remain in the application project while WPF provides the settings and overlay presentation layer.
- **Alternatives considered:** A single UI framework; the existing split is retained.
