using System;
using System.Threading;
using System.Threading.Tasks;

namespace WindowWorks.App
{
    // Host-side service invoked by UI to apply hotkey/gesture changes in-process.
    internal class HotkeyApplyService : WindowWorks.App.UI.Services.IHotkeyApplyService, IDisposable
    {
        private readonly Persistence _persistence;
        private readonly HotkeyManager _hotkeyManager;
        private readonly Models.AppSettings _settings;

        // Debounce timer for saving settings to disk (apply immediately, save after quiet period)
        private readonly TimeSpan _saveDebounce = TimeSpan.FromMilliseconds(400);
        private CancellationTokenSource? _saveCts;

        public HotkeyApplyService(Persistence persistence, HotkeyManager hotkeyManager, Models.AppSettings settings)
        {
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _hotkeyManager = hotkeyManager ?? throw new ArgumentNullException(nameof(hotkeyManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void ApplyHotkeys(string? commandPalette, string? emergencyReset, string? transparencyIncrease, string? transparencyDecrease, string? toggleTopmost)
        {
            bool changed = false;
            if (!string.IsNullOrWhiteSpace(commandPalette) && !string.Equals(commandPalette, _settings.HotkeyCommandPalette, StringComparison.Ordinal))
            {
                _settings.HotkeyCommandPalette = commandPalette;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(emergencyReset) && !string.Equals(emergencyReset, _settings.HotkeyEmergencyReset, StringComparison.Ordinal))
            {
                _settings.HotkeyEmergencyReset = emergencyReset;
                changed = true;
            }
            // Map transparency params to the existing HotkeyOpacityNudge setting if provided.
            if (!string.IsNullOrWhiteSpace(transparencyIncrease) && !string.Equals(transparencyIncrease, _settings.HotkeyOpacityNudge, StringComparison.Ordinal))
            {
                _settings.HotkeyOpacityNudge = transparencyIncrease;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(transparencyDecrease) && !string.Equals(transparencyDecrease, _settings.HotkeyOpacityNudge, StringComparison.Ordinal))
            {
                _settings.HotkeyOpacityNudge = transparencyDecrease;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(toggleTopmost) && !string.Equals(toggleTopmost, _settings.HotkeyToggleTopmost, StringComparison.Ordinal))
            {
                _settings.HotkeyToggleTopmost = toggleTopmost;
                changed = true;
            }

            // Apply hotkeys immediately if keyboard hotkeys changed
            if (changed)
            {
                try
                {
                    _hotkeyManager.ApplyHotkeySettings(_settings);
                }
                catch { }

                // Debounced save
                DebouncedSave();
            }
        }

        private void DebouncedSave()
        {
            try
            {
                _saveCts?.Cancel();
                _saveCts?.Dispose();
                _saveCts = new CancellationTokenSource();
                var ct = _saveCts.Token;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(_saveDebounce, ct).ConfigureAwait(false);
                        if (!ct.IsCancellationRequested)
                        {
                            _persistence.SaveSettings(_settings);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, CancellationToken.None);
            }
            catch { }
        }

        public void Dispose()
        {
            try { _saveCts?.Cancel(); _saveCts?.Dispose(); } catch { }
        }
    }
}
