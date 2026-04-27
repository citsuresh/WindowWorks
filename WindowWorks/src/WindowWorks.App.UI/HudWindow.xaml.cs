using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace WindowWorks.App.UI
{
    public partial class HudWindow : Window
    {
        public HudWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Show the HUD pinned to the bottom-right corner of the primary screen with given margins (in physical pixels).
        /// Keeps the window topmost without activating it.
        /// </summary>
        public void ShowBottomRight(int marginRight = 20, int marginBottom = 40)
        {
            Dispatcher.Invoke(() =>
            {
                if (!IsVisible) Show();
                UpdateLayout();
                try
                {
                    // Get primary screen working area in physical pixels
                    var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    {
                        float dpiX = g.DpiX;
                        float dpiY = g.DpiY;
                        double rightDip = wa.Right * 96.0 / dpiX;
                        double bottomDip = wa.Bottom * 96.0 / dpiY;
                        double w = ActualWidth;
                        double h = ActualHeight;
                        Left = Math.Max(0, rightDip - marginRight * 96.0 / dpiX - w);
                        Top = Math.Max(0, bottomDip - marginBottom * 96.0 / dpiY - h);
                    }
                }
                catch
                {
                    // fallback placement
                    Left = SystemParameters.WorkArea.Width - ActualWidth - 20;
                    Top = SystemParameters.WorkArea.Height - ActualHeight - 40;
                }
            });
        }

        private void UndoButton_Click(object? sender, RoutedEventArgs e)
        {
            try { ResetCloseTimer(); } catch { }
            OnUndo?.Invoke();
        }

        private void ResetButton_Click(object? sender, RoutedEventArgs e)
        {
            try { ResetCloseTimer(); } catch { }
            OnReset?.Invoke();
        }

        public Action? OnUndo { get; set; }
        public Action? OnReset { get; set; }

        public void UpdateMessage(string text)
        {
            Dispatcher.Invoke(() => MessageText.Text = text);
        }

        public void UpdateProgress(int percent)
        {
            Dispatcher.Invoke(() => {
                int v = Math.Max(0, Math.Min(100, percent));
                OpacityBar.Value = v;
                PercentText.Text = v + "%";
            });
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CYCAPTION = 4;
        private const int SM_CYFRAME = 32;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// Show HUD at screen pixel coordinates (x,y). If sourceWindow is provided, use its DPI for conversion
        /// to WPF device-independent units so positioning is correct on per-monitor DPI setups.
        /// If cursorX is provided, the HUD will horizontally center around that cursor X position.
        /// </summary>
        public void ShowAt(int x, int y, IntPtr? sourceWindow = null, int? cursorX = null)
        {
            Dispatcher.Invoke(() => {
                // Adjust Y to sit below the window titlebar so HUD doesn't overlap the caption.
                try
                {
                    int caption = GetSystemMetrics(SM_CYCAPTION);
                    int frame = GetSystemMetrics(SM_CYFRAME);
                    int extra = 18; // increased gap to ensure HUD sits below caption
                    y += caption + frame + extra;
                }
                catch { }

                // Convert from physical screen pixels (GetWindowRect coordinates) to WPF device-independent units (DIP)
                // DIP = pixels * 96 / dpi
                double dipX = x;
                double dipY = y;
                try
                {
                    // Prefer per-window DPI when available (Windows 10+)
                    if (sourceWindow.HasValue && sourceWindow.Value != IntPtr.Zero)
                    {
                        try
                        {
                            uint dpi = GetDpiForWindow(sourceWindow.Value);
                            if (dpi > 0)
                            {
                                dipX = x * 96.0 / dpi;
                                dipY = y * 96.0 / dpi;
                            }
                        }
                        catch { /* fallback to system DPI */ }
                    }

                    if (dipX == x && dipY == y)
                    {
                        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                        {
                            float dpiX = g.DpiX; // typically 96, 120, etc.
                            float dpiY = g.DpiY;
                            dipX = x * 96.0 / dpiX;
                            dipY = y * 96.0 / dpiY;
                        }
                    }
                }
                catch
                {
                    dipX = x; dipY = y;
                }

                // Set vertical position to content-top converted to DIP
                Top = dipY;

                if (!IsVisible) Show();
                // ensure layout so ActualWidth is valid
                UpdateLayout();

                // If a cursorX was provided, center horizontally around the cursor (converted to DIP)
                if (cursorX.HasValue)
                {
                    double dipCursorX = cursorX.Value;
                    try
                    {
                        if (sourceWindow.HasValue && sourceWindow.Value != IntPtr.Zero)
                        {
                            uint dpi = GetDpiForWindow(sourceWindow.Value);
                            if (dpi > 0)
                            {
                                dipCursorX = cursorX.Value * 96.0 / dpi;
                            }
                        }
                        else
                        {
                            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                            {
                                float dpiX = g.DpiX;
                                dipCursorX = cursorX.Value * 96.0 / dpiX;
                            }
                        }
                    }
                    catch { dipCursorX = cursorX.Value; }

                    double w = ActualWidth;
                    Left = dipCursorX - (w / 2.0);
                }
                else
                {
                    Left = dipX;
                }

                // avoid stealing focus from the target window; keep ShowActivated false
            });
        }

        /// <summary>
        /// Apply visual settings: background color, font size and corner radius.
        /// Colors may come from system accent or custom hex strings.
        /// Safe to call from any thread.
        /// </summary>
        public void ApplyVisuals(string? backgroundColorHex, int fontSize, int cornerRadius)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(backgroundColorHex))
                        {
                            var bc = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(backgroundColorHex);
                            (this.Content as System.Windows.Controls.Border)!.Background = bc;
                        }
                    }
                    catch { }
                    try { MessageText.FontSize = fontSize; } catch { }
                    try { ((this.Content as System.Windows.Controls.Border)!).CornerRadius = new CornerRadius(cornerRadius); } catch { }
                });
            }
            catch { }
        }

        public void ShowTransient(int timeoutMs = 1000)
        {
            Dispatcher.Invoke(() => {
                if (!IsVisible) Show();
                // start a simple timer to close
                // reset any existing timer
                _closeTimer?.Stop();
                _closeTimer?.Dispose();
                _closeTimer = new System.Timers.Timer(timeoutMs) { AutoReset = false };
                _closeTimer.Elapsed += (s, e) => Dispatcher.Invoke(() => { _closeTimer.Dispose(); _closeTimer = null; Close(); });
                _closeTimer.Start();
            });
        }

        /// <summary>
        /// Ensure this HUD window is topmost above other topmost windows without activating it.
        /// </summary>
        public void EnsureTopmost()
        {
            Dispatcher.Invoke(() => {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeSet.SetWindowPos(hwnd, NativeSet.HWND_TOPMOST, 0, 0, 0, 0, NativeSet.SWP_NOMOVE | NativeSet.SWP_NOSIZE | NativeSet.SWP_NOACTIVATE | NativeSet.SWP_SHOWWINDOW);
                    }
                }
                catch { }
            });
        }

        // allow external callers to reset the transient close timer
        public void ResetCloseTimer(int timeoutMs = 1000)
        {
            Dispatcher.Invoke(() => {
                if (_closeTimer != null)
                {
                    _closeTimer.Stop();
                    _closeTimer.Interval = timeoutMs;
                    _closeTimer.Start();
                }
            });
        }

        private System.Timers.Timer? _closeTimer;

        private static class NativeSet
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_SHOWWINDOW = 0x0040;
        }
    }
}
