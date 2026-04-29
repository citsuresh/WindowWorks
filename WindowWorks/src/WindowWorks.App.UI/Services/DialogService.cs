using System;
using System.Windows.Forms;

namespace WindowWorks.App.UI.Services
{
    public class DialogService : IDialogService
    {
        public string? ShowColorPicker(string? initialHex)
        {
            try
            {
                using var dlg = new ColorDialog();
                if (!string.IsNullOrWhiteSpace(initialHex))
                {
                    try
                    {
                        var c = System.Drawing.ColorTranslator.FromHtml(initialHex);
                        dlg.Color = c;
                    }
                    catch { }
                }
                var res = dlg.ShowDialog();
                if (res == DialogResult.OK)
                {
                    var c = dlg.Color;
                    return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
                }
            }
            catch { }
            return null;
        }

        public string? ShowShortcutCapture(string? current)
        {
            try
            {
                var win = new ShortcutCaptureWindow();
                try { if (!string.IsNullOrWhiteSpace(current)) win.TxtCurrent.Text = current; } catch { }

                // Prefer to center the capture window over the window that currently has focus
                try
                {
                    // Determine the most appropriate owner: the focused element's window, or the application's MainWindow as fallback
                    System.Windows.Window? owner = null;
                    var focused = System.Windows.Input.Keyboard.FocusedElement as System.Windows.DependencyObject;
                    if (focused != null)
                    {
                        owner = System.Windows.Window.GetWindow(focused);
                    }
                    if (owner == null)
                    {
                        owner = System.Windows.Application.Current?.MainWindow;
                    }

                    if (owner != null)
                    {
                        win.Owner = owner;
                        win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
                    }
                    else
                    {
                        // If no owner found, center on screen
                        win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
                    }
                }
                catch { /* best-effort positioning - ignore errors */ }

                var ok = win.ShowDialog();
                if (ok == true) return win.Captured;
            }
            catch { }
            return null;
        }
    }
}
