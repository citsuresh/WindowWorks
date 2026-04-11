using System.Windows.Controls;

namespace WindowWorks.App.UI
{
    public partial class ShortcutsSettingsControl : UserControl
    {
        public event EventHandler? HotkeysChanged;

        public ShortcutsSettingsControl()
        {
            InitializeComponent();
            PickerCommandPalette.ShortcutChanged += OnShortcutChanged;
            PickerEmergencyReset.ShortcutChanged += OnShortcutChanged;
            // Opacity nudge and Toggle Topmost remain mouse gestures; do not expose keyboard shortcuts here.
            try { var b = this.FindName("BtnEditCommand") as System.Windows.Controls.Button; if (b != null) b.Click += BtnEditCommand_Click; } catch { }
            try { var b2 = this.FindName("BtnEditEmergency") as System.Windows.Controls.Button; if (b2 != null) b2.Click += BtnEditEmergency_Click; } catch { }
            try { var b3 = this.FindName("BtnEditOpacity") as System.Windows.Controls.Button; if (b3 != null) b3.Click += BtnEditOpacity_Click; } catch { }
            try { var b4 = this.FindName("BtnEditToggle") as System.Windows.Controls.Button; if (b4 != null) b4.Click += BtnEditToggle_Click; } catch { }
        }

        public void LoadFromSettings(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? d)
        {
            if (d == null) return;
            if (d.TryGetValue("HotkeyCommandPalette", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) PickerCommandPalette.Shortcut = v.GetString();
            if (d.TryGetValue("HotkeyEmergencyReset", out v) && v.ValueKind == System.Text.Json.JsonValueKind.String) PickerEmergencyReset.Shortcut = v.GetString();
            // Opacity nudge and Toggle Topmost are mouse gestures; they are not loaded as keyboard hotkeys.
            ValidateConflicts();
        }

        private void OnHotkeyTextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ValidateConflicts();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
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
                HotkeysChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (sender == PickerEmergencyReset)
            {
                // Handle Emergency Reset shortcut change
                ValidateConflicts();
                HotkeysChanged?.Invoke(this, EventArgs.Empty);
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
                    HotkeysChanged?.Invoke(this, EventArgs.Empty);
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
                win.ConflictChecker = (cap) =>
                {
                    if (!string.IsNullOrWhiteSpace(PickerCommandPalette.Shortcut) && PickerCommandPalette.Shortcut == cap) return true;
                    if (!string.IsNullOrWhiteSpace(PickerEmergencyReset.Shortcut) && PickerEmergencyReset.Shortcut == cap) return true;
                    return false;
                };
                if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.Captured))
                {
                    LblToggleGesture.Text = win.Captured;
                    HotkeysChanged?.Invoke(this, EventArgs.Empty);
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
                        HotkeysChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch { }
        }
    }
}
