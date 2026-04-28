using System.Windows;
using System.Windows.Controls;

namespace WindowWorks.App.UI
{
    public partial class SettingsTabClickThrough : UserControl
    {
        public event Action? SettingsChanged;
        private TextBlock? _transparencyValueTextBlock;
        // This control is a UI for Click-Through settings.
        // Wire-up: call LoadFromSettings with settings dictionary to populate controls, and
        // expose an API to write changes back to the settings dictionary.

        public SettingsTabClickThrough()
        {
            InitializeComponent();
            this.Loaded += SettingsTabClickThrough_Loaded;
        }

        private void SettingsTabClickThrough_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                var sld = this.FindName("SldGestureTransparency") as Slider;
                if (sld != null)
                {
                    sld.ValueChanged += SldGestureTransparency_ValueChangedInternal;
                    // Ensure a display TextBlock exists beside the slider and cache it for fast updates.
                    _transparencyValueTextBlock = this.FindName("TxtGestureTransparencyValue") as TextBlock;
                    if (_transparencyValueTextBlock == null)
                    {
                        if (sld.Parent is StackPanel sp)
                        {
                            _transparencyValueTextBlock = new TextBlock { Width = 40, Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center, Text = ((int)sld.Value).ToString() + "%" };
                            sp.Children.Add(_transparencyValueTextBlock);
                        }
                    }
                    else
                    {
                        _transparencyValueTextBlock.Text = ((int)sld.Value).ToString() + "%";
                    }
                }

