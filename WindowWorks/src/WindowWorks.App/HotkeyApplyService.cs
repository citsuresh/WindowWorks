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

        public WindowWorks.App.UI.Services.HotkeyApplyResult ApplyHotkeys(string? commandPalette, string? emergencyReset, string? transparencyIncrease, string? transparencyDecrease, string? toggleTopmost)
        {
            var result = new WindowWorks.App.UI.Services.HotkeyApplyResult();
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
                    // Attempt to apply hotkeys and capture per-key registration feedback via HotkeyRegistrationFailed event.
                    // Temporarily subscribe to capture failures for mapping back to the result object.
                    void OnFail(object? s, HotkeyRegistrationFailedEventArgs e)
                    {
                        try
                        {
                            var keyName = $"{e.Modifiers}+{e.Key}";
                            result.SetSuccess(keyName, false, $"Error {e.ErrorCode}");
                        }
                        catch { }
                    }

                    _hotkeyManager.HotkeyRegistrationFailed += OnFail;
                    _hotkeyManager.ApplyHotkeySettings(_settings);
                    // If no failure was recorded for a known key, mark it success.
                    // Known keys: HotkeyCommandPalette, HotkeyEmergencyReset
                    var kp = _settings.HotkeyCommandPalette ?? string.Empty;
                    var kr = _settings.HotkeyEmergencyReset ?? string.Empty;
                    if (!result.Success.ContainsKey(kp) && !string.IsNullOrWhiteSpace(kp)) result.SetSuccess(kp, true, null);
                    if (!result.Success.ContainsKey(kr) && !string.IsNullOrWhiteSpace(kr)) result.SetSuccess(kr, true, null);
                    _hotkeyManager.HotkeyRegistrationFailed -= OnFail;
                }
                catch { }

                // Debounced save
                DebouncedSave();
            }

            return result;
        }

        public WindowWorks.App.UI.Services.HotkeyApplyResult ApplyModifierSettings(bool? enableModifierMode, bool? autoTransparency, int? transparencyPercent, bool? showNotification)
        {
            var result = new WindowWorks.App.UI.Services.HotkeyApplyResult();
            bool changed = false;
            if (enableModifierMode.HasValue && enableModifierMode.Value != _settings.EnableClickThroughModifierMode)
            {
                _settings.EnableClickThroughModifierMode = enableModifierMode.Value;
                changed = true;
            }
            if (autoTransparency.HasValue && autoTransparency.Value != _settings.ClickThrough_Modifier_AutoTransparency)
            {
                _settings.ClickThrough_Modifier_AutoTransparency = autoTransparency.Value;
                changed = true;
            }
            if (transparencyPercent.HasValue && transparencyPercent.Value != _settings.ClickThrough_Modifier_TransparencyPercent)
            {
                _settings.ClickThrough_Modifier_TransparencyPercent = transparencyPercent.Value;
                changed = true;
            }
            if (showNotification.HasValue && showNotification.Value != _settings.ClickThrough_Modifier_ShowNotification)
            {
                _settings.ClickThrough_Modifier_ShowNotification = showNotification.Value;
                changed = true;
            }

            if (changed)
            {
                DebouncedSave();
            }

            return result;
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
