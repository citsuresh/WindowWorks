using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowWorks.App.UI
{
    public partial class ShortcutPicker : UserControl
    {
        public event EventHandler<string>? ShortcutChanged;
        public event EventHandler? EditRequested;

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
            if ((modifiers & ModifierKeys.Control) != 0) shortcut += "Ctrl+";
            if ((modifiers & ModifierKeys.Alt) != 0) shortcut += "Alt+";
            if ((modifiers & ModifierKeys.Shift) != 0) shortcut += "Shift+";
            if ((modifiers & ModifierKeys.Windows) != 0) shortcut += "Win+";

            shortcut += key.ToString();

            // update both textbox (if present) and display label
            try { if (this.FindName("ShortcutTextBox") is TextBox tb) tb.Text = shortcut; } catch { }
            try { if (this.FindName("DisplayText") is TextBlock dt) dt.Text = shortcut; } catch { }
            ShortcutChanged?.Invoke(this, shortcut);
        }

        public string Shortcut
        {
            get
            {
                try { if (this.FindName("ShortcutTextBox") is TextBox tb) return tb.Text; } catch { }
                try { if (this.FindName("DisplayText") is TextBlock dt) return dt.Text; } catch { }
                return string.Empty;
            }
            set
            {
                try { if (this.FindName("ShortcutTextBox") is TextBox tb) tb.Text = value; } catch { }
                try { if (this.FindName("DisplayText") is TextBlock dt) dt.Text = value; } catch { }
                // Fire change only when setter used programmatically
                try { ShortcutChanged?.Invoke(this, value ?? string.Empty); } catch { }
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}