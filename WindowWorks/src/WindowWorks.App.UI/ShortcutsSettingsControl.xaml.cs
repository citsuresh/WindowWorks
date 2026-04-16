using System.Windows;
using System.Windows.Controls;

namespace WindowWorks.App.UI
{
    public partial class ShortcutsSettingsControl : UserControl
    {
        private System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? _initialDict;
        private bool _suppressNotifications = false;
        // Simple CLR event for change notifications (preferable for MVVM transition)
        public event Action<ShortcutsSettingsControl>? HotkeysChanged;

        public ShortcutsSettingsControl()
        {
            InitializeComponent();
            // ShortcutChanged handlers are wired in XAML (ShortcutChanged="OnShortcutChanged")
        }

        public void LoadFromSettings(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? d)
        {
            if (d == null) return;
            _suppressNotifications = true;
            if (d.TryGetValue("HotkeyCommandPalette", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) PickerCommandPalette.Shortcut = v.GetString();
            if (d.TryGetValue("HotkeyEmergencyReset", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String) PickerEmergencyReset.Shortcut = v.GetString();
            // Load persisted gestures and update visible labels
            if (d.TryGetValue("HotkeyOpacityNudge", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String) LblOpacityGesture.Text = v.GetString() ?? LblOpacityGesture.Text;
            if (d.TryGetValue("HotkeyToggleTopmost", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String) LblToggleGesture.Text = v.GetString() ?? LblToggleGesture.Text;
            ValidateConflicts();
            _suppressNotifications = false;
        }

        // LoadFromDictionary is deprecated for Shortcuts; ShortcutsSettingsViewModel should be used as DataContext instead.
        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? d)
        {
            // Keep backward-compat shim: populate UI directly if control is used without VM
            _initialDict = d;
            if (_initialDict != null) LoadFromSettings(_initialDict);
        }

        private void OnHotkeyTextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateConflicts();
            UpdateInitialDictFromUi();
            // Notify parent via CLR event
            HotkeysChanged?.Invoke(this);
        }

        private void ValidateConflicts()
        {
            // Reset
            PickerCommandPalette.BorderBrush = System.Windows.Media.Brushes.Gray;
            PickerEmergencyReset.BorderBrush = System.Windows.Media.Brushes.Gray;

            // Parse both and compare
            if (!string.IsNullOrWhiteSpace(PickerCommandPalette.Shortcut) && !string.IsNullOrWhiteSpace(PickerEmergencyReset.Shortcut))
            {
                if (PickerCommandPalette.Shortcut == PickerEmergencyReset.Shortcut)
                {
                    PickerCommandPalette.BorderBrush = System.Windows.Media.Brushes.Red;
                    PickerEmergencyReset.BorderBrush = System.Windows.Media.Brushes.Red;
                }
            }

        }

        // Simple local hotkey parser that returns modifier mask and key token.
        // Modifiers mask: bit0=Ctrl, bit1=Alt, bit2=Shift, bit3=Win
        private static bool TryParseHotkeyLocal(string s, out int modsMask, out string keyToken)
        {
            modsMask = 0; keyToken = string.Empty;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try
            {
                var parts = s.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var t = p.Trim();
                    if (string.Equals(t, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Control", StringComparison.OrdinalIgnoreCase)) modsMask |= 1;
                    else if (string.Equals(t, "Alt", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Menu", StringComparison.OrdinalIgnoreCase)) modsMask |= 2;
                    else if (string.Equals(t, "Shift", StringComparison.OrdinalIgnoreCase)) modsMask |= 4;
                    else if (string.Equals(t, "Win", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Windows", StringComparison.OrdinalIgnoreCase)) modsMask |= 8;
                    else
                    {
                        // treat remainder as key token
                        keyToken = t.ToUpperInvariant();
                    }
                }
                return !string.IsNullOrEmpty(keyToken);
            }
            catch { return false; }
        }

        private void OnShortcutChanged(object sender, string shortcut)
        {
            if (sender == PickerCommandPalette)
            {
                // Handle Command palette shortcut change
                ValidateConflicts();
                UpdateInitialDictFromUi();
                HotkeysChanged?.Invoke(this);
            }
            else if (sender == PickerEmergencyReset)
            {
                // Handle Reset All shortcut change
                ValidateConflicts();
                UpdateInitialDictFromUi();
                HotkeysChanged?.Invoke(this);
            }
            // Only handle editable keyboard pickers
        }

        private void BtnEditCommand_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            ShowCaptureForPicker(PickerCommandPalette);
        }

        private void BtnEditEmergency_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            ShowCaptureForPicker(PickerEmergencyReset);
        }

        private void BtnEditOpacity_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            // Capture a gesture for opacity nudge
            try
            {
                var win = new ShortcutCaptureWindow();
                win.Owner = System.Windows.Window.GetWindow(this);
                // Show current gesture in the capture UI when available
                try { var cur = LblOpacityGesture.Text; if (!string.IsNullOrWhiteSpace(cur)) win.TxtCurrent.Text = cur; } catch { }
                win.ConflictChecker = (cap) =>
                {
                    // check against existing keyboard hotkeys
                    if (!string.IsNullOrWhiteSpace(PickerCommandPalette.Shortcut) && PickerCommandPalette.Shortcut == cap) return true;
                    if (!string.IsNullOrWhiteSpace(PickerEmergencyReset.Shortcut) && PickerEmergencyReset.Shortcut == cap) return true;
                    return false;
                };
                if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.Captured))
                {
                    // Persist gesture label in UI; actual wiring is done in HotkeyManager via settings
                    LblOpacityGesture.Text = win.Captured;
                    UpdateInitialDictFromUi();
                    HotkeysChanged?.Invoke(this);
                }
            }
            catch { }
        }

        private void BtnEditToggle_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            // Capture a gesture for toggle topmost
            try
            {
                var win = new ShortcutCaptureWindow();
                win.Owner = System.Windows.Window.GetWindow(this);
                // Show current gesture in the capture UI when available
                try { var cur = LblToggleGesture.Text; if (!string.IsNullOrWhiteSpace(cur)) win.TxtCurrent.Text = cur; } catch { }
                win.ConflictChecker = (cap) =>
                {
                    if (!string.IsNullOrWhiteSpace(PickerCommandPalette.Shortcut) && PickerCommandPalette.Shortcut == cap) return true;
                    if (!string.IsNullOrWhiteSpace(PickerEmergencyReset.Shortcut) && PickerEmergencyReset.Shortcut == cap) return true;
                    return false;
                };
                if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.Captured))
                {
                    LblToggleGesture.Text = win.Captured;
                    UpdateInitialDictFromUi();
                    HotkeysChanged?.Invoke(this);
                }
            }
            catch { }
        }