                // Attach change handlers to controls to notify parent window that this section became dirty
                try
                {
                    var chk = this.FindName("ChkEnableGesture") as CheckBox; if (chk != null) { chk.Checked += (_, __) => SettingsChanged?.Invoke(); chk.Unchecked += (_, __) => SettingsChanged?.Invoke(); }
                    var chk2 = this.FindName("ChkGestureAutoTransparency") as CheckBox; if (chk2 != null) { chk2.Checked += (_, __) => SettingsChanged?.Invoke(); chk2.Unchecked += (_, __) => SettingsChanged?.Invoke(); }
                    var chk3 = this.FindName("ChkGestureNotify") as CheckBox; if (chk3 != null) { chk3.Checked += (_, __) => SettingsChanged?.Invoke(); chk3.Unchecked += (_, __) => SettingsChanged?.Invoke(); }
                    var sld2 = this.FindName("SldGestureTransparency") as Slider; if (sld2 != null) { sld2.ValueChanged += (_, __) => SettingsChanged?.Invoke(); }

                    var chkm = this.FindName("ChkEnableModifier") as CheckBox; if (chkm != null) { chkm.Checked += (_, __) => SettingsChanged?.Invoke(); chkm.Unchecked += (_, __) => SettingsChanged?.Invoke(); }
                    var chkma = this.FindName("ChkModifierAutoTransparency") as CheckBox; if (chkma != null) { chkma.Checked += (_, __) => SettingsChanged?.Invoke(); chkma.Unchecked += (_, __) => SettingsChanged?.Invoke(); }
                    var sldm = this.FindName("SldModifierTransparency") as Slider; if (sldm != null) { sldm.ValueChanged += (_, __) => SettingsChanged?.Invoke(); }
                    var chkmn = this.FindName("ChkModifierNotify") as CheckBox; if (chkmn != null) { chkmn.Checked += (_, __) => SettingsChanged?.Invoke(); chkmn.Unchecked += (_, __) => SettingsChanged?.Invoke(); }
                }
                catch { }
            }
            catch { }
        }

        private void SldGestureTransparency_ValueChangedInternal(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (sender is Slider sld)
                {
                    if (_transparencyValueTextBlock != null)
                    {
                        _transparencyValueTextBlock.Text = ((int)sld.Value).ToString() + "%";
                    }
                    else
                    {
                        // Fallback: try FindName once
                        var tb = this.FindName("TxtGestureTransparencyValue") as TextBlock;
                        if (tb != null) tb.Text = ((int)sld.Value).ToString() + "%";
                    }
                    // notify parent
                    try { SettingsChanged?.Invoke(); } catch { }
                }
            }
            catch { }
        }

        private TextBlock? _modifierTransparencyTextBlock;
        private void SldModifierTransparency_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (sender is Slider sld)
                {
                    if (_modifierTransparencyTextBlock == null) _modifierTransparencyTextBlock = this.FindName("TxtModifierTransparencyValue") as TextBlock;
                    if (_modifierTransparencyTextBlock != null) _modifierTransparencyTextBlock.Text = ((int)sld.Value).ToString() + "%";
                    try { SettingsChanged?.Invoke(); } catch { }
                }
            }
            catch { }
        }

        // Legacy code-behind behavior replaced by MVVM.
        // Public helpers remain to aid backward compatibility if other code calls them, but they now prefer the ViewModel when present.
        public void LoadFromSettings(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? d)
        {
            if (d == null) return;
            try
            {
                var vm = this.DataContext as WindowWorks.App.UI.ViewModels.ClickThroughSettingsViewModel;
                if (vm != null)
                {
                    vm.LoadFromDictionary(d);
                    return;
                }

                // Fallback to manual population
                if (d.TryGetValue("EnableClickThroughGestureMode", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkEnableGesture.IsChecked = true; else if (d.TryGetValue("EnableClickThroughGestureMode", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkEnableGesture.IsChecked = false;
                if (d.TryGetValue("ClickThrough_Gesture_AutoTransparency", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkGestureAutoTransparency.IsChecked = true; else if (d.TryGetValue("ClickThrough_Gesture_AutoTransparency", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkGestureAutoTransparency.IsChecked = false;
                if (d.TryGetValue("ClickThrough_Gesture_TransparencyPercent", out v) && v.TryGetInt32(out var tp))
                {
                    SldGestureTransparency.Value = tp;
                    // update cached text block if present
                    if (_transparencyValueTextBlock != null) _transparencyValueTextBlock.Text = tp + "%";
                }
                if (d.TryGetValue("ClickThrough_Gesture_ShowNotification", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkGestureNotify.IsChecked = true; else if (d.TryGetValue("ClickThrough_Gesture_ShowNotification", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkGestureNotify.IsChecked = false;
                // Modifier mode settings
                if (d.TryGetValue("EnableClickThroughModifierMode", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkEnableModifier.IsChecked = true; else if (d.TryGetValue("EnableClickThroughModifierMode", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkEnableModifier.IsChecked = false;
                if (d.TryGetValue("ClickThrough_Modifier_AutoTransparency", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkModifierAutoTransparency.IsChecked = true; else if (d.TryGetValue("ClickThrough_Modifier_AutoTransparency", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkModifierAutoTransparency.IsChecked = false;
                if (d.TryGetValue("ClickThrough_Modifier_TransparencyPercent", out v) && v.TryGetInt32(out var mtp))
                {
                    SldModifierTransparency.Value = mtp;
                    if (_modifierTransparencyTextBlock != null) _modifierTransparencyTextBlock.Text = mtp + "%";
                }
                if (d.TryGetValue("ClickThrough_Modifier_ShowNotification", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkModifierNotify.IsChecked = true; else if (d.TryGetValue("ClickThrough_Modifier_ShowNotification", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkModifierNotify.IsChecked = false;
            }
            catch { }
        }

        public void SaveToDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> dict)
        {
            try
            {
                var vm = this.DataContext as WindowWorks.App.UI.ViewModels.ClickThroughSettingsViewModel;
                if (vm != null)
                {
                    var d = vm.ToDictionary();
                    foreach (var kv in d)
                    {
                        dict[kv.Key] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(kv.Value)).RootElement;
                    }
                    return;
                }

                dict["EnableClickThroughGestureMode"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ChkEnableGesture.IsChecked ?? false)).RootElement;
                dict["ClickThrough_Gesture_AutoTransparency"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ChkGestureAutoTransparency.IsChecked ?? false)).RootElement;
                dict["ClickThrough_Gesture_TransparencyPercent"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize((int)SldGestureTransparency.Value)).RootElement;
                dict["ClickThrough_Gesture_ShowNotification"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ChkGestureNotify.IsChecked ?? false)).RootElement;
                // Modifier mode settings
                dict["EnableClickThroughModifierMode"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ChkEnableModifier.IsChecked ?? false)).RootElement;
                dict["ClickThrough_Modifier_AutoTransparency"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ChkModifierAutoTransparency.IsChecked ?? false)).RootElement;
                dict["ClickThrough_Modifier_TransparencyPercent"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize((int)SldModifierTransparency.Value)).RootElement;
                dict["ClickThrough_Modifier_ShowNotification"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ChkModifierNotify.IsChecked ?? false)).RootElement;
            }
            catch { }
        }
    }
}
