using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// Shared, DPI-aware window-rect computation extracted for reuse by both the existing
    /// <see cref="HighlightOverlay"/> (left untouched, per explicit instruction) and the new
    /// picker highlight/box-list windows (docs/REPARENT_FEATURE_PLAN.md §6.2-§6.4). Deliberately
    /// a new, standalone helper rather than a refactor of HighlightOverlay's own inline logic, so
    /// the existing class's behavior is guaranteed unchanged.
    /// </summary>
    internal static class WindowRectHelper
    {
        /// <summary>
        /// Returns the given window's screen rect in WPF DIPs, preferring DWM extended frame
        /// bounds (includes drop shadow/chrome) and falling back to plain GetWindowRect. Returns
        /// false if the window's rect could not be determined (e.g. it has gone away).
        /// </summary>
        public static bool TryGetFrameBoundsDip(IntPtr hwnd, out double left, out double top, out double width, out double height)
        {
            left = top = width = height = 0;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.RECT r;
            try
            {
                int hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<NativeMethods.RECT>());
                if (hr != 0 && !NativeMethods.GetWindowRect(hwnd, out r))
                {
                    return false;
                }
            }
            catch
            {
                if (!NativeMethods.GetWindowRect(hwnd, out r))
                {
                    return false;
                }
            }

            int pixelWidth = Math.Max(1, r.Right - r.Left);
            int pixelHeight = Math.Max(1, r.Bottom - r.Top);

            double dpiX = 96.0, dpiY = 96.0;
            try
            {
                uint dpi = NativeMethods.GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    dpiX = dpi;
                    dpiY = dpi;
                }
            }
            catch { }

            left = r.Left * 96.0 / dpiX;
            top = r.Top * 96.0 / dpiY;
            width = pixelWidth * 96.0 / dpiX;
            height = pixelHeight * 96.0 / dpiY;
            return true;
        }

        /// <summary>
        /// Converts an arbitrary screen-pixel rect (e.g. a UI Automation DOM element's
        /// <c>BoundingRectangle</c>, already clipped via a rect-clip helper) to WPF DIPs, using
        /// <paramref name="dpiReferenceHwnd"/> (typically the containing browser window) for the
        /// per-monitor DPI lookup — mirrors <see cref="TryGetFrameBoundsDip"/>'s pixel-to-DIP
        /// conversion, but for a rect that has no HWND of its own to query directly.
        /// </summary>
        public static void ScreenRectToDip(
            IntPtr dpiReferenceHwnd,
            int screenLeft,
            int screenTop,
            int screenRight,
            int screenBottom,
            out double left,
            out double top,
            out double width,
            out double height)
        {
            double dpi = 96.0;
            try
            {
                uint rawDpi = NativeMethods.GetDpiForWindow(dpiReferenceHwnd);
                if (rawDpi > 0)
                {
                    dpi = rawDpi;
                }
            }
            catch { }

            double scale = 96.0 / dpi;
            left = screenLeft * scale;
            top = screenTop * scale;
            width = Math.Max(1, (screenRight - screenLeft)) * scale;
            height = Math.Max(1, (screenBottom - screenTop)) * scale;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
            [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);

            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);
            public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        }
    }
}
