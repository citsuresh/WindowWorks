namespace WindowWorks.App.Models
{
    /// <summary>
    /// Application-level settings persisted to disk. Keep defaults here so callers
    /// may rely on reasonable behavior if no settings file exists.
    /// </summary>
    public class AppSettings
    {
        // HUD display duration in milliseconds
        // Default to 1 second (1000 ms)
        public int HudDurationMs { get; set; } = 1000;

        // Highlight overlay duration in milliseconds (when HUD is shown this will be extended to match)
        // Default to 1 second (1000 ms)
        public int HighlightDurationMs { get; set; } = 1000;

        // Highlight visual parameters
        // Border color in ARGB hex string (#AARRGGBB or #RRGGBB)
        public string HighlightBorderColor { get; set; } = "#CCFFFF00"; // semi-opaque yellow
        public int HighlightBorderThickness { get; set; } = 4;
        public int HighlightCornerRadius { get; set; } = 4;

        // Enable visual highlight and HUD confirmation messages
        public bool EnableHighlight { get; set; } = true;
        public bool EnableConfirmations { get; set; } = true;

        // Opacity nudge step sizes (percent)
        public int OpacityStepDefault { get; set; } = 5;
        public int OpacityStepFine { get; set; } = 1;

        // Mouse gesture toggles
        public bool EnableCtrlWheelOpacity { get; set; } = true;
        // Backwards-compatible flag name (legacy) and new flag for Ctrl+Shift topmost gesture.
        // Leave both present so older settings files continue to work; host will prefer EnableCtrlShiftTopmost.
        public bool EnableCtrlAltTopmost { get; set; } = true;
        public bool EnableCtrlShiftTopmost { get; set; } = true;
        // Click-through feature removed
        // public bool EnableCtrlShiftClickThrough { get; set; } = true;

        // Hotkey placeholders (stored but not wired here)
        public string HotkeyCommandPalette { get; set; } = "Win+`";
        public string HotkeyEmergencyReset { get; set; } = "Ctrl+Alt+R";

        // NOTE: transparency/topmost gestures are mouse-based by default; no keyboard shortcuts stored here.
        
        // Configurable mouse gestures stored as strings (e.g. "Ctrl+MouseWheel+Up", "Ctrl+Alt+Click")
        // These are configurable and persisted so users can customize gestures if desired.
        public string HotkeyOpacityNudge { get; set; } = "Ctrl+MouseWheel";
        public string HotkeyToggleTopmost { get; set; } = "Ctrl+Shift+Click";

        // Click-through feature settings (Gesture Mode)
        // Enable Gesture Mode (Ctrl+Alt+Click by default)
        public bool EnableClickThroughGestureMode { get; set; } = true;
        // Gesture combination string persisted (e.g. "Ctrl+Alt+Click")
        public string HotkeyClickThroughGesture { get; set; } = "Ctrl+Alt+Click";
        // Reset shortcut for click-through gesture (global shortcut)
        public string HotkeyClickThroughReset { get; set; } = "Ctrl+Alt+C";
        // When true, automatically set transparency when enabling gesture mode
        public bool ClickThrough_Gesture_AutoTransparency { get; set; } = false;
        // Transparency percent to apply when enabling gesture mode
        public int ClickThrough_Gesture_TransparencyPercent { get; set; } = 50;
        // Show notification when gesture mode is enabled
        public bool ClickThrough_Gesture_ShowNotification { get; set; } = true;

        // Modifier Mode settings - temporary click-through while modifier combo is held
        public bool EnableClickThroughModifierMode { get; set; } = false;
        public bool ClickThrough_Modifier_AutoTransparency { get; set; } = false;
        public int ClickThrough_Modifier_TransparencyPercent { get; set; } = 50;
        public bool ClickThrough_Modifier_ShowNotification { get; set; } = true;

        // HUD visual settings
        public int HudFontSize { get; set; } = 13;
        public string HudBackgroundColor { get; set; } = "#88000000";
        public int HudCornerRadius { get; set; } = 6;
        // HUD transparency (0-100 percent)
        public int HudTransparencyPercent { get; set; } = 50;
        // Which HUDs to show (bit flags stored as simple booleans for UI granularity)
        public bool ShowHudOnOpacityChange { get; set; } = true;
        public bool ShowHudOnTopmostToggle { get; set; } = true;
        public bool ShowHudOnPresetApplied { get; set; } = true;
        // HUD toggles for click-through features
        public bool ShowHudOnClickThroughGesture { get; set; } = true;
        public bool ShowHudOnClickThroughModifier { get; set; } = true;

        // Highlight pulse animation
        public bool HighlightPulse { get; set; } = true;
        // Pulse interval in milliseconds
        public int HighlightPulseIntervalMs { get; set; } = 900;

        // Maximum highlight duration cap in milliseconds. UI may allow user to control this.
        // Default to 1 second (1000 ms) hard cap to avoid very long overlays.
        public int HighlightMaxDurationMs { get; set; } = 1000;

        // Process whitelist/blacklist (process names without extension)
        public System.Collections.Generic.List<string> BlacklistProcesses { get; set; } = new();
        public System.Collections.Generic.List<string> WhitelistProcesses { get; set; } = new();
        // Previously supported 'Use system accent colors' option removed.
        // Application now always uses the configured HighlightBorderColor.
    }
}
