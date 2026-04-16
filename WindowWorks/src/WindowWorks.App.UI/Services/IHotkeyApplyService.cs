using System;

namespace WindowWorks.App.UI.Services
{
    // In-process service UI calls to request host to apply hotkey/gesture changes.
    public interface IHotkeyApplyService
    {
        // Notify host that hotkeys/gestures changed. Parameters nullable; host ignores nulls.
        void ApplyHotkeys(string? commandPalette, string? emergencyReset, string? transparencyIncrease, string? transparencyDecrease, string? toggleTopmost);
    }
}
