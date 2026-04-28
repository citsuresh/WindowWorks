using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    public class ClickThroughSettingsViewModel : INotifyPropertyChanged, ISettingsSectionViewModel
    {
        private bool _enableGesture = true;
        private bool _gestureAutoTransparency = true;
        private int _gestureTransparencyPercent = 50;
        private bool _gestureShowNotification = true;

        private bool _enableModifier = true;
        private bool _modifierAutoTransparency = true;
        private int _modifierTransparencyPercent = 50;
        private bool _modifierShowNotification = true;

        public bool EnableGesture { get => _enableGesture; set { if (value == _enableGesture) return; _enableGesture = value; OnPropertyChanged(); } }
        public bool GestureAutoTransparency { get => _gestureAutoTransparency; set { if (value == _gestureAutoTransparency) return; _gestureAutoTransparency = value; OnPropertyChanged(); } }
        public int GestureTransparencyPercent { get => _gestureTransparencyPercent; set { if (value == _gestureTransparencyPercent) return; _gestureTransparencyPercent = value; OnPropertyChanged(); } }
        public bool GestureShowNotification { get => _gestureShowNotification; set { if (value == _gestureShowNotification) return; _gestureShowNotification = value; OnPropertyChanged(); } }

        public bool EnableModifier { get => _enableModifier; set { if (value == _enableModifier) return; _enableModifier = value; OnPropertyChanged(); } }
        public bool ModifierAutoTransparency { get => _modifierAutoTransparency; set { if (value == _modifierAutoTransparency) return; _modifierAutoTransparency = value; OnPropertyChanged(); } }
        public int ModifierTransparencyPercent { get => _modifierTransparencyPercent; set { if (value == _modifierTransparencyPercent) return; _modifierTransparencyPercent = value; OnPropertyChanged(); } }
        public bool ModifierShowNotification { get => _modifierShowNotification; set { if (value == _modifierShowNotification) return; _modifierShowNotification = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void LoadFromDictionary(Dictionary<string, JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                if (d.TryGetValue("EnableClickThroughGestureMode", out var v) && v.ValueKind == JsonValueKind.True) EnableGesture = true; else if (d.TryGetValue("EnableClickThroughGestureMode", out v) && v.ValueKind == JsonValueKind.False) EnableGesture = false;
                if (d.TryGetValue("ClickThrough_Gesture_AutoTransparency", out v) && v.ValueKind == JsonValueKind.True) GestureAutoTransparency = true; else if (d.TryGetValue("ClickThrough_Gesture_AutoTransparency", out v) && v.ValueKind == JsonValueKind.False) GestureAutoTransparency = false;
                if (d.TryGetValue("ClickThrough_Gesture_TransparencyPercent", out v) && v.TryGetInt32(out var tp)) GestureTransparencyPercent = tp;
                if (d.TryGetValue("ClickThrough_Gesture_ShowNotification", out v) && v.ValueKind == JsonValueKind.True) GestureShowNotification = true; else if (d.TryGetValue("ClickThrough_Gesture_ShowNotification", out v) && v.ValueKind == JsonValueKind.False) GestureShowNotification = false;

                if (d.TryGetValue("EnableClickThroughModifierMode", out v) && v.ValueKind == JsonValueKind.True) EnableModifier = true; else if (d.TryGetValue("EnableClickThroughModifierMode", out v) && v.ValueKind == JsonValueKind.False) EnableModifier = false;
                if (d.TryGetValue("ClickThrough_Modifier_AutoTransparency", out v) && v.ValueKind == JsonValueKind.True) ModifierAutoTransparency = true; else if (d.TryGetValue("ClickThrough_Modifier_AutoTransparency", out v) && v.ValueKind == JsonValueKind.False) ModifierAutoTransparency = false;
                if (d.TryGetValue("ClickThrough_Modifier_TransparencyPercent", out v) && v.TryGetInt32(out var mtp)) ModifierTransparencyPercent = mtp;
                if (d.TryGetValue("ClickThrough_Modifier_ShowNotification", out v) && v.ValueKind == JsonValueKind.True) ModifierShowNotification = true; else if (d.TryGetValue("ClickThrough_Modifier_ShowNotification", out v) && v.ValueKind == JsonValueKind.False) ModifierShowNotification = false;
            }
            catch { }
        }

        public Dictionary<string, object?> ToDictionary()
        {
            var d = new Dictionary<string, object?>();
            d["EnableClickThroughGestureMode"] = EnableGesture;
            d["ClickThrough_Gesture_AutoTransparency"] = GestureAutoTransparency;
            d["ClickThrough_Gesture_TransparencyPercent"] = GestureTransparencyPercent;
            d["ClickThrough_Gesture_ShowNotification"] = GestureShowNotification;

            d["EnableClickThroughModifierMode"] = EnableModifier;
            d["ClickThrough_Modifier_AutoTransparency"] = ModifierAutoTransparency;
            d["ClickThrough_Modifier_TransparencyPercent"] = ModifierTransparencyPercent;
            d["ClickThrough_Modifier_ShowNotification"] = ModifierShowNotification;
            return d;
        }
    }
}
