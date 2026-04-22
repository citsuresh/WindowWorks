using System;
using System.Windows.Forms;

namespace WindowWorks.App
{
    static class Program
    {
        /// <summary>
        /// Application entry. Creates shared managers and runs a hidden ApplicationContext so
        /// the app is tray-only. All long-lived services are owned by AppContext and disposed on exit.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Create core services
            var persistence = new Persistence();
            var settings = persistence.LoadSettings();
            var auditLog = new AuditLog();
            var presetManager = new PresetManager(persistence, auditLog);
            var windowManager = new WindowManager(auditLog, settings);
            var hotkeyManager = new HotkeyManager(settings);

            // ClickThrough manager (Phase 1) - tracks modified windows and applies WS_EX_TRANSPARENT
            var clickThroughManager = new ClickThroughManager(settings, auditLog);

            var tray = new TrayController(hotkeyManager, windowManager, presetManager, auditLog, persistence, settings, clickThroughManager);

            var context = new TrayApplicationContext(tray, hotkeyManager, windowManager, presetManager, persistence, auditLog, settings, clickThroughManager);
            // Register UI services using a minimal local service collection (no external NuGet required)
            try
            {
                var svcColl = new WindowWorks.App.UI.Services.SimpleServiceCollection();
                svcColl.AddSingleton<WindowWorks.App.UI.Services.IDialogService, WindowWorks.App.UI.Services.DialogService>();
                var provider = svcColl.BuildServiceProvider();
                try
                {
                    var app = System.Windows.Application.Current as WindowWorks.App.UI.WpfApp;
                    if (app != null)
                    {
                        app.Services = provider;
                    }
                }
                catch { }
                // Also expose provider via AppServices static so UI components can resolve services
                try { WindowWorks.App.UI.AppServices.Provider = provider; } catch { }
                // Wire host-side HotkeyApplyService so UI can notify host to apply hotkeys in-process
                try
                {
                    var hotkeyApply = new HotkeyApplyService(persistence, hotkeyManager, settings);
                    WindowWorks.App.UI.AppServices.HotkeyApplyService = hotkeyApply;
                }
                catch { }
            }
            catch { }
            Application.Run(context);
        }
    }

    internal class TrayApplicationContext : ApplicationContext
    {
        private readonly TrayController _tray;
        private readonly HotkeyManager _hotkeyManager;
        private readonly WindowManager _windowManager;
        private readonly PresetManager _presetManager;
        private readonly Persistence _persistence;
        private readonly AuditLog _auditLog;
        private readonly Models.AppSettings _settings;
        private readonly ClickThroughManager _clickThroughManager;

        public TrayApplicationContext(TrayController tray, HotkeyManager hotkeyManager, WindowManager windowManager, PresetManager presetManager, Persistence persistence, AuditLog auditLog, Models.AppSettings settings, ClickThroughManager clickThroughManager)
        {
            _tray = tray;
            _hotkeyManager = hotkeyManager;
            _windowManager = windowManager;
            _presetManager = presetManager;
            _persistence = persistence;
            _auditLog = auditLog;
            _settings = settings;
            _clickThroughManager = clickThroughManager ?? throw new ArgumentNullException(nameof(clickThroughManager));

            // Start managers that require message loop or hooks
            _hotkeyManager.Start();
            _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;
            // Mouse gesture for click-through (Ctrl+Alt+Click)
            _hotkeyManager.ClickThroughGestureRequested += HotkeyManager_ClickThroughGestureRequested;

            tray.Initialize();
            tray.ExitRequested += Tray_ExitRequested;
        }

        private void HotkeyManager_HotkeyPressed(object? sender, HotkeyEventArgs e)
        {
            try
            {
                // Respect configured hotkeys from settings. If they match, act accordingly.
                if (!string.IsNullOrWhiteSpace(_settings.HotkeyCommandPalette) && HotkeyManager.ParseHotkeyString(_settings.HotkeyCommandPalette, out var cmods, out var ckey))
                {
                    if (e.Modifiers == cmods && e.Key == ckey)
                    {
                        var overlay = new UI.OnboardingOverlay();
                        overlay.ShowOverlay();
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(_settings.HotkeyEmergencyReset) && HotkeyManager.ParseHotkeyString(_settings.HotkeyEmergencyReset, out var rmods, out var rkey))
                {
                    if (e.Modifiers == rmods && e.Key == rkey)
                    {
                        // Ask confirmation before reset
                        try
                        {
                            var result = MessageBox.Show(
                                "Are you sure you want to reset all window changes and snapshots? This cannot be undone.",
                                "Confirm Reset All",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (result == DialogResult.Yes)
                            {
                                _auditLog.EmergencyReset(_windowManager);
                                // Ensure click-through state is also reset when performing an emergency reset
                                try { _clickThroughManager.ResetAllClickThrough(); } catch { }
                            }
                        }
                        catch { }
                        return;
                    }
                }
            }
            catch { }
        }

        private void Tray_ExitRequested(object? sender, EventArgs e)
        {
            ExitThread();
        }

        private void HotkeyManager_ClickThroughGestureRequested(object? sender, EventArgs e)
        {
            try
            {
                // When the gesture is triggered, determine window under cursor and enable click-through
                var wm = _windowManager;
                if (wm == null) return;
                IntPtr hwnd = wm.GetWindowUnderCursor();
                if (hwnd == IntPtr.Zero) return;

                bool applyTransparency = _settings.ClickThrough_Gesture_AutoTransparency;
                int tp = _settings.ClickThrough_Gesture_TransparencyPercent;
                try
                {
                    _clickThroughManager.EnableClickThrough(hwnd, applyTransparency, tp);
                    if (_settings.ClickThrough_Gesture_ShowNotification)
                    {
                        // Show a native tray/toast notification instead of a blocking MessageBox
                        try
                        {
                            _tray.ShowNotification("Click-Through", "Click-Through enabled for the selected window.", System.Windows.Forms.ToolTipIcon.Info, 4000);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _hotkeyManager.Dispose();
                _windowManager.Dispose();
                _presetManager.Dispose();
                _persistence.Dispose();
                _auditLog.Dispose();
                try { _clickThroughManager.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
