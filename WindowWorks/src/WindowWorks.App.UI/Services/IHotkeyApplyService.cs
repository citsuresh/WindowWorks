using System;

namespace WindowWorks.App.UI.Services
{
    // In-process service UI calls to request host to apply hotkey/gesture changes.
    public interface IHotkeyApplyService
    {
        // Notify host that hotkeys/gestures changed. Parameters nullable; host ignores nulls.
        // Returns a HotkeyApplyResult containing per-shortcut success/failure information.
        HotkeyApplyResult ApplyHotkeys(string? commandPalette, string? emergencyReset, string? transparencyIncrease, string? transparencyDecrease, string? toggleTopmost);

        // Apply modifier mode settings immediately. Parameters nullable; host ignores nulls.
        HotkeyApplyResult ApplyModifierSettings(bool? enableModifierMode, bool? autoTransparency, int? transparencyPercent, bool? showNotification);
    }
}
