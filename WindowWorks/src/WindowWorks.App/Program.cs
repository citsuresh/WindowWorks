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

            var tray = new TrayController(hotkeyManager, windowManager, presetManager, auditLog, persistence, settings);

            var context = new TrayApplicationContext(tray, hotkeyManager, windowManager, presetManager, persistence, auditLog);
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
        public TrayApplicationContext(TrayController tray, HotkeyManager hotkeyManager, WindowManager windowManager, PresetManager presetManager, Persistence persistence, AuditLog auditLog)
        {
            _tray = tray;
            _hotkeyManager = hotkeyManager;
            _windowManager = windowManager;
            _presetManager = presetManager;
            _persistence = persistence;
            _auditLog = auditLog;

            // Start managers that require message loop or hooks
            _hotkeyManager.Start();
            _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;

            tray.Initialize();
            tray.ExitRequested += Tray_ExitRequested;
        }

        private void HotkeyManager_HotkeyPressed(object? sender, HotkeyEventArgs e)
        {
            // Basic handling for Win+` to open command palette placeholder
            if (e.Modifiers == HotkeyModifiers.Win && e.Key == Keys.Oem3) // Oem3 is the ` key
            {
                // TODO: Show command palette UI. For now show onboarding overlay quickly.
                var overlay = new UI.OnboardingOverlay();
                overlay.ShowOverlay();
            }
            else if (e.Modifiers == (HotkeyModifiers.Win | HotkeyModifiers.Shift) && e.Key == Keys.R)
            {
                // Emergency reset
                _auditLog.EmergencyReset(_windowManager);
            }
            // Note: click-through feature removed; Win+Shift+C disabled.
        }

        private void Tray_ExitRequested(object? sender, EventArgs e)
        {
            ExitThread();
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
            }
            base.Dispose(disposing);
        }
    }
}
