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
        private string? _windowReparent;
        private string? _propertyInspector = "Ctrl+Alt+I";
        private string? _opacityGesture;
        private string? _toggleGesture;
        // Exposed for binding to ShortcutPicker.Shortcut
        public string? CommandPalette { get => _commandPalette; set { if (value == _commandPalette) return; _commandPalette = value; OnPropertyChanged(); } }
        public string? EmergencyReset { get => _emergencyReset; set { if (value == _emergencyReset) return; _emergencyReset = value; OnPropertyChanged(); } }
        public string? WindowReparent { get => _windowReparent; set { if (value == _windowReparent) return; _windowReparent = value; OnPropertyChanged(); } }
        public string? PropertyInspector { get => _propertyInspector; set { if (value == _propertyInspector) return; _propertyInspector = value; OnPropertyChanged(); } }

        // Legacy gesture properties remain
        public string? OpacityGesture { get => _opacityGesture; set { if (value == _opacityGesture) return; _opacityGesture = value; OnPropertyChanged(); } }
        public string? ToggleGesture { get => _toggleGesture; set { if (value == _toggleGesture) return; _toggleGesture = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;

        // Commands to edit shortcuts via the dialog service
        public System.Windows.Input.ICommand EditCommandPalette { get; }
        public System.Windows.Input.ICommand EditEmergencyReset { get; }
        public System.Windows.Input.ICommand EditWindowReparent { get; }
        public System.Windows.Input.ICommand EditPropertyInspector { get; }

        private readonly Services.IDialogService? _dialogService;

        public ShortcutsSettingsViewModel() : this(WindowWorks.App.UI.AppServices.GetService<Services.IDialogService>()) { }

        public ShortcutsSettingsViewModel(Services.IDialogService? dialogService)
        {
            _dialogService = dialogService;
            EditCommandPalette = new RelayCommand(_ => ExecuteEditCommandPalette());
            EditEmergencyReset = new RelayCommand(_ => ExecuteEditEmergencyReset());
            EditWindowReparent = new RelayCommand(_ => ExecuteEditWindowReparent());
            EditPropertyInspector = new RelayCommand(_ => ExecuteEditPropertyInspector());
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void ExecuteEditCommandPalette()
        {
            try
            {
                var result = _dialogService?.ShowShortcutCapture(CommandPalette);
                if (!string.IsNullOrWhiteSpace(result)) CommandPalette = result;
            }
            catch { }
        }

        private void ExecuteEditEmergencyReset()
        {
            try
            {
                var result = _dialogService?.ShowShortcutCapture(EmergencyReset);
                if (!string.IsNullOrWhiteSpace(result)) EmergencyReset = result;
            }
            catch { }
        }

        private void ExecuteEditWindowReparent()
        {
            try
            {
                var result = _dialogService?.ShowShortcutCapture(WindowReparent);
                if (!string.IsNullOrWhiteSpace(result)) WindowReparent = result;
            }
            catch { }
        }

        private void ExecuteEditPropertyInspector()
        {
            try
            {
                var result = _dialogService?.ShowShortcutCapture(PropertyInspector, keyboardOnly: true);
                if (!string.IsNullOrWhiteSpace(result)) PropertyInspector = result;
            }
            catch { }
        }

        // Gesture editing is not exposed via commands in the UI currently.

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("HotkeyCommandPalette", out var v) && v.ValueKind == JsonValueKind.String) CommandPalette = v.GetString();
                if (d.TryGetValue("HotkeyEmergencyReset", out v) && v.ValueKind == JsonValueKind.String) EmergencyReset = v.GetString();
                if (d.TryGetValue("HotkeyWindowReparent", out v) && v.ValueKind == JsonValueKind.String) WindowReparent = v.GetString();
                if (d.TryGetValue("HotkeyPropertyInspector", out v) && v.ValueKind == JsonValueKind.String) PropertyInspector = v.GetString();
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
            if (!string.IsNullOrWhiteSpace(WindowReparent)) d["HotkeyWindowReparent"] = WindowReparent;
            if (!string.IsNullOrWhiteSpace(PropertyInspector)) d["HotkeyPropertyInspector"] = PropertyInspector;
            if (!string.IsNullOrWhiteSpace(OpacityGesture)) d["HotkeyOpacityGesture"] = OpacityGesture;
            if (!string.IsNullOrWhiteSpace(ToggleGesture)) d["HotkeyToggleGesture"] = ToggleGesture;
            return d;
        }
    }
}
