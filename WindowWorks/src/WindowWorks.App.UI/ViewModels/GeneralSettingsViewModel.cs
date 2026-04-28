using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    public class GeneralSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private bool _enableHighlight = true;
        private bool _enableConfirmations = true;

        public bool EnableHighlight { get => _enableHighlight; set { if (value == _enableHighlight) return; _enableHighlight = value; OnPropertyChanged(); } }
        public bool EnableConfirmations { get => _enableConfirmations; set { if (value == _enableConfirmations) return; _enableConfirmations = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("EnableHighlight", out var v) && v.ValueKind == JsonValueKind.True) EnableHighlight = true; else if (d.TryGetValue("EnableHighlight", out v) && v.ValueKind == JsonValueKind.False) EnableHighlight = false;
                if (d.TryGetValue("EnableConfirmations", out v) && v.ValueKind == JsonValueKind.True) EnableConfirmations = true; else if (d.TryGetValue("EnableConfirmations", out v) && v.ValueKind == JsonValueKind.False) EnableConfirmations = false;
            }
            catch { }
        }

        public Dictionary<string, object?> ToDictionary()
        {
            var d = new Dictionary<string, object?>();
            d["EnableHighlight"] = EnableHighlight;
            d["EnableConfirmations"] = EnableConfirmations;
            return d;
        }
    }
}
