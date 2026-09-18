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

            // ClickThrough manager - tracks modified windows and applies WS_EX_TRANSPARENT
            var clickThroughManager = new ClickThroughManager(settings, auditLog);

            // Reparent controller - created here (not inside TrayApplicationContext) so the tray
            // menu's "Reset Reparenting" entry (docs/REPARENT_FEATURE_PLAN.md §8 step 11) can
            // share the same instance/tracking list as the hotkey-driven picker flow.
            var reparentController = new ReparentController(settings);
            var propertyInspectorController = new PropertyInspectorController();

            // Crash recovery (§14 Phase 1 item 10): run once at startup, before any new picker/
            // hotkey activity, so any windows left orphaned by a previous crash (before its
            // normal restore path ran) are recovered before the user can start reparenting again.
            try { reparentController.RunCrashRecoveryPass(); } catch { }

            var tray = new TrayController(hotkeyManager, windowManager, presetManager, auditLog, persistence, settings, clickThroughManager, reparentController);

            var context = new TrayApplicationContext(tray, hotkeyManager, windowManager, presetManager, persistence, auditLog, settings, clickThroughManager, reparentController, propertyInspectorController);
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
        private readonly ReparentController _reparentController;
        private readonly PropertyInspectorController? _propertyInspectorController;

        public TrayApplicationContext(TrayController tray, HotkeyManager hotkeyManager, WindowManager windowManager, PresetManager presetManager, Persistence persistence, AuditLog auditLog, Models.AppSettings settings, ClickThroughManager clickThroughManager, ReparentController reparentController, PropertyInspectorController? propertyInspectorController = null)
        {
            _tray = tray;
            _hotkeyManager = hotkeyManager;
            _windowManager = windowManager;
            _presetManager = presetManager;
            _persistence = persistence;
            _auditLog = auditLog;
            _settings = settings;
            _clickThroughManager = clickThroughManager ?? throw new ArgumentNullException(nameof(clickThroughManager));
            _reparentController = reparentController ?? throw new ArgumentNullException(nameof(reparentController));
            _propertyInspectorController = propertyInspectorController;

            // Start managers that require message loop or hooks
            _hotkeyManager.Start();
            _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;
            // Mouse gesture for click-through (Ctrl+Alt+Click)
            _hotkeyManager.ClickThroughGestureRequested += HotkeyManager_ClickThroughGestureRequested;

            tray.Initialize();
            tray.ExitRequested += Tray_ExitRequested;
            tray.InspectUiElementRequested += Tray_InspectUiElementRequested;
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

                if (!string.IsNullOrWhiteSpace(_settings.HotkeyWindowReparent) && HotkeyManager.ParseHotkeyString(_settings.HotkeyWindowReparent, out var pmods, out var pkey))
                {
                    if (e.Modifiers == pmods && e.Key == pkey)
                    {
                        try { _reparentController.InvokePicker(); } catch { }
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(_settings.HotkeyPropertyInspector) && HotkeyManager.ParseHotkeyString(_settings.HotkeyPropertyInspector, out var imods, out var ikey))
                {
                    if (e.Modifiers == imods && e.Key == ikey)
                    {
                        try { _propertyInspectorController?.InvokePicker(); } catch { }
                    }
                }
            }
            catch { }
        }

        private void Tray_InspectUiElementRequested(object? sender, EventArgs e)
        {
            try { _propertyInspectorController?.InvokePicker(); } catch { }
        }

        private void Tray_ExitRequested(object? sender, EventArgs e)
        {
            if (!_reparentController.RestoreAll())
            {
                MessageBox.Show(
                    "WindowWorks could not safely restore every reparented window. The app will remain open so you can retry restoring it.",
                    "Window Reparenting Restore Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

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
                            _tray.ShowNotification("Click-Through", "Click-Through enabled for the selected window. Select the \"Reset Click-Through\" Tray menu option to disable click-through", System.Windows.Forms.ToolTipIcon.Info, 4000);
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
                // Graceful-shutdown restore (docs/REPARENT_FEATURE_PLAN.md §14 Phase 1 item 9):
                // restore every currently-reparented window back to its original state before the
                // app exits normally, so a normal exit never leaves stray reparented windows behind
                // (crash recovery via a state file is a separate, not-yet-implemented mechanism —
                // this only covers the graceful/non-crash exit path).
                // Do not tear down the process-owned sockets while a target may still be attached
                // to one. Tray_ExitRequested normally prevents reaching disposal in that state;
                // retain the controller if disposal is invoked through another route.
                if (!_reparentController.RestoreAll())
                {
                    return;
                }
                _tray.Dispose();
                _hotkeyManager.Dispose();
                _windowManager.Dispose();
                _presetManager.Dispose();
                _persistence.Dispose();
                _auditLog.Dispose();
                try { _clickThroughManager.Dispose(); } catch { }
                try { _reparentController.Dispose(); } catch { }
                try { _propertyInspectorController?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
