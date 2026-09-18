using System;
using System.Collections.Generic;
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

        public WindowWorks.App.UI.Services.HotkeyApplyResult ApplyHotkeys(string? commandPalette, string? emergencyReset, string? transparencyIncrease, string? transparencyDecrease, string? toggleTopmost, string? windowReparent = null, string? propertyInspector = null)
        {
            var result = new WindowWorks.App.UI.Services.HotkeyApplyResult();
            bool changed = false;
            string previousCommandPalette = _settings.HotkeyCommandPalette ?? string.Empty;
            string previousEmergencyReset = _settings.HotkeyEmergencyReset ?? string.Empty;
            string previousWindowReparent = _settings.HotkeyWindowReparent ?? string.Empty;
            string previousPropertyInspector = _settings.HotkeyPropertyInspector ?? string.Empty;
            bool commandPaletteChanged = false;
            bool emergencyResetChanged = false;
            bool windowReparentChanged = false;
            bool propertyInspectorChanged = false;
            if (!string.IsNullOrWhiteSpace(commandPalette) && !string.Equals(commandPalette, _settings.HotkeyCommandPalette, StringComparison.Ordinal))
            {
                _settings.HotkeyCommandPalette = commandPalette;
                changed = true;
                commandPaletteChanged = true;
            }
            if (!string.IsNullOrWhiteSpace(emergencyReset) && !string.Equals(emergencyReset, _settings.HotkeyEmergencyReset, StringComparison.Ordinal))
            {
                _settings.HotkeyEmergencyReset = emergencyReset;
                changed = true;
                emergencyResetChanged = true;
            }
            if (!string.IsNullOrWhiteSpace(windowReparent) && !string.Equals(windowReparent, _settings.HotkeyWindowReparent, StringComparison.Ordinal))
            {
                _settings.HotkeyWindowReparent = windowReparent;
                changed = true;
                windowReparentChanged = true;
            }
            if (!string.IsNullOrWhiteSpace(propertyInspector) && !string.Equals(propertyInspector, _settings.HotkeyPropertyInspector, StringComparison.Ordinal))
            {
                _settings.HotkeyPropertyInspector = propertyInspector;
                changed = true;
                propertyInspectorChanged = true;
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
                    var outcomes = _hotkeyManager.ApplyHotkeySettingsWithOutcomes(_settings);
                    ApplyOutcome(0, commandPalette, previousCommandPalette, commandPaletteChanged, outcomes, result);
                    ApplyOutcome(1, emergencyReset, previousEmergencyReset, emergencyResetChanged, outcomes, result);
                    ApplyOutcome(2, windowReparent, previousWindowReparent, windowReparentChanged, outcomes, result);
                    ApplyOutcome(3, propertyInspector, previousPropertyInspector, propertyInspectorChanged, outcomes, result);
                }
                catch
                {
                    RestoreFailedSetting(commandPaletteChanged, previousCommandPalette, value => _settings.HotkeyCommandPalette = value);
                    RestoreFailedSetting(emergencyResetChanged, previousEmergencyReset, value => _settings.HotkeyEmergencyReset = value);
                    RestoreFailedSetting(windowReparentChanged, previousWindowReparent, value => _settings.HotkeyWindowReparent = value);
                    RestoreFailedSetting(propertyInspectorChanged, previousPropertyInspector, value => _settings.HotkeyPropertyInspector = value);
                    SetApplyFailure(result, 0, commandPaletteChanged, commandPalette);
                    SetApplyFailure(result, 1, emergencyResetChanged, emergencyReset);
                    SetApplyFailure(result, 2, windowReparentChanged, windowReparent);
                    SetApplyFailure(result, 3, propertyInspectorChanged, propertyInspector);
                }

                // Debounced save
                DebouncedSave();
            }

            return result;
        }

        private void ApplyOutcome(
            int id,
            string? requestedShortcut,
            string previousShortcut,
            bool wasChanged,
            IReadOnlyDictionary<int, HotkeyRegistrationOutcome> outcomes,
            WindowWorks.App.UI.Services.HotkeyApplyResult result)
        {
            if (!outcomes.TryGetValue(id, out var outcome))
            {
                SetApplyFailure(result, id, wasChanged, requestedShortcut);
                return;
            }

            string keyName = wasChanged ? requestedShortcut ?? outcome.ConfiguredShortcut : outcome.ConfiguredShortcut;
            string? message = outcome.Succeeded
                ? null
                : outcome.PreviousRegistrationRestored
                    ? $"Error {outcome.ErrorCode}; the previous shortcut was restored."
                    : $"Error {outcome.ErrorCode}; the shortcut was not registered.";
            result.SetSuccess(keyName, outcome.Succeeded, message);
            result.SetBindingSuccess(id, keyName, outcome.Succeeded, message);

            if (!outcome.Succeeded && wasChanged)
            {
                switch (id)
                {
                    case 0:
                        _settings.HotkeyCommandPalette = previousShortcut;
                        break;
                    case 1:
                        _settings.HotkeyEmergencyReset = previousShortcut;
                        break;
                    case 2:
                        _settings.HotkeyWindowReparent = previousShortcut;
                        break;
                    case 3:
                        _settings.HotkeyPropertyInspector = previousShortcut;
                        break;
                }
            }
        }

        private static void RestoreFailedSetting(bool wasChanged, string previousValue, Action<string> restore)
        {
            if (wasChanged)
            {
                restore(previousValue);
            }
        }

        private static void SetApplyFailure(
            WindowWorks.App.UI.Services.HotkeyApplyResult result,
            int id,
            bool wasChanged,
            string? requestedShortcut)
        {
            if (wasChanged && !string.IsNullOrWhiteSpace(requestedShortcut))
            {
                result.SetSuccess(requestedShortcut, false, "The shortcut could not be registered.");
                result.SetBindingSuccess(id, requestedShortcut, false, "The shortcut could not be registered.");
            }
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
