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
                // Set owner to the WPF application's main window if available
                win.Owner = System.Windows.Application.Current?.MainWindow;
                var ok = win.ShowDialog();
                if (ok == true) return win.Captured;
            }
            catch { }
            return null;
        }
    }
}
