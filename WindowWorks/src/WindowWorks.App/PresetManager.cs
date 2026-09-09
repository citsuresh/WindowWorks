using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowWorks.App
{
    /// <summary>
    /// Loads presets and applies them to windows.
    /// Presets are simple named actions: e.g., Reading Mode (opacity 85, topmost false, click-through false)
    /// </summary>
    public class PresetManager : IDisposable
    {
        private readonly Persistence _persistence;
        private readonly AuditLog _auditLog;
        public List<Models.Preset> LoadedPresets { get; private set; } = new();

        public PresetManager(Persistence persistence, AuditLog auditLog)
        {
            _persistence = persistence;
            _auditLog = auditLog;
            LoadDefaults();
        }

        private void LoadDefaults()
        {
            try
            {
                // Prefer user-saved presets from %APPDATA%\WindowWorks\presets.json
                var saved = _persistence.LoadPresetsFromAppFolder();
                if (saved != null && saved.Any())
                {
                    LoadedPresets = saved.ToList();
                    return;
                }

                // Fall back to embedded seed defaults on first run / missing file
                var defaults = _persistence.LoadEmbeddedDefaultPresets();
                if (defaults != null && defaults.Any())
                {
                    LoadedPresets = defaults.ToList();
                    // Persist the seeded defaults so presets.json exists going forward
                    try { _persistence.SavePresets(LoadedPresets); } catch { }
                }
            }
            catch { /* swallow for now; persistence will log in future */ }
        }

        public void ApplyPresetToWindow(Models.Preset preset, IntPtr hwnd, WindowManager windowManager)
        {
            if (preset == null || hwnd == IntPtr.Zero) return;
            // Save snapshot before applying to allow undo
            var snap = Models.WindowStateSnapshot.FromWindow(hwnd);
            windowManager.ApplyOpacity(hwnd, preset.Opacity);
            windowManager.SetTopmost(hwnd, preset.Topmost);
            // Record snapshot for undo
            _auditLog.RecordSnapshot(snap);
        }

        public void Dispose()
        {
            // placeholder
        }
    }
}