        private void ShowCaptureForPicker(ShortcutPicker picker)
        {
            try
            {
                var win = new ShortcutCaptureWindow();
                win.Owner = System.Windows.Window.GetWindow(this);
                // Populate current value in capture UI
                try { var current = picker.Shortcut; if (!string.IsNullOrWhiteSpace(current)) win.TxtCurrent.Text = current; } catch { }
                win.ConflictChecker = (cap) =>
                {
                    // simple conflict check against other pickers
                    if (picker != PickerCommandPalette && !string.IsNullOrWhiteSpace(PickerCommandPalette.Shortcut) && PickerCommandPalette.Shortcut == cap) return true;
                    if (picker != PickerEmergencyReset && !string.IsNullOrWhiteSpace(PickerEmergencyReset.Shortcut) && PickerEmergencyReset.Shortcut == cap) return true;
                    return false;
                };
                if (win.ShowDialog() == true)
                {
                    if (!string.IsNullOrWhiteSpace(win.Captured))
                    {
                        picker.Shortcut = win.Captured;
                        // update any visible label bound to this picker
                        try { if (picker == PickerCommandPalette && this.FindName("LblCommandDisplay") is System.Windows.Controls.TextBlock tb) tb.Text = win.Captured; } catch { }
                        try { if (picker == PickerEmergencyReset && this.FindName("LblEmergencyDisplay") is System.Windows.Controls.TextBlock tb2) tb2.Text = win.Captured; } catch { }
                        ValidateConflicts();
                        UpdateInitialDictFromUi();
                        HotkeysChanged?.Invoke(this);
                    }
                }
            }
            catch { }
        }

        private void UpdateInitialDictFromUi()
        {
            try
            {
                if (_initialDict == null) return;
                var cmd = PickerCommandPalette?.Shortcut;
                var ers = PickerEmergencyReset?.Shortcut;
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    // Use JsonSerializer.Serialize to produce a correctly escaped JSON string literal
                    _initialDict["HotkeyCommandPalette"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(cmd)).RootElement;
                }
                if (!string.IsNullOrWhiteSpace(ers))
                {
                    _initialDict["HotkeyEmergencyReset"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ers)).RootElement;
                }
            }
            catch { }
        }
    }
}
