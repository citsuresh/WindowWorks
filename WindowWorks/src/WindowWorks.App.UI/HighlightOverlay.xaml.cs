using System;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Controls;

namespace WindowWorks.App.UI
{
    public partial class HighlightOverlay : Window
    {
        private System.Timers.Timer? _closeTimer;

        public HighlightOverlay()
        {
            InitializeComponent();
            // allow per-pixel transparency so the window background can be fully transparent
            try { this.AllowsTransparency = true; } catch { }
            try { this.Background = System.Windows.Media.Brushes.Transparent; } catch { }
            // keep Border as visual fallback; window region will create the hollow border
            try { BorderHighlight.Background = System.Windows.Media.Brushes.Transparent; } catch { }
            BorderHighlight.Visibility = Visibility.Collapsed;
            this.Loaded += (_, __) => {
                // ensure window is toolwindow so it doesn't appear in alt-tab
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                    // Set toolwindow and transparent so overlay doesn't participate in alt-tab and is click-through
                    ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
                    NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
                }
            };
        }

        public void ShowAround(IntPtr hwnd, int durationMs = 1000)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                // Prefer DWM extended frame bounds (includes drop shadow and chrome). Fall back to GetWindowRect.
                NativeMethods.RECT r;
                int hr = 0;
                try
                {
                    hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<NativeMethods.RECT>());
                    if (hr != 0)
                    {
                        // fallback
                        if (!NativeMethods.GetWindowRect(hwnd, out r)) return;
                    }
                }
                catch
                {
                    if (!NativeMethods.GetWindowRect(hwnd, out r)) return;
                }

                // Get device pixel sizes directly from window rect (pixels)
                int pixelLeft = r.Left;
                int pixelTop = r.Top;
                int pixelWidth = Math.Max(1, r.Right - r.Left);
                int pixelHeight = Math.Max(1, r.Bottom - r.Top);

                // Convert pixel sizes to WPF DIPs for layout (DIPs = pixels * 96 / dpi)
                double dpiX = 96.0, dpiY = 96.0;
                try
                {
                    // Prefer per-window DPI when available to avoid scaling mismatch on multi-monitor setups
                    try
                    {
                        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
                        if (dpi > 0)
                        {
                            dpiX = dpi; dpiY = dpi;
                        }
                        else
                        {
                            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) { dpiX = g.DpiX; dpiY = g.DpiY; }
                        }
                    }
                    catch
                    {
                        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) { dpiX = g.DpiX; dpiY = g.DpiY; }
                    }
                }
                catch { }

                double left = pixelLeft * 96.0 / dpiX;
                double top = pixelTop * 96.0 / dpiY;
                double width = pixelWidth * 96.0 / dpiX;
                double height = pixelHeight * 96.0 / dpiY;

                // Position overlay window to match target window in screen DIPs
                // Expand by half the border thickness so the stroke centers on the window edge
                double borderThicknessDip = BorderHighlight.BorderThickness.Left;
                double expandDip = borderThicknessDip / 2.0;
                // Clamp expansion to avoid excessive overhang
                double maxExpand = Math.Min(10, Math.Max(0, borderThicknessDip));
                double effectiveExpand = Math.Min(expandDip, maxExpand);

                Left = left - effectiveExpand;
                Top = top - effectiveExpand;
                Width = Math.Max(1, width + (effectiveExpand * 2.0));
                Height = Math.Max(1, height + (effectiveExpand * 2.0));

                // Border element should fill the overlay window; offset so it surrounds the original target bounds
                BorderHighlight.Width = Width;
                BorderHighlight.Height = Height;
                Canvas.SetLeft(BorderHighlight, 0);
                Canvas.SetTop(BorderHighlight, 0);
                try
                {
                    // Improve crispness on device pixels
                    BorderHighlight.SnapsToDevicePixels = true;
                    BorderHighlight.UseLayoutRounding = true;
                    if (BorderHighlight.BorderBrush == null)
                    {
                        BorderHighlight.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0x00));
                    }
                    BorderHighlight.Visibility = Visibility.Visible;
                }
                catch { }

                // pick border thickness and corner radius from control
                int thickness = (int)Math.Max(1, BorderHighlight.BorderThickness.Left);
                int corner = (int)Math.Max(0, BorderHighlight.CornerRadius.TopLeft);

                // If AllowsTransparency is enabled (layered WPF window), avoid SetWindowRgn which can
                // conflict with per-pixel alpha windows. Use the Border element (transparent background)
                // so only the stroke is visible. Otherwise fall back to region-based hollow rect.
                try
                {
                    var hwndOverlay = new WindowInteropHelper(this).Handle;
                    if (hwndOverlay != IntPtr.Zero)
                    {
                        if (this.AllowsTransparency)
                        {
                            // ensure fully transparent background and show border-only visual
                            try { this.Background = System.Windows.Media.Brushes.Transparent; } catch { }
                            try { BorderHighlight.Background = System.Windows.Media.Brushes.Transparent; } catch { }
                            BorderHighlight.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            int w = (int)Math.Max(1, Math.Round(Width));
                            int h = (int)Math.Max(1, Math.Round(Height));
                            IntPtr outer = Gdi.CreateRoundRectRgn(0, 0, w + 1, h + 1, corner * 2 + 1, corner * 2 + 1);
                            IntPtr inner = Gdi.CreateRoundRectRgn(thickness, thickness, Math.Max(1, w - thickness) + 1, Math.Max(1, h - thickness) + 1, Math.Max(1, (corner - thickness) * 2), Math.Max(1, (corner - thickness) * 2));

                            // subtract inner from outer
                            int combineRes = Gdi.CombineRgn(outer, outer, inner, Gdi.RGN_DIFF);
                            if (combineRes != 0)
                            {
                                // SetWindowRgn takes ownership of the region passed in (outer)
                                User32.SetWindowRgn(hwndOverlay, outer, true);
                            }
                            else
                            {
                                // fallback: delete outer
                                Gdi.DeleteObject(outer);
                            }

                            // delete inner region handle
                            Gdi.DeleteObject(inner);
                            BorderHighlight.Visibility = Visibility.Visible;
                        }
                    }
                }
                catch { }

                if (!IsVisible) Show();
                // Place overlay just behind the target window by setting Z order to just below target
                try
                {
                    var hwndOverlay = new WindowInteropHelper(this).Handle;
                    if (hwndOverlay != IntPtr.Zero)
                    {
                        try
                        {
                            // Make overlay topmost so it can be positioned relative to topmost target windows,
                            // then insert it just after the target in Z-order so it sits directly behind it.
                            NativeSet.SetWindowPos(hwndOverlay, NativeSet.HWND_TOPMOST, 0, 0, 0, 0,
                                NativeSet.SWP_NOMOVE | NativeSet.SWP_NOSIZE | NativeSet.SWP_NOACTIVATE | NativeSet.SWP_SHOWWINDOW);

                            // Insert after target hwnd to place overlay immediately behind it.
                            NativeSet.SetWindowPos(hwndOverlay, hwnd, 0, 0, 0, 0,
                                NativeSet.SWP_NOMOVE | NativeSet.SWP_NOSIZE | NativeSet.SWP_NOACTIVATE | NativeSet.SWP_SHOWWINDOW);

                            // ensure border is visible and slightly thicker when rendered behind
                            try { BorderHighlight.BorderThickness = new Thickness(Math.Max(2, BorderHighlight.BorderThickness.Left)); } catch { }
                            try { BorderHighlight.Opacity = 1.0; } catch { }
                        }
                        catch { }
                    }
                }
                catch { }

                // (re)start close timer
                StartOrRestartTimer(durationMs);
            }
            catch { }
        }

        private void StartOrRestartTimer(int durationMs)
        {
            try
            {
                // Sanity-check duration (ms). Prevent accidental very large values from settings or callers.
                const int MinMs = 100; // minimum show time
                // Allow a configurable maximum, respect app settings when available.
                int MaxMs = 1000; // default hard cap 1s
                try
                {
                    // Read persisted settings.json from %APPDATA%/WindowWorks and parse HighlightMaxDurationMs if present
                    var appFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowWorks");
                    var settingsPath = System.IO.Path.Combine(appFolder, "settings.json");
                    if (System.IO.File.Exists(settingsPath))
                    {
                        var json = System.IO.File.ReadAllText(settingsPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("HighlightMaxDurationMs", out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            if (prop.TryGetInt32(out var v) && v > 0) MaxMs = v;
                        }
                    }
                }
                catch { }
                if (durationMs < MinMs) durationMs = MinMs;
                if (durationMs > MaxMs) durationMs = MaxMs;

                if (_closeTimer == null)
                {
                    _closeTimer = new System.Timers.Timer(durationMs) { AutoReset = false };
                    _closeTimer.Elapsed += (s, e) => Dispatcher.Invoke(() => CloseOverlay());
                    _closeTimer.Start();
                }
                else
                {
                    _closeTimer.Stop();
                    _closeTimer.Interval = durationMs;
                    _closeTimer.Start();
                }
            }
            catch { }
        }

        private void CloseOverlay()
        {
            try { BorderHighlight.Visibility = Visibility.Collapsed; } catch { }
            try { _closeTimer?.Dispose(); _closeTimer = null; } catch { }
            try
            {
                var hwndOverlay = new WindowInteropHelper(this).Handle;
                if (hwndOverlay != IntPtr.Zero)
                {
                    // remove any window region
                    User32.SetWindowRgn(hwndOverlay, IntPtr.Zero, true);
                    // restore WS_EX_LAYERED removed earlier so future windows are not affected
                    try
                    {
                        int ex = NativeMethods.GetWindowLong(hwndOverlay, NativeMethods.GWL_EXSTYLE);
                        ex &= ~NativeMethods.WS_EX_TOOLWINDOW;
                        NativeMethods.SetWindowLong(hwndOverlay, NativeMethods.GWL_EXSTYLE, ex);
                    }
                    catch { }
                }
            }
            catch { }
            try { Close(); } catch { }
            try { Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); } catch { }
        }

        /// <summary>
        /// Extend or reset the overlay lifetime to the provided duration (ms).
        /// Safe to call from other threads; will marshal to overlay dispatcher.
        /// </summary>
        public void Extend(int durationMs)
        {
            try
            {
                Dispatcher.Invoke(() => StartOrRestartTimer(durationMs));
            }
            catch { }
        }

        // Visual setters used by WindowManager to apply configured settings
        public void SetBorderColor(string argbOrRgb)
        {
            try
            {
                Dispatcher.Invoke(() => {
                    try
                    {
                        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(argbOrRgb);
                        BorderHighlight.BorderBrush = new System.Windows.Media.SolidColorBrush(c);
                    }
                    catch { }
                });
            }
            catch { }
        }

        public void SetBorderThickness(int thickness)
        {
            try { Dispatcher.Invoke(() => BorderHighlight.BorderThickness = new Thickness(Math.Max(1, thickness))); } catch { }
        }

        public void SetCornerRadius(int radius)
        {
            try { Dispatcher.Invoke(() => BorderHighlight.CornerRadius = new CornerRadius(Math.Max(0, radius))); } catch { }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
            [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);

            // DWM extended frame bounds for accurate outer window chrome (includes drop shadows)
            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);
            public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const int WS_EX_TRANSPARENT = 0x00000020;
            public const int WS_EX_LAYERED = 0x00080000;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        }

        private static class User32
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        }

        private static class Gdi
        {
            public const int RGN_DIFF = 4;
            [DllImport("gdi32.dll")]
            public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);
            [DllImport("gdi32.dll")]
            public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);
            [DllImport("gdi32.dll")]
            public static extern bool DeleteObject(IntPtr hObject);
        }

        private static class NativeSet
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_SHOWWINDOW = 0x0040;
            public const uint SWP_NOACTIVATE = 0x0010;
        }
    }
}
