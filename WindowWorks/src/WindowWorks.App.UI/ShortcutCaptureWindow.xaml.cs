using System;
using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WindowWorks.App.UI
{
    public partial class ShortcutCaptureWindow : Window
    {
        public string Captured { get; private set; } = string.Empty;
        public bool IsConflict { get; set; }
        public Func<string, bool>? ConflictChecker { get; set; }
        private System.Windows.Threading.DispatcherTimer? _conflictTimer;

        public ShortcutCaptureWindow()
        {
            InitializeComponent();
            // Timer to auto-hide the conflict warning after a short duration
            try
            {
                _conflictTimer = new System.Windows.Threading.DispatcherTimer(System.TimeSpan.FromSeconds(3), System.Windows.Threading.DispatcherPriority.Normal, (s, e) =>
                {
                    try
                    {
                        TxtConflict.Visibility = Visibility.Collapsed;
                        _conflictTimer?.Stop();
                    }
                    catch { }
                }, this.Dispatcher);
                _conflictTimer.Stop();
            }
            catch { }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
            {
                return;
            }
            string s = "";
            if ((mods & ModifierKeys.Control) != 0) s += "Ctrl+";
            if ((mods & ModifierKeys.Alt) != 0) s += "Alt+";
            if ((mods & ModifierKeys.Shift) != 0) s += "Shift+";
            if ((mods & ModifierKeys.Windows) != 0) s += "Win+";
            s += key.ToString();
            Captured = s;
            TxtCaptured.Text = Captured;
            UpdateConflict();
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == 0) return;
            try
            {
                string s = "";
                if ((mods & ModifierKeys.Control) != 0) s += "Ctrl+";
                if ((mods & ModifierKeys.Alt) != 0) s += "Alt+";
                if ((mods & ModifierKeys.Shift) != 0) s += "Shift+";
                if ((mods & ModifierKeys.Windows) != 0) s += "Win+";
                s += "MouseWheel" + (e.Delta > 0 ? "+Up" : "+Down");
                Captured = s;
                TxtCaptured.Text = Captured;
                UpdateConflict();
                e.Handled = true;
            }
            catch { }
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == 0) return;
            try
            {
                string s = "";
                if ((mods & ModifierKeys.Control) != 0) s += "Ctrl+";
                if ((mods & ModifierKeys.Alt) != 0) s += "Alt+";
                if ((mods & ModifierKeys.Shift) != 0) s += "Shift+";
                if ((mods & ModifierKeys.Windows) != 0) s += "Win+";
                string btn = e.ChangedButton == MouseButton.Left ? "Click" : e.ChangedButton.ToString();
                s += btn;
                Captured = s;
                TxtCaptured.Text = Captured;
                UpdateConflict();
                e.Handled = true;
            }
            catch { }
        }

        private void UpdateConflict()
        {
            bool conflict = IsConflict;
            try { if (ConflictChecker != null && !string.IsNullOrEmpty(Captured)) conflict = ConflictChecker(Captured); } catch { }
            // Also check system-wide conflict for keyboard shortcuts
            try { if (!conflict && !string.IsNullOrEmpty(Captured)) conflict = DetectSystemHotkeyConflict(Captured); } catch { }
            if (conflict)
            {
                try
                {
                    TxtConflict.Text = "The key combination is already being used by an application.";
                    TxtConflict.Visibility = Visibility.Visible;
                    // restart auto-hide timer
                    try { _conflictTimer?.Stop(); _conflictTimer?.Start(); } catch { }
                }
                catch { }
            }
            else
            {
                TxtConflict.Visibility = Visibility.Collapsed;
                try { _conflictTimer?.Stop(); } catch { }
            }
            // Only enable OK when there is a captured gesture and it is not conflicting
            BtnOk.IsEnabled = !string.IsNullOrEmpty(Captured) && !conflict;
        }

        // Attempt to detect system-wide registration conflicts for keyboard shortcuts.
        // Returns true when a conflict exists (i.e., RegisterHotKey would fail).
        private bool DetectSystemHotkeyConflict(string gesture)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gesture)) return false;
                // Only support keyboard combinations for system registration check
                if (gesture.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0 || gesture.IndexOf("Click", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Cannot test mouse gestures with RegisterHotKey; assume no system conflict
                    return false;
                }

                var parts = gesture.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return false;
                uint mods = 0;
                string keyToken = parts[parts.Length - 1].Trim();
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var t = parts[i].Trim();
                    if (string.Equals(t, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Control", StringComparison.OrdinalIgnoreCase)) mods |= 0x0002; // MOD_CONTROL
                    else if (string.Equals(t, "Alt", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Menu", StringComparison.OrdinalIgnoreCase)) mods |= 0x0001; // MOD_ALT
                    else if (string.Equals(t, "Shift", StringComparison.OrdinalIgnoreCase)) mods |= 0x0004; // MOD_SHIFT
                    else if (string.Equals(t, "Win", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Windows", StringComparison.OrdinalIgnoreCase)) mods |= 0x0008; // MOD_WIN
                }

                // Parse key token to System.Windows.Input.Key
                if (!Enum.TryParse<System.Windows.Input.Key>(keyToken, true, out var keyEnum))
                {
                    // Try single char
                    if (keyToken.Length == 1)
                    {
                        var c = keyToken[0];
                        // Map letter/digit
                        if (char.IsLetterOrDigit(c))
                        {
                            var upper = char.ToUpperInvariant(c);
                            if (Enum.TryParse<System.Windows.Input.Key>(upper.ToString(), out keyEnum) == false)
                                return false;
                        }
                        else return false;
                    }
                    else return false;
                }

                int vk = KeyInterop.VirtualKeyFromKey(keyEnum);
                var helper = new WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return false;

                int id = Math.Abs(gesture.GetHashCode());
                if (RegisterHotKey(hwnd, id, mods, (uint)vk))
                {
                    // success -> no conflict; unregister and return false
                    UnregisterHotKey(hwnd, id);
                    return false;
                }
                else
                {
                    // failure -> conflict
                    return true;
                }
            }
            catch { return false; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
