using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WindowWorks.App
{
    /// <summary>
    /// Controls the NotifyIcon tray presence and main menu actions.
    /// Lightweight and keeps UI interactions off the main Program logic.
    /// </summary>
    public class TrayController : IDisposable
    {
        private WindowWorks.App.UI.HudWindow? _activeHud;
        private IntPtr _lastTargetHwnd = IntPtr.Zero;
        private readonly NotifyIcon _notifyIcon;
        private readonly HotkeyManager _hotkeyManager;
        private readonly WindowManager _windowManager;
        private readonly PresetManager _presetManager;
        private readonly AuditLog _auditLog;
        private readonly Persistence _persistence;
        private readonly Models.AppSettings _settings;
        public event EventHandler? ExitRequested;

        public TrayController(HotkeyManager hotkeyManager, WindowManager windowManager, PresetManager presetManager, AuditLog auditLog, Persistence persistence, Models.AppSettings settings)
        {
            _hotkeyManager = hotkeyManager;
            _windowManager = windowManager;
            _presetManager = presetManager;
            _auditLog = auditLog;
            _persistence = persistence;
            _settings = settings;

            _notifyIcon = new NotifyIcon();
            try
            {
                // Prefer bundled appicon.ico if present next to the project output
                var exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                string? icoPath = null;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var appDir = System.IO.Path.GetDirectoryName(exePath);
                    var localIco = System.IO.Path.Combine(appDir ?? string.Empty, "appicon.ico");
                    if (System.IO.File.Exists(localIco)) icoPath = localIco;
                }

                if (icoPath == null)
                {
                    var alt = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appicon.ico");
                    if (System.IO.File.Exists(alt)) icoPath = alt;
                }

                if (!string.IsNullOrEmpty(icoPath))
                {
                    try { _notifyIcon.Icon = new Icon(icoPath); }
                    catch
                    {
                        try { _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exePath); }
                        catch { _notifyIcon.Icon = SystemIcons.Application; }
                    }
                }
                else
                {
                    try { if (!string.IsNullOrEmpty(exePath)) _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exePath); else _notifyIcon.Icon = SystemIcons.Application; }
                    catch { _notifyIcon.Icon = SystemIcons.Application; }
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
            _notifyIcon.Text = "WindowWorks";
            _notifyIcon.Visible = true;

            // Single double-click handler to open settings
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

            BuildContextMenu();
            // Add Settings menu entry
            try
            {
                var menu = _notifyIcon.ContextMenuStrip;
                if (menu != null)
                {
                    menu.Items.Insert(0, new ToolStripMenuItem("Settings", null, (s, e) => ShowSettings()));
                    menu.Items.Insert(1, new ToolStripSeparator());
                }
            }
            catch { }

            // Subscribe to hotkey/mouse gesture events from manager
            _hotkeyManager.OpacityNudgeRequested += HotkeyManager_OpacityNudgeRequested;
            _hotkeyManager.ToggleTopmostRequested += HotkeyManager_ToggleTopmostRequested;
            // Subscribe to hotkey registration failures to notify the user via tray balloon
            _hotkeyManager.HotkeyRegistrationFailed += HotkeyManager_HotkeyRegistrationFailed;
        }

        public void Initialize()
        {
            // No-op for now. Left for symmetry and future async startup.
            // No longer subscribing to window manager debug events.
        }

        private void WindowManager_ProcessBlacklisted(object? sender, string processName)
        {
            try
            {
                string title = "Rendering fallback applied";
                string text = $"Opacity changes skipped for process '{processName}' because it caused rendering issues. You can adjust the blacklist in settings.";
                _notifyIcon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();

            // HUD duration was removed from tray menu per user request. Duration is configured via settings (default 1s).

            var presetsMenu = new ToolStripMenuItem("Presets");
            foreach (var p in _presetManager.LoadedPresets)
            {
                var item = new ToolStripMenuItem(p.Name);
                item.Tag = p;
                item.Click += (s, e) => ApplyPreset(item.Tag as Models.Preset);
                presetsMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(presetsMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Onboarding", null, (s, e) => ShowOnboarding()));
            menu.Items.Add(new ToolStripMenuItem("Open presets folder", null, (s, e) => _persistence.OpenAppFolder())); // Ensure method reference remains if any items existed previously
            menu.Items.Add(new ToolStripMenuItem("Emergency Reset", null, (s, e) => _auditLog.EmergencyReset(_windowManager)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty)));

            _notifyIcon.ContextMenuStrip = menu;
        }

        private async void ShowSettings()
        {
            try
            {
                var settings = _persistence.LoadSettings();
                // Show the new WPF settings window on an STA thread and retrieve updated settings as JSON
                var currentJson = System.Text.Json.JsonSerializer.Serialize(settings);
                var updatedJson = await WindowWorks.App.UI.SettingsWindow.ShowDialogModalAsync(currentJson).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(updatedJson))
                {
                    try
                    {
                        var dict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(updatedJson);
                        if (dict != null)
                        {
                            if (dict.TryGetValue("HighlightBorderColor", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) _settings.HighlightBorderColor = v.GetString() ?? _settings.HighlightBorderColor;
                            if (dict.TryGetValue("HighlightBorderThickness", out v) && v.TryGetInt32(out var it)) _settings.HighlightBorderThickness = it;
                            if (dict.TryGetValue("HighlightCornerRadius", out v) && v.TryGetInt32(out var ic)) _settings.HighlightCornerRadius = ic;
                            if (dict.TryGetValue("HighlightDurationMs", out v) && v.TryGetInt32(out var ih)) _settings.HighlightDurationMs = ih;
                            if (dict.TryGetValue("HudDurationMs", out v) && v.TryGetInt32(out var iu)) _settings.HudDurationMs = iu;
                            if (dict.TryGetValue("UseSystemColors", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) _settings.UseSystemColors = true; else if (dict.TryGetValue("UseSystemColors", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) _settings.UseSystemColors = false;
                            if (dict.TryGetValue("EnableHighlight", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) _settings.EnableHighlight = true; else if (dict.TryGetValue("EnableHighlight", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) _settings.EnableHighlight = false;
                            if (dict.TryGetValue("EnableConfirmations", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) _settings.EnableConfirmations = true; else if (dict.TryGetValue("EnableConfirmations", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) _settings.EnableConfirmations = false;
                            // hotkeys
                            if (dict.TryGetValue("HotkeyCommandPalette", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String) _settings.HotkeyCommandPalette = v.GetString() ?? _settings.HotkeyCommandPalette;
                            if (dict.TryGetValue("HotkeyEmergencyReset", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String) _settings.HotkeyEmergencyReset = v.GetString() ?? _settings.HotkeyEmergencyReset;
                            try { _persistence.SaveSettings(_settings); } catch { }
                            // Re-apply hotkeys so changes take effect immediately
                            try { _hotkeyManager.ApplyHotkeySettings(_settings); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void SetHudDuration(int ms)
        {
            try
            {
                _settings.HudDurationMs = ms;
                try { _persistence.SaveSettings(_settings); } catch { }

                // Update menu checked state if available
                try
                {
                    var menu = _notifyIcon.ContextMenuStrip;
                    if (menu != null)
                    {
                        foreach (ToolStripItem it in menu.Items)
                        {
                            if (it is ToolStripMenuItem m && m.Text == "HUD duration")
                            {
                                foreach (ToolStripItem sub in m.DropDownItems)
                                {
                                    if (sub is ToolStripMenuItem si && si.Tag is int tagMs)
                                    {
                                        si.Checked = tagMs == ms;
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
                catch { }

                // Apply to active HUD if present
                try { _activeHud?.ResetCloseTimer(ms); } catch { }
            }
            catch { }
        }

        private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
        {
            // Double-click opens the Settings window
            ShowSettings();
        }

        private void HotkeyManager_HotkeyRegistrationFailed(object? sender, HotkeyRegistrationFailedEventArgs e)
        {
            try
            {
                string title = "Hotkey conflict";
                string text = $"Hotkey {e.Modifiers}+{e.Key} could not be registered (error {e.ErrorCode}). It may be in use by another application. Change hotkeys in settings.";
                // Show balloon tip to notify user; non-blocking
                _notifyIcon.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
            }
            catch { }
        }

        private void ShowOnboarding()
        {
            var overlay = new WindowWorks.App.UI.OnboardingOverlay();
            overlay.ShowOverlay();
        }

        private void ApplyPreset(Models.Preset? p)
        {
            if (p == null) return;
            var cursorHwnd = _windowManager.GetWindowUnderCursor();
            IntPtr hwnd = WindowManager.Native.GetTopmostWindowUnderCursor();
            if (hwnd == IntPtr.Zero) hwnd = cursorHwnd != IntPtr.Zero ? cursorHwnd : _windowManager.GetForegroundWindowHandle();
            if (hwnd == IntPtr.Zero) return;

            try { _windowManager.ShowHighlight(hwnd); } catch { }
            _presetManager.ApplyPresetToWindow(p, hwnd, _windowManager);

            // Show HUD confirmation and extend highlight
            try
            {
                var label = _windowManager.GetWindowLabel(hwnd);
                var msg = string.IsNullOrWhiteSpace(label) ? "Preset applied" : $"Preset applied — {label}";
                ShowHud(msg, hwnd);
                _windowManager.ExtendHighlight(hwnd, _settings.HighlightDurationMs);
            }
            catch { }
        }

        private void HotkeyManager_OpacityNudgeRequested(object? sender, OpacityNudgeEventArgs e)
        {
            // Resolve the real target window (skip transparent/desktop/HUD windows)
            IntPtr resolved = WindowManager.Native.GetTopmostWindowUnderCursor();
            if (resolved == IntPtr.Zero) resolved = _windowManager.GetWindowUnderCursor();
            if (resolved == IntPtr.Zero) resolved = _windowManager.GetForegroundWindowHandle();

            // If the resolved window belongs to this process (likely our overlay/HUD), prefer the last target if available
            try
            {
                if (resolved != IntPtr.Zero && WindowManager.Native.GetWindowThreadProcessId(resolved, out uint rp) != 0 && rp == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    if (_lastTargetHwnd != IntPtr.Zero)
                    {
                        resolved = _lastTargetHwnd;
                    }
                }
            }
            catch { }

            IntPtr hwnd = resolved;
            if (hwnd == IntPtr.Zero) return;

            // Never operate on the desktop
            try
            {
                var desktop = WindowManager.Native.GetDesktopWindow();
                if (hwnd == desktop) return;
            }
            catch { }

            _lastTargetHwnd = hwnd;
            try { _windowManager.ShowHighlight(hwnd); } catch { }
            _windowManager.AdjustOpacity(hwnd, e.Delta, saveSnapshot: true);
            // Respect HUD display setting
            try
            {
                if (_settings.ShowHudOnOpacityChange)
                {
                    ShowHud("Opacity " + (e.Delta > 0 ? "+" : "") + e.Delta + "%", hwnd);
                }
            }
            catch { }
        }

        private void HotkeyManager_ToggleTopmostRequested(object? sender, EventArgs e)
        {
            IntPtr hwnd = WindowManager.Native.GetTopmostWindowUnderCursor();
            if (hwnd == IntPtr.Zero) hwnd = _windowManager.GetWindowUnderCursor();
            if (hwnd == IntPtr.Zero) hwnd = _windowManager.GetForegroundWindowHandle();
            if (hwnd == IntPtr.Zero) return;

            try
            {
                var desktop = WindowManager.Native.GetDesktopWindow();
                if (hwnd == desktop) return;
            }
            catch { }

            _lastTargetHwnd = hwnd;
            try { _windowManager.ShowHighlight(hwnd); } catch { }
            bool enabled = _windowManager.ToggleTopmost(hwnd, saveSnapshot: true);
            ShowHud(enabled ? "Always-on-top enabled" : "Always-on-top disabled", hwnd);
        }

        private void HotkeyManager_ToggleClickThroughRequested(object? sender, EventArgs e)
        {
            // Click-through feature has been removed; ignore the gesture.
        }

        private void ShowHud(string message, IntPtr? targetHwnd = null)
        {
            Action undo = () => {
                var restored = _auditLog.UndoLast(_windowManager);
                try
                {
                    if (restored != null && restored.Hwnd != IntPtr.Zero)
                    {
                        // Update HUD progress to reflect restored opacity
                        int val = restored.Opacity;
                        if (_activeHud != null) _activeHud.UpdateProgress(val);
                    }
                }
                catch { }
            };
            int progress = 100; 
            try
            {
                var hwnd = targetHwnd ?? _windowManager.GetForegroundWindowHandle();
                progress = _windowManager.GetOpacityPercent(hwnd);
            }
            catch { }

            if (_activeHud != null)
            {
                try
                {
                    _activeHud.OnUndo = undo;
                    _activeHud.UpdateMessage(message);
                    _activeHud.UpdateProgress(progress);
                    _activeHud.ResetCloseTimer();

                    var hwndPos = targetHwnd ?? _windowManager.GetForegroundWindowHandle();
                    if (hwndPos != IntPtr.Zero)
                    {
                        // Anchor to the top of the window content area
                        if (WindowManager.Native.GetClientRect(hwndPos, out var client))
                        {
                            var topLeft = new NativeMethods.POINT { X = client.Left, Y = client.Top };
                            NativeMethods.ClientToScreen(hwndPos, ref topLeft);
                            int x = topLeft.X + 10; // small left padding
                            int y = topLeft.Y + 20;  // increased gap from content top to avoid overlapping titlebar
                            var cursorPt = NativeMethods.GetCursorPoint();
                            _activeHud.ShowAt(x, y, hwndPos, cursorPt.X);
                            // Ensure HUD appears above newly topmost windows
                            try { _activeHud.EnsureTopmost(); } catch { }
                        }
                        else if (WindowManager.Native.GetWindowRect(hwndPos, out var rr))
                        {
                            int x = rr.Left + 20;
                            int y = rr.Top + 20;
                            var cursorPt = NativeMethods.GetCursorPoint();
                            _activeHud.ShowAt(x, y, hwndPos, cursorPt.X);
                            // Ensure HUD appears above newly topmost windows
                        }
                    }
                    _activeHud.ShowTransient(_settings.HudDurationMs);
                    // extend highlight duration to match HUD
                    try { _windowManager.ExtendHighlight(hwndPos, _settings.HighlightDurationMs); } catch { }
                    return;
                }
                catch
                {
                    // fall through and recreate
                }
            }
            _activeHud = new WindowWorks.App.UI.HudWindow();
            _activeHud.OnUndo = undo;
            // Reset should restore opacity to 100% for the current target window and record a snapshot
            IntPtr actionHwndNew = targetHwnd ?? (_lastTargetHwnd != IntPtr.Zero ? _lastTargetHwnd : _windowManager.GetForegroundWindowHandle());
            _activeHud.OnReset = () => {
                try
                {
                    if (actionHwndNew == IntPtr.Zero) return;
                    var snap = Models.WindowStateSnapshot.FromWindow(actionHwndNew);
                    _windowManager.ApplyOpacity(actionHwndNew, 100);
                    _auditLog.RecordSnapshot(snap);
                    _activeHud.UpdateProgress(100);
                    _activeHud.UpdateMessage("Opacity reset to 100%");
                }
                catch { }
            };
            _activeHud.Closed += (s, e) => { _activeHud = null; _lastTargetHwnd = IntPtr.Zero; };
            _activeHud.UpdateMessage(message);
            _activeHud.UpdateProgress(progress);

            try
            {
                var hwndPos2 = targetHwnd ?? _windowManager.GetForegroundWindowHandle();
                if (hwndPos2 != IntPtr.Zero && WindowManager.Native.GetWindowRect(hwndPos2, out var rr2))
                {
                    int x = rr2.Left + 20;
                    int y = rr2.Top + 20;
                    _activeHud.ShowAt(x, y, hwndPos2);
                    // extend highlight to match HUD duration
                    try { _windowManager.ExtendHighlight(hwndPos2, _settings.HighlightDurationMs); } catch { }
                }
                else
                {
                    _activeHud.ShowAt(100, 100);
                }
                _activeHud.ShowTransient(_settings.HudDurationMs);
            }
            catch
            {
                _activeHud.ShowTransient(_settings.HudDurationMs);
            }
        }

        public void Dispose()
        {
            _notifyIcon?.Dispose();
        }
    }
}
