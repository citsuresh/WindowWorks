using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;

namespace WindowWorks.App.UI
{
    // SettingsWindow is UI-only and does not reference application models to avoid circular project refs.
    public partial class SettingsWindow : Window
    {
        private TaskCompletionSource<string?>? _tcs;
        private System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? _initialDict;
        // Ensure only one settings window instance is active at a time.
        private static SettingsWindow? s_activeWindow;
        private static TaskCompletionSource<string?>? s_activeTcs;
        private static readonly object s_lock = new();
        // Cache per-section control and VM so changes persist when switching tabs
        private class SectionData
        {
            public string Key = string.Empty;
            public UserControl? Control;
            public ViewModels.ISettingsSectionViewModel? Vm;
            public bool Dirty;
            public System.Windows.Controls.TextBlock? StarIndicator;
            public System.Windows.Controls.ListBoxItem? NavItem;
        }

        private void UpdateNavItemDirty(string key, bool isDirty)
        {
            try
            {
                if (!_sections.TryGetValue(key, out var sd)) return;
                sd.Dirty = isDirty;
                if (sd.StarIndicator != null)
                {
                    sd.StarIndicator.Visibility = isDirty ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    // Also bold the text by updating the sibling Run if possible
                    if (sd.NavItem?.Content is System.Windows.Controls.StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is System.Windows.Controls.TextBlock tb)
                    {
                        tb.FontWeight = isDirty ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
                    }
                }
            }
            catch { }
        }

        private void WireVmDirtyTracking(string key, System.ComponentModel.INotifyPropertyChanged vm)
        {
            if (vm == null) return;
            vm.PropertyChanged += (s, e) =>
            {
                try
                {
                    // Debug: log VM property changes for troubleshooting
                    try { System.Diagnostics.Debug.WriteLine($"[SettingsWindow] VM change: {key} -> {e.PropertyName}"); } catch { }
                    // Mark section dirty on any VM property change
                    UpdateNavItemDirty(key, true);
                    if (_sections.TryGetValue(key, out var sd)) sd.Dirty = true;
                }
                catch { }
            };
        }
        private readonly System.Collections.Generic.Dictionary<string, SectionData> _sections = new();

