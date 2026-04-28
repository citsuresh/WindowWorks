using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    public class HighlightSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private string? _borderColor; // stored as #RRGGBB or #AARRGGBB
        private int _borderTransparencyPercent;
        private int _borderThickness;
        private int _cornerRadius;
        private int _highlightDurationMs;
        private int _hudDurationMs;

        public string? BorderColor { get => _borderColor; set { if (value == _borderColor) return; _borderColor = value; OnPropertyChanged(); } }
        public int BorderTransparencyPercent { get => _borderTransparencyPercent; set { if (value == _borderTransparencyPercent) return; _borderTransparencyPercent = value; OnPropertyChanged(); } }
        public int BorderThickness { get => _borderThickness; set { if (value == _borderThickness) return; _borderThickness = value; OnPropertyChanged(); } }
        public int CornerRadius { get => _cornerRadius; set { if (value == _cornerRadius) return; _cornerRadius = value; OnPropertyChanged(); } }
        public int HighlightDurationMs { get => _highlightDurationMs; set { if (value == _highlightDurationMs) return; _highlightDurationMs = value; OnPropertyChanged(); } }
        public int HudDurationMs { get => _hudDurationMs; set { if (value == _hudDurationMs) return; _hudDurationMs = value; OnPropertyChanged(); } }

        // Commands (UI actions) - wired by view when DialogService is available
        public System.Windows.Input.ICommand? ChooseColorCommand { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("HighlightBorderColor", out var v) && v.ValueKind == JsonValueKind.String) BorderColor = v.GetString();
                if (d.TryGetValue("HighlightBorderTransparencyPercent", out v) && v.TryGetInt32(out var tp)) BorderTransparencyPercent = tp;
                if (d.TryGetValue("HighlightBorderThickness", out v) && v.TryGetInt32(out var bt)) BorderThickness = bt;
                if (d.TryGetValue("HighlightCornerRadius", out v) && v.TryGetInt32(out var cr)) CornerRadius = cr;
                if (d.TryGetValue("HighlightDurationMs", out v) && v.TryGetInt32(out var hm)) HighlightDurationMs = hm;
                if (d.TryGetValue("HudDurationMs", out v) && v.TryGetInt32(out var um)) HudDurationMs = um;
                // If explicit transparency percent not provided but color contains alpha (#AARRGGBB), derive percent
                try
                {
                    if ((BorderTransparencyPercent == 0) && !string.IsNullOrWhiteSpace(BorderColor))
                    {
                        var s = BorderColor.Trim();
                        if (s.StartsWith("#") && s.Length == 9)
                        {
                            var aHex = s.Substring(1, 2);
                            if (byte.TryParse(aHex, System.Globalization.NumberStyles.HexNumber, null, out var a))
                            {
                                BorderTransparencyPercent = (int)Math.Round(100.0 - (a / 255.0 * 100.0));
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
            if (!string.IsNullOrWhiteSpace(BorderColor)) d["HighlightBorderColor"] = BorderColor;
            d["HighlightBorderTransparencyPercent"] = BorderTransparencyPercent;
            d["HighlightBorderThickness"] = BorderThickness;
            // Use legacy key name expected by other code: HighlightCornerRadius
            d["HighlightCornerRadius"] = CornerRadius;
            d["HighlightDurationMs"] = HighlightDurationMs;
            d["HudDurationMs"] = HudDurationMs;
            return d;
        }
    }
}
