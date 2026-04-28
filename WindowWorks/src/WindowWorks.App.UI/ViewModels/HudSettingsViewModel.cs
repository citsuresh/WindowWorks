using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    public class HudSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private string? _backgroundColor;
        private int _transparencyPercent;
        private int _fontSize;
        private int _cornerRadius;
        private int _durationMs;
        private bool _showOnOpacityChange;
        private bool _showOnTopmostToggle;
        private bool _showOnPresetApplied;
        private bool _showOnClickThroughGesture;
        private bool _showOnClickThroughModifier;

        public string? BackgroundColor { get => _backgroundColor; set { if (value == _backgroundColor) return; _backgroundColor = value; OnPropertyChanged(); } }
        public int TransparencyPercent { get => _transparencyPercent; set { if (value == _transparencyPercent) return; _transparencyPercent = value; OnPropertyChanged(); } }
        public int FontSize { get => _fontSize; set { if (value == _fontSize) return; _fontSize = value; OnPropertyChanged(); } }
        public int CornerRadius { get => _cornerRadius; set { if (value == _cornerRadius) return; _cornerRadius = value; OnPropertyChanged(); } }
        public int DurationMs { get => _durationMs; set { if (value == _durationMs) return; _durationMs = value; OnPropertyChanged(); } }
        public bool ShowOnOpacityChange { get => _showOnOpacityChange; set { if (value == _showOnOpacityChange) return; _showOnOpacityChange = value; OnPropertyChanged(); } }
        public bool ShowOnTopmostToggle { get => _showOnTopmostToggle; set { if (value == _showOnTopmostToggle) return; _showOnTopmostToggle = value; OnPropertyChanged(); } }
        public bool ShowOnPresetApplied { get => _showOnPresetApplied; set { if (value == _showOnPresetApplied) return; _showOnPresetApplied = value; OnPropertyChanged(); } }
        public bool ShowOnClickThroughGesture { get => _showOnClickThroughGesture; set { if (value == _showOnClickThroughGesture) return; _showOnClickThroughGesture = value; OnPropertyChanged(); } }
        public bool ShowOnClickThroughModifier { get => _showOnClickThroughModifier; set { if (value == _showOnClickThroughModifier) return; _showOnClickThroughModifier = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("HudBackgroundColor", out var v) && v.ValueKind == JsonValueKind.String) BackgroundColor = v.GetString();
                if (d.TryGetValue("HudTransparencyPercent", out v) && v.TryGetInt32(out var tp)) TransparencyPercent = tp;
                if (d.TryGetValue("HudFontSize", out v) && v.TryGetInt32(out var fs)) FontSize = fs;
                if (d.TryGetValue("HudCornerRadius", out v) && v.TryGetInt32(out var cr)) CornerRadius = cr;
                if (d.TryGetValue("HudDurationMs", out v) && v.TryGetInt32(out var dm)) DurationMs = dm;
                if (d.TryGetValue("ShowHudOnOpacityChange", out v) && v.ValueKind == JsonValueKind.True) ShowOnOpacityChange = true; else if (d.TryGetValue("ShowHudOnOpacityChange", out v) && v.ValueKind == JsonValueKind.False) ShowOnOpacityChange = false;
                if (d.TryGetValue("ShowHudOnTopmostToggle", out v) && v.ValueKind == JsonValueKind.True) ShowOnTopmostToggle = true; else if (d.TryGetValue("ShowHudOnTopmostToggle", out v) && v.ValueKind == JsonValueKind.False) ShowOnTopmostToggle = false;
                if (d.TryGetValue("ShowHudOnPresetApplied", out v) && v.ValueKind == JsonValueKind.True) ShowOnPresetApplied = true; else if (d.TryGetValue("ShowHudOnPresetApplied", out v) && v.ValueKind == JsonValueKind.False) ShowOnPresetApplied = false;
                if (d.TryGetValue("ShowHudOnClickThroughGesture", out v) && v.ValueKind == JsonValueKind.True) ShowOnClickThroughGesture = true; else if (d.TryGetValue("ShowHudOnClickThroughGesture", out v) && v.ValueKind == JsonValueKind.False) ShowOnClickThroughGesture = false;
                if (d.TryGetValue("ShowHudOnClickThroughModifier", out v) && v.ValueKind == JsonValueKind.True) ShowOnClickThroughModifier = true; else if (d.TryGetValue("ShowHudOnClickThroughModifier", out v) && v.ValueKind == JsonValueKind.False) ShowOnClickThroughModifier = false;

                // If explicit transparency percent not present but color contains alpha (#AARRGGBB), derive it
                try
                {
                    if ((TransparencyPercent == 0) && !string.IsNullOrWhiteSpace(BackgroundColor))
                    {
                        var s = BackgroundColor.Trim();
                        if (s.StartsWith("#") && s.Length == 9)
                        {
                            var aHex = s.Substring(1, 2);
                            if (byte.TryParse(aHex, System.Globalization.NumberStyles.HexNumber, null, out var a))
                            {
                                TransparencyPercent = (int)Math.Round(100.0 - (a / 255.0 * 100.0));
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        public Dictionary<string, object?> ToDictionary()
        {
            var d = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(BackgroundColor)) d["HudBackgroundColor"] = BackgroundColor;
            d["HudTransparencyPercent"] = TransparencyPercent;
            d["HudFontSize"] = FontSize;
            d["HudCornerRadius"] = CornerRadius;
            d["HudDurationMs"] = DurationMs;
            d["ShowHudOnOpacityChange"] = ShowOnOpacityChange;
            d["ShowHudOnTopmostToggle"] = ShowOnTopmostToggle;
            d["ShowHudOnPresetApplied"] = ShowOnPresetApplied;
            d["ShowHudOnClickThroughGesture"] = ShowOnClickThroughGesture;
            d["ShowHudOnClickThroughModifier"] = ShowOnClickThroughModifier;
            return d;
        }
    }
}
