using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using WindowWorks.App.UI.Services;

namespace WindowWorks.App.UI
{
    public partial class ShortcutPicker : UserControl
    {
        private System.Threading.CancellationTokenSource? _warningCts;
        public event EventHandler<string>? ShortcutChanged;
        public event EventHandler? EditRequested;
        public static readonly System.Windows.DependencyProperty ShortcutProperty = System.Windows.DependencyProperty.Register(
            nameof(Shortcut), typeof(string), typeof(ShortcutPicker), new System.Windows.PropertyMetadata(string.Empty, OnShortcutPropertyChanged));

        public string Shortcut
        {
            get => (string)GetValue(ShortcutProperty);
            set => SetValue(ShortcutProperty, value);
        }

        private static T? FindAncestorOfType<T>(DependencyObject? start) where T : DependencyObject
        {
            while (start != null)
            {
                if (start is T t) return t;
                start = System.Windows.Media.VisualTreeHelper.GetParent(start);
            }
            return null;
        }

        private static void CollectShortcutPickers(DependencyObject root, System.Collections.Generic.List<ShortcutPicker> outList)
        {
            if (root == null) return;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ShortcutPicker sp) outList.Add(sp);
                CollectShortcutPickers(child, outList);
            }
        }

        // Attempt to register a test hotkey to detect system-wide conflict for keyboard combos.
        private bool IsSystemHotkeyConflict(string shortcut)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shortcut)) return false;
                if (shortcut.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0 || shortcut.IndexOf("Click", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

                var parts = shortcut.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
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

                if (!Enum.TryParse<System.Windows.Input.Key>(keyToken, true, out var keyEnum))
                {
                    if (keyToken.Length == 1)
                    {
                        var c = keyToken[0];
                        if (char.IsLetterOrDigit(c))
                        {
                            var upper = char.ToUpperInvariant(c);
                            if (!Enum.TryParse<System.Windows.Input.Key>(upper.ToString(), out keyEnum)) return false;
                        }
                        else return false;
                    }
                    else return false;
                }

                int vk = KeyInterop.VirtualKeyFromKey(keyEnum);
                var helper = new System.Windows.Interop.WindowInteropHelper(System.Windows.Window.GetWindow(this));
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return false;
                int id = Math.Abs(shortcut.GetHashCode());
                if (RegisterHotKey(hwnd, id, mods, (uint)vk))
                {
                    UnregisterHotKey(hwnd, id);
                    return false;
                }
                else return true;
            }
            catch { return false; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private static void OnShortcutPropertyChanged(System.Windows.DependencyObject d, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (d is ShortcutPicker sp)
            {
                var newVal = e.NewValue as string ?? string.Empty;
                try { if (sp.FindName("ShortcutTextBox") is TextBox tb) tb.Text = newVal; } catch { }
                try { if (sp.FindName("DisplayText") is TextBlock dt) dt.Text = newVal; } catch { }
                try { sp.ShortcutChanged?.Invoke(sp, newVal); } catch { }
            }
        }

        public ShortcutPicker()
        {
            InitializeComponent();
            // The visual display is handled by DisplayText in XAML. PreviewKeyDown is wired in XAML now.
            try { if (this.FindName("DisplayText") is TextBlock dt) { /* no-op, display-only */ } } catch { }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // Prevent default handling

            var modifiers = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LWin || key == Key.RWin)
            {
                return; // Ignore modifier keys alone
            }

            string shortcut = "";
            // Keyboard.Modifiers may not reliably report the Windows key; explicitly check both LWin/RWin as well.
            bool winDown = (modifiers & ModifierKeys.Windows) != 0 || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
            if ((modifiers & ModifierKeys.Control) != 0) shortcut += "Ctrl+";
            if ((modifiers & ModifierKeys.Alt) != 0) shortcut += "Alt+";
            if ((modifiers & ModifierKeys.Shift) != 0) shortcut += "Shift+";
            if (winDown) shortcut += "Win+";

            shortcut += key.ToString();

            // update both textbox (if present) and display label
            try { if (this.FindName("ShortcutTextBox") is TextBox tb) tb.Text = shortcut; } catch { }
            try { if (this.FindName("DisplayText") is TextBlock dt) dt.Text = shortcut; } catch { }
            try { Shortcut = shortcut; } catch { }
            // Check for conflicts: sibling pickers and system-wide registration
            try
            {
                bool conflict = false;
                // Check sibling ShortcutPicker controls under the same ShortcutsSettingsControl
                var sc = FindAncestorOfType<ShortcutsSettingsControl>(this);
                if (sc != null)
                {
                    var siblings = new System.Collections.Generic.List<ShortcutPicker>();
                    CollectShortcutPickers(sc, siblings);
                    foreach (var s in siblings)
                    {
                        if (!object.ReferenceEquals(s, this) && !string.IsNullOrWhiteSpace(s.Shortcut) && s.Shortcut == shortcut)
                        {
                            conflict = true; break;
                        }
                    }
                }

                // If no sibling conflict, check system-wide hotkey conflict for keyboard combos
                if (!conflict)
                {
                    if (IsSystemHotkeyConflict(shortcut)) conflict = true;
                }

                if (conflict)
                {
                    ShowTemporaryWarning("This key combination is already being used by an application.");
                }
            }
            catch { }
        }

        // Shortcut dependency property implemented above

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }

        // Display a temporary warning message in the attached WarningText control
        private void ShowTemporaryWarning(string message, int milliseconds = 2500)
        {
            try
            {
                _warningCts?.Cancel();
                _warningCts?.Dispose();
                _warningCts = new System.Threading.CancellationTokenSource();
                var token = _warningCts.Token;
                if (this.FindName("WarningText") is TextBlock wt)
                {
                    wt.Text = message;
                    wt.Visibility = Visibility.Visible;
                }
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(milliseconds, token).ConfigureAwait(false);
                        if (!token.IsCancellationRequested)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                try { if (this.FindName("WarningText") is TextBlock wt2) { wt2.Visibility = Visibility.Collapsed; wt2.Text = string.Empty; } } catch { }
                            });
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }
    }
}