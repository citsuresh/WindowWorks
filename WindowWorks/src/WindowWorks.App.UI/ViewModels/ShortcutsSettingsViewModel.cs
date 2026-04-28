using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    public class ShortcutsSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private string? _commandPalette;
        private string? _emergencyReset;
        private string? _opacityGesture;
        private string? _toggleGesture;

        public string? CommandPalette { get => _commandPalette; set { if (value == _commandPalette) return; _commandPalette = value; OnPropertyChanged(); } }
        public string? EmergencyReset { get => _emergencyReset; set { if (value == _emergencyReset) return; _emergencyReset = value; OnPropertyChanged(); } }
        public string? OpacityGesture { get => _opacityGesture; set { if (value == _opacityGesture) return; _opacityGesture = value; OnPropertyChanged(); } }
        public string? ToggleGesture { get => _toggleGesture; set { if (value == _toggleGesture) return; _toggleGesture = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("HotkeyCommandPalette", out var v) && v.ValueKind == JsonValueKind.String) CommandPalette = v.GetString();
                if (d.TryGetValue("HotkeyEmergencyReset", out v) && v.ValueKind == JsonValueKind.String) EmergencyReset = v.GetString();
                if (d.TryGetValue("HotkeyOpacityGesture", out v) && v.ValueKind == JsonValueKind.String) OpacityGesture = v.GetString();
                if (d.TryGetValue("HotkeyToggleGesture", out v) && v.ValueKind == JsonValueKind.String) ToggleGesture = v.GetString();
            }
            catch { }
        }

        public Dictionary<string, object?> ToDictionary()
        {
            var d = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(CommandPalette)) d["HotkeyCommandPalette"] = CommandPalette;
            if (!string.IsNullOrWhiteSpace(EmergencyReset)) d["HotkeyEmergencyReset"] = EmergencyReset;
            if (!string.IsNullOrWhiteSpace(OpacityGesture)) d["HotkeyOpacityGesture"] = OpacityGesture;
            if (!string.IsNullOrWhiteSpace(ToggleGesture)) d["HotkeyToggleGesture"] = ToggleGesture;
            return d;
        }
    }
}