        public SettingsWindow()
        {
            InitializeComponent();
            // Build navigation list with mod indicator placeholders so we can mark modified sections
            try
            {
                NavList.Items.Clear();
                var sections = new[] { "General", "Highlight", "HUD", "Click-Through", "Shortcuts" };
                foreach (var s in sections)
                {
                    var nameTb = new System.Windows.Controls.TextBlock(new System.Windows.Documents.Run(s));
                    var star = new System.Windows.Controls.TextBlock(new System.Windows.Documents.Run(" *")) { Visibility = System.Windows.Visibility.Collapsed, FontWeight = System.Windows.FontWeights.Bold };
                    var sp = new System.Windows.Controls.StackPanel() { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    sp.Children.Add(nameTb);
                    sp.Children.Add(star);
                    var lbi = new System.Windows.Controls.ListBoxItem() { Content = sp, Tag = s };
                    NavList.Items.Add(lbi);
                    // Initialize section data cache
                    var sd = new SectionData() { Key = s, Control = null, Vm = null, Dirty = false, StarIndicator = star, NavItem = lbi };
                    _sections[s] = sd;
                }
                if (NavList.SelectedIndex < 0) NavList.SelectedIndex = 0;
            }
            catch { }
        }

        public SettingsWindow(string currentJson, TaskCompletionSource<string?> tcs) : this()
        {
            _tcs = tcs;
            // Optionally initialize controls from provided JSON (lightweight parsing)
            try
            {
                if (!string.IsNullOrWhiteSpace(currentJson))
                {
                    _initialDict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(currentJson);
                    // Defer applying values until pages are shown; consumer may expand later
                }
            }
            catch { }
            // Create VMs for all sections up-front so state is preserved even if a tab was never shown
            try
            {
                // General
                var gvm = new WindowWorks.App.UI.ViewModels.GeneralSettingsViewModel();
                try { if (_initialDict != null) gvm.LoadFromDictionary(_initialDict); } catch { }
                _sections["General"].Vm = gvm;
                try { WireVmDirtyTracking("General", gvm); } catch { }

                // Highlight
                var hvm = new WindowWorks.App.UI.ViewModels.HighlightSettingsViewModel();
                try { if (_initialDict != null) hvm.LoadFromDictionary(_initialDict); } catch { }
                _sections["Highlight"].Vm = hvm;
                try { WireVmDirtyTracking("Highlight", hvm); } catch { }

                // HUD
                var hudVm = new WindowWorks.App.UI.ViewModels.HudSettingsViewModel();
                try { if (_initialDict != null) hudVm.LoadFromDictionary(_initialDict); } catch { }
                _sections["HUD"].Vm = hudVm;
                try { WireVmDirtyTracking("HUD", hudVm); } catch { }

                // Click-Through
                var ctVm = new WindowWorks.App.UI.ViewModels.ClickThroughSettingsViewModel();
                try { if (_initialDict != null) ctVm.LoadFromDictionary(_initialDict); } catch { }
                _sections["Click-Through"].Vm = ctVm;
                try { WireVmDirtyTracking("Click-Through", ctVm); } catch { }

                // Shortcuts
                var sVm = new WindowWorks.App.UI.ViewModels.ShortcutsSettingsViewModel();
                try { if (_initialDict != null) sVm.LoadFromDictionary(_initialDict); } catch { }
                _sections["Shortcuts"].Vm = sVm;
                try { WireVmDirtyTracking("Shortcuts", sVm); } catch { }
            }
            catch { }
            // Now that initial dictionary is available, select first page to ensure values are applied.
            // NavList.SelectedIndex may already be 0 (set in parameterless ctor) and won't raise SelectionChanged again,
            // so force a reset to trigger loading with the initial dictionary.
            try { NavList.SelectedIndex = -1; NavList.SelectedIndex = 0; } catch { }
            try { RegisterActiveInstance(); } catch { }
        }

        private void RegisterActiveInstance()
        {
            // Called on the window's STA thread during creation
            lock (s_lock)
            {
                s_activeWindow = this;
            }
        }

        // Ensure WPF bindings on a control are pushed to their sources before reading ViewModel state.
        private void UpdateBindingSource(FrameworkElement parent, string controlName, System.Windows.DependencyProperty dp)
        {
            try
            {
                if (parent == null) return;
                var ctrl = parent.FindName(controlName) as FrameworkElement;
                if (ctrl == null) return;
                var be = System.Windows.Data.BindingOperations.GetBindingExpression(ctrl, dp);
                be?.UpdateSource();
            }
            catch { }
        }

        private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var item = NavList.SelectedItem as System.Windows.Controls.ListBoxItem;
            if (item == null) return;
            var key = item.Tag as string ?? (item.Content as string) ?? string.Empty;
            // Reuse cached control/VM if present
            if (_sections.TryGetValue(key, out var sd))
            {
                if (sd.Control != null)
                {
                    // Ensure VM is attached if available
                    if (sd.Vm != null) sd.Control.DataContext = sd.Vm;
                    ContentArea.Content = sd.Control;
                    return;
                }
            }
            switch (key)
            {
                case "General":
                    var g = new GeneralSettingsControl();
                    // Use MVVM: create GeneralSettingsViewModel, load initial values and assign as DataContext
                    try
                    {
                        var gvm = new WindowWorks.App.UI.ViewModels.GeneralSettingsViewModel();
                        if (_initialDict != null) gvm.LoadFromDictionary(_initialDict);
                        g.DataContext = gvm;
                        _sections["General"].Vm = gvm;
                        // Track dirty state when VM properties change
                        try { WireVmDirtyTracking("General", gvm); } catch { }
                    }
                    catch
                    {
                        // Fallback: if ViewModel creation fails, attempt legacy direct loading on the control
                        try { g.LoadFromDictionary(_initialDict); } catch { }
                    }
                    ContentArea.Content = g;
                    _sections["General"].Control = g;
                    break;
                case "Highlight":
                    var h = new HighlightSettingsControl();
                    // Attach ViewModel and load data into it
                    var vm = new WindowWorks.App.UI.ViewModels.HighlightSettingsViewModel();
                    try { if (_initialDict != null) vm.LoadFromDictionary(_initialDict); } catch { }
                    h.DataContext = vm;
                    _sections["Highlight"].Vm = vm;
                    try { WireVmDirtyTracking("Highlight", vm); } catch { }
                    ContentArea.Content = h;
                    _sections["Highlight"].Control = h;
                    break;
                case "HUD":
                    var hud = new HudSettingsControl();
                    // Use MVVM: create a HudSettingsViewModel, load settings into it, and assign as DataContext
                    try
                    {
                        var hudVm = new WindowWorks.App.UI.ViewModels.HudSettingsViewModel();
                        if (_initialDict != null) hudVm.LoadFromDictionary(_initialDict);
                        hud.DataContext = hudVm;
                        _sections["HUD"].Vm = hudVm;
                        try { WireVmDirtyTracking("HUD", hudVm); } catch { }
                    }
                    catch
                    {
                        // If VM setup fails, still show the control without pre-loading (legacy fallback removed)
                    }
                    ContentArea.Content = hud;
                    _sections["HUD"].Control = hud;
                    break;
                case "Shortcuts":
                    var sControl = new ShortcutsSettingsControl();
                    // Use MVVM: create a ShortcutsSettingsViewModel, load settings into it, and assign as DataContext
                    try
                    {
                        var sVm = new WindowWorks.App.UI.ViewModels.ShortcutsSettingsViewModel();
                        if (_initialDict != null) sVm.LoadFromDictionary(_initialDict);
                        sControl.DataContext = sVm;
                        _sections["Shortcuts"].Vm = sVm;
                        try { WireVmDirtyTracking("Shortcuts", sVm); } catch { }
                    }
                    catch
                    {
                        // If VM setup fails, fall back to control (legacy behavior removed)
                    }
                    // Subscribe to simple CLR event in case parent needs to react; control updates the dictionary itself.
                    sControl.HotkeysChanged += (_) => { /* no-op */ };
                    // Subscribe to control-level settings changed notification when available to mark section dirty immediately.
                    try { sControl.SettingsChanged += () => { _sections["Shortcuts"].Dirty = true; UpdateNavItemDirty("Shortcuts", true); }; } catch { }
                    ContentArea.Content = sControl;
                    break;
                case "Click-Through":
                    var ct = new SettingsTabClickThrough();
                    try
                    {
                        var ctVm = new WindowWorks.App.UI.ViewModels.ClickThroughSettingsViewModel();
                        if (_initialDict != null) ctVm.LoadFromDictionary(_initialDict);
                        ct.DataContext = ctVm;
                        _sections["Click-Through"].Vm = ctVm;
                        try { WireVmDirtyTracking("Click-Through", ctVm); } catch { }
                    }
                    catch
                    {
                        try { if (_initialDict != null) ct.LoadFromSettings(_initialDict); } catch { }
                    }
                    ContentArea.Content = ct;
                    try { ct.SettingsChanged += () => { _sections["Click-Through"].Dirty = true; UpdateNavItemDirty("Click-Through", true); }; } catch { }
                    _sections["Click-Through"].Control = ct;
                    break;

                default:
                    ContentArea.Content = null;
                    break;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Collect settings from current content control(s).
            // For now, collect from known controls if present.
            try
            {
                // Build a lightweight dictionary and serialize to JSON for the host to consume
                var settingsDict = new Dictionary<string, object?>();
                // Attempt to populate fields from HighlightSettingsControl if visible
                if (ContentArea.Content is HighlightSettingsControl h)
                {
                    // Prefer ViewModel when available
                    if (h.DataContext is WindowWorks.App.UI.ViewModels.ISettingsSectionViewModel sec)
                    {
                        // Ensure bindings have been pushed from UI to VM
                        try { UpdateBindingSource(h, "TxtBorderColor", System.Windows.Controls.TextBox.TextProperty); } catch { }
                        try { UpdateBindingSource(h, "SldBorderTransparency", System.Windows.Controls.Slider.ValueProperty); } catch { }
                        try { UpdateBindingSource(h, "TxtBorderThickness", System.Windows.Controls.TextBox.TextProperty); } catch { }
                        try { UpdateBindingSource(h, "TxtCornerRadius", System.Windows.Controls.TextBox.TextProperty); } catch { }
                        try { UpdateBindingSource(h, "TxtHighlightMs", System.Windows.Controls.TextBox.TextProperty); } catch { }
                        try { UpdateBindingSource(h, "TxtHudMs", System.Windows.Controls.TextBox.TextProperty); } catch { }
                        var dict = sec.ToDictionary();
                        foreach (var kv in dict) settingsDict[kv.Key] = kv.Value;
                    }
                    else
                    {
                        // Read values by finding named elements (legacy fallback)
                        var tbColor = h.FindName("TxtBorderColor") as System.Windows.Controls.TextBox;
                        var tbBorder = h.FindName("TxtBorderThickness") as System.Windows.Controls.TextBox;
                        var tbCorner = h.FindName("TxtCornerRadius") as System.Windows.Controls.TextBox;
                        var tbHighlight = h.FindName("TxtHighlightMs") as System.Windows.Controls.TextBox;
                        var tbHud = h.FindName("TxtHudMs") as System.Windows.Controls.TextBox;
                        var chkSys = h.FindName("ChkUseSystemColors") as System.Windows.Controls.CheckBox;
                        if (tbColor != null)
                        {
                            // Combine color and transparency into #AARRGGBB when possible.
                            var colorText = tbColor.Text?.Trim() ?? string.Empty;
                            // determine transparency slider if present
                            var sld = h.FindName("SldBorderTransparency") as System.Windows.Controls.Slider;
                            int percent = 0;
                            try { if (sld != null) percent = (int)sld.Value; else if (h.FindName("TxtBorderTransparencyValue") is System.Windows.Controls.TextBlock tv && int.TryParse(tv.Text, out var v)) percent = v; } catch { }

                            // If percent is not set but the color contains an alpha channel (#AARRGGBB), derive percent from alpha
                            try
                            {
                                if (percent == 0 && !string.IsNullOrWhiteSpace(colorText) && colorText.StartsWith("#") && colorText.Length == 9)
                                {
                                    var aHex = colorText.Substring(1, 2);
                                    if (byte.TryParse(aHex, System.Globalization.NumberStyles.HexNumber, null, out var a))
                                    {
                                        percent = (int)Math.Round(100.0 - (a / 255.0 * 100.0));
                                    }
                                }
                            }
                            catch { }

                            string finalColor = colorText;
                            try
                            {
                                string baseHex = colorText ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(baseHex))
                                {
                                    if (baseHex.StartsWith("#"))
                                    {
                                        if (baseHex.Length == 9) baseHex = baseHex.Substring(3); // #AARRGGBB -> RRGGBB
                                        else if (baseHex.Length == 7) baseHex = baseHex.Substring(1); // #RRGGBB -> RRGGBB
                                    }
                                    // ensure 6-digit base
                                    if (baseHex.Length == 6)
                                    {
                                        var a = (byte)(255 * (100 - Math.Clamp(percent, 0, 100)) / 100.0);
                                        finalColor = $"#{a:X2}{baseHex.ToUpperInvariant()}";
                                    }
                                }
                            }
                            catch { }
                            settingsDict["HighlightBorderColor"] = finalColor;
                            // also persist percent for backward compatibility
                            settingsDict["HighlightBorderTransparencyPercent"] = percent;
                        }
                        if (tbBorder != null && int.TryParse(tbBorder.Text, out var bt)) settingsDict["HighlightBorderThickness"] = bt;
                        if (tbCorner != null && int.TryParse(tbCorner.Text, out var cr)) settingsDict["HighlightCornerRadius"] = cr;
                        if (tbHighlight != null && int.TryParse(tbHighlight.Text, out var hm)) settingsDict["HighlightDurationMs"] = hm;
                        if (tbHud != null && int.TryParse(tbHud.Text, out var um)) settingsDict["HudDurationMs"] = um;
                        if (chkSys != null) settingsDict["UseSystemColors"] = chkSys.IsChecked == true;
                    }
                }

                // For General page, prefer ViewModel values when available (MVVM)
                if (ContentArea.Content is GeneralSettingsControl g)
                {
                    // If a ViewModel is attached, use it
                    if (g.DataContext is WindowWorks.App.UI.ViewModels.GeneralSettingsViewModel gvm)
                    {
                        // Ensure any bindings have been pushed
                        try { UpdateBindingSource(g, "ChkEnableHighlight", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty); } catch { }
                        try { UpdateBindingSource(g, "ChkEnableConfirmations", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty); } catch { }
                        var dict = gvm.ToDictionary();
                        foreach (var kv in dict) settingsDict[kv.Key] = kv.Value;
                    }
                    else
                    {
                        // Legacy fallback: read directly from named elements
                        var chk1 = g.FindName("ChkEnableHighlight") as System.Windows.Controls.CheckBox;
                        var chk2 = g.FindName("ChkEnableConfirmations") as System.Windows.Controls.CheckBox;
                        if (chk1 != null) settingsDict["EnableHighlight"] = chk1.IsChecked == true;
                        if (chk2 != null) settingsDict["EnableConfirmations"] = chk2.IsChecked == true;
                    }
                }
                // For Shortcuts page, prefer ViewModel values when available, otherwise capture hotkey strings from the control
                if (ContentArea.Content is ShortcutsSettingsControl s)
                {
                    if (s.DataContext is WindowWorks.App.UI.ViewModels.ISettingsSectionViewModel sVm)
                    {
                        try
                        {
                            // Ensure any bindings on the control have been pushed where applicable
                            try { UpdateBindingSource(s, "PickerCommandPalette", System.Windows.Controls.Control.TagProperty); } catch { }
                            try { UpdateBindingSource(s, "PickerEmergencyReset", System.Windows.Controls.Control.TagProperty); } catch { }
                            var dict = sVm.ToDictionary();
                            foreach (var kv in dict) settingsDict[kv.Key] = kv.Value;
                        }
                        catch { }
                    }
                    else
                    {
                        var picker1 = s.FindName("PickerCommandPalette") as ShortcutPicker;
                        var picker2 = s.FindName("PickerEmergencyReset") as ShortcutPicker;
                        if (picker1 != null) settingsDict["HotkeyCommandPalette"] = picker1.Shortcut;
                        if (picker2 != null) settingsDict["HotkeyEmergencyReset"] = picker2.Shortcut;
                    }
                    // Opacity nudge and Toggle Topmost are mouse gestures and are not saved as keyboard shortcuts.
                }
                // For Click-Through page, capture Gesture Mode settings
                if (ContentArea.Content is SettingsTabClickThrough ct)
                {
                    var chkEnable = ct.FindName("ChkEnableGesture") as System.Windows.Controls.CheckBox;
                    var chkAuto = ct.FindName("ChkGestureAutoTransparency") as System.Windows.Controls.CheckBox;
                    var sld = ct.FindName("SldGestureTransparency") as System.Windows.Controls.Slider;
                    var chkNotify = ct.FindName("ChkGestureNotify") as System.Windows.Controls.CheckBox;

                    if (chkEnable != null) settingsDict["EnableClickThroughGestureMode"] = chkEnable.IsChecked == true;
                    if (chkAuto != null) settingsDict["ClickThrough_Gesture_AutoTransparency"] = chkAuto.IsChecked == true;
                    if (sld != null) settingsDict["ClickThrough_Gesture_TransparencyPercent"] = (int)sld.Value;
                    if (chkNotify != null) settingsDict["ClickThrough_Gesture_ShowNotification"] = chkNotify.IsChecked == true;
                    // Modifier Mode controls
                    var chkModEnable = ct.FindName("ChkEnableModifier") as System.Windows.Controls.CheckBox;
                    var chkModAuto = ct.FindName("ChkModifierAutoTransparency") as System.Windows.Controls.CheckBox;
                    var sldMod = ct.FindName("SldModifierTransparency") as System.Windows.Controls.Slider;
                    var chkModNotify = ct.FindName("ChkModifierNotify") as System.Windows.Controls.CheckBox;
                    if (chkModEnable != null) settingsDict["EnableClickThroughModifierMode"] = chkModEnable.IsChecked == true;
                    if (chkModAuto != null) settingsDict["ClickThrough_Modifier_AutoTransparency"] = chkModAuto.IsChecked == true;
                    if (sldMod != null) settingsDict["ClickThrough_Modifier_TransparencyPercent"] = (int)sldMod.Value;
                    if (chkModNotify != null) settingsDict["ClickThrough_Modifier_ShowNotification"] = chkModNotify.IsChecked == true;
                }
                // For HUD page, capture HUD visual settings. Prefer ViewModel values when available (MVVM)
                if (ContentArea.Content is HudSettingsControl hud)
                {
                    if (hud.DataContext is WindowWorks.App.UI.ViewModels.HudSettingsViewModel hv)
                    {
                        // Ensure UI bindings have pushed latest values to the ViewModel
                        UpdateBindingSource(hud, "TxtHudBackground", System.Windows.Controls.TextBox.TextProperty);
                        UpdateBindingSource(hud, "TxtHudFontSize", System.Windows.Controls.TextBox.TextProperty);
                        UpdateBindingSource(hud, "TxtHudCorner", System.Windows.Controls.TextBox.TextProperty);
                        UpdateBindingSource(hud, "TxtHudDuration", System.Windows.Controls.TextBox.TextProperty);
                        UpdateBindingSource(hud, "SldTransparency", System.Windows.Controls.Slider.ValueProperty);
                        UpdateBindingSource(hud, "ChkOpacityHud", System.Windows.Controls.CheckBox.IsCheckedProperty);
                        UpdateBindingSource(hud, "ChkTopmostHud", System.Windows.Controls.CheckBox.IsCheckedProperty);
                        UpdateBindingSource(hud, "ChkPresetHud", System.Windows.Controls.CheckBox.IsCheckedProperty);

                        var dict = hv.ToDictionary();
                        foreach (var kv in dict) settingsDict[kv.Key] = kv.Value;
                    }
                    else
                    {
                        var tbBg = hud.FindName("TxtHudBackground") as System.Windows.Controls.TextBox;
                        var tbTrans = hud.FindName("TxtTransparency") as System.Windows.Controls.TextBox;
                        var tbFont = hud.FindName("TxtHudFontSize") as System.Windows.Controls.TextBox;
                        var tbCorner = hud.FindName("TxtHudCorner") as System.Windows.Controls.TextBox;
                        var chkOpacity = hud.FindName("ChkOpacityHud") as System.Windows.Controls.CheckBox;
                        var chkTop = hud.FindName("ChkTopmostHud") as System.Windows.Controls.CheckBox;
                        var chkPreset = hud.FindName("ChkPresetHud") as System.Windows.Controls.CheckBox;
                        var tbDuration = hud.FindName("TxtHudDuration") as System.Windows.Controls.TextBox;
                        if (tbBg != null)
                        {
                            var colorText = tbBg.Text?.Trim() ?? string.Empty;
                            // prefer slider value for transparency if present
                            var sld = hud.FindName("SldTransparency") as System.Windows.Controls.Slider;
                            int percent = 0;
                            try { if (sld != null) percent = (int)sld.Value; else if (tbTrans != null && int.TryParse(tbTrans.Text, out var v)) percent = v; } catch { }

                            // If percent is not set but the color contains an alpha channel (#AARRGGBB), derive percent from alpha
                            try
                            {
                                if (percent == 0 && !string.IsNullOrWhiteSpace(colorText) && colorText.StartsWith("#") && colorText.Length == 9)
                                {
                                    var aHex = colorText.Substring(1, 2);
                                    if (byte.TryParse(aHex, System.Globalization.NumberStyles.HexNumber, null, out var a))
                                    {
                                        percent = (int)Math.Round(100.0 - (a / 255.0 * 100.0));
                                    }
                                }
                            }
                            catch { }

                            string finalColor = colorText;
                            try
                            {
                                string baseHex = colorText ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(baseHex))
                                {
                                    if (baseHex.StartsWith("#"))
                                    {
                                        if (baseHex.Length == 9) baseHex = baseHex.Substring(3); // #AARRGGBB -> RRGGBB
                                        else if (baseHex.Length == 7) baseHex = baseHex.Substring(1); // #RRGGBB -> RRGGBB
                                    }
                                    if (baseHex.Length == 6)
                                    {
                                        var a = (byte)(255 * (100 - Math.Clamp(percent, 0, 100)) / 100.0);
                                        finalColor = $"#{a:X2}{baseHex.ToUpperInvariant()}";
                                    }
                                }
                            }
                            catch { }
                            settingsDict["HudBackgroundColor"] = finalColor;
                            settingsDict["HudTransparencyPercent"] = percent;
                            if (tbDuration != null && int.TryParse(tbDuration.Text, out var dm)) settingsDict["HudDurationMs"] = dm;
                        }
                        if (tbFont != null && int.TryParse(tbFont.Text, out var fs)) settingsDict["HudFontSize"] = fs;
                        if (tbCorner != null && int.TryParse(tbCorner.Text, out var cr)) settingsDict["HudCornerRadius"] = cr;
                        if (chkOpacity != null) settingsDict["ShowHudOnOpacityChange"] = chkOpacity.IsChecked == true;
                        if (chkTop != null) settingsDict["ShowHudOnTopmostToggle"] = chkTop.IsChecked == true;
                        if (chkPreset != null) settingsDict["ShowHudOnPresetApplied"] = chkPreset.IsChecked == true;
                    }
                }
                // Collect dictionaries from all sections (VMs preferred)
                foreach (var kv in _sections)
                {
                    try
                    {
                        var sd = kv.Value;
                        if (sd.Vm != null)
                        {
                            var d = sd.Vm.ToDictionary();
                            foreach (var k2 in d) settingsDict[k2.Key] = k2.Value;
                        }
                        else if (sd.Control != null)
                        {
                            // Fallback: if control exposes SaveToDictionary, call it
                            try
                            {
                                var mi = sd.Control.GetType().GetMethod("SaveToDictionary");
                                if (mi != null)
                                {
                                    var dictParam = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();
                                    mi.Invoke(sd.Control, new object[] { dictParam });
                                    foreach (var kv2 in dictParam) settingsDict[kv2.Key] = kv2.Value;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // Signal result as JSON and close
                // Apply hotkey changes immediately via host service so keyboard hotkeys re-register without restart.
                try
                {
                    string? cp = null;
                    string? er = null;
                    if (settingsDict.TryGetValue("HotkeyCommandPalette", out var cpObj) && cpObj is string cpStr) cp = cpStr;
                    if (settingsDict.TryGetValue("HotkeyEmergencyReset", out var erObj) && erObj is string erStr) er = erStr;
                    try
                    {
                        // Only apply hotkey changes if the Shortcuts section was modified to avoid re-registering unchanged hotkeys
                        if (_sections.TryGetValue("Shortcuts", out var shortcutsSection) && shortcutsSection.Dirty)
                        {
                            var res = WindowWorks.App.UI.AppServices.HotkeyApplyService.ApplyHotkeys(cp, er, null, null, null);
                            // Optionally, UI could surface res to show per-key errors. For now we ignore the result.
                        }
                    }
                    catch { }

                    // Also apply modifier mode settings immediately via host service
                    try
                    {
                        // Only apply modifier settings if the Click-Through section was modified to prevent re-applying unchanged modifier settings
                        if (_sections.TryGetValue("Click-Through", out var ctSection) && ctSection.Dirty)
                        {
                            bool? enableMod = null;
                            bool? modAuto = null;
                            int? modTp = null;
                            bool? modNotify = null;
                            if (settingsDict.TryGetValue("EnableClickThroughModifierMode", out var em)) { if (em is bool b) enableMod = b; }
                            if (settingsDict.TryGetValue("ClickThrough_Modifier_AutoTransparency", out var ma)) { if (ma is bool b2) modAuto = b2; }
                            if (settingsDict.TryGetValue("ClickThrough_Modifier_TransparencyPercent", out var mt)) { if (mt is int iv) modTp = iv; }
                            if (settingsDict.TryGetValue("ClickThrough_Modifier_ShowNotification", out var mn)) { if (mn is bool b3) modNotify = b3; }
                            try { var r2 = WindowWorks.App.UI.AppServices.HotkeyApplyService.ApplyModifierSettings(enableMod, modAuto, modTp, modNotify); } catch { }
                        }
                    }
                    catch { }
                }
                catch { }

                var json = JsonSerializer.Serialize(settingsDict);
                // Emit a debug copy of the settings to help diagnose persistence issues
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowWorks");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "settings-emitted.json");
                    File.WriteAllText(path, json);
                    Debug.WriteLine($"[SettingsWindow] Emitted settings to: {path}");
                    Debug.WriteLine(json);
                }
                catch { }

                // Mark all sections as clean now that we've generated the merged JSON
                try
                {
                    foreach (var sd in _sections.Values)
                    {
                        sd.Dirty = false;
                        try { UpdateNavItemDirty(sd.Key, false); } catch { }
                    }
                }
                catch { }

                _tcs?.TrySetResult(json);
            }
            catch
            {
                _tcs?.TrySetResult(null);
            }
            finally
            {
                try { UnregisterActiveInstance(); } catch { }
                this.Close();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _tcs?.TrySetResult(null);
            try { UnregisterActiveInstance(); } catch { }
            this.Close();
        }

        // Helper to show the settings window on an STA thread and return updated settings JSON or null
        // Non-blocking async variant so callers need not block the main UI thread.
        public static Task<string?> ShowDialogModalAsync(string currentJson)
        {
            lock (s_lock)
            {
                if (s_activeWindow != null)
                {
                    // If an instance is already active, return its TCS so caller can await the existing window result.
                    return s_activeTcs?.Task ?? Task.FromResult<string?>(null);
                }
                var tcs = new TaskCompletionSource<string?>();
                s_activeTcs = tcs;

                var thread = new Thread(() =>
                {
                    var win = new SettingsWindow(currentJson, tcs);
                    try
                    {
                        win.ShowDialog();
                    }
                    catch
                    {
                        // Ensure TCS completes in case of unexpected failure
                        tcs.TrySetResult(null);
                    }
                    finally
                    {
                        // If window closed without producing a result, ensure TCS has a value
                        if (!tcs.Task.IsCompleted)
                        {
                            tcs.TrySetResult(null);
                        }
                        lock (s_lock)
                        {
                            s_activeTcs = null;
                            s_activeWindow = null;
                        }
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();

                // Return the task; caller should await it. Do not block the caller thread.
                return tcs.Task;
            }
        }

        // Backwards-compatible synchronous wrapper (keeps previous behavior).
        public static string? ShowDialogModal(string currentJson)
        {
            return ShowDialogModalAsync(currentJson).GetAwaiter().GetResult();
        }

        private void UnregisterActiveInstance()
        {
            lock (s_lock)
            {
                if (s_activeWindow == this)
                {
                    s_activeWindow = null;
                    // ensure TCS cleared by caller path in ShowDialogModalAsync finalizer
                }
            }
        }
    }
}
