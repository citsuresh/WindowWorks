using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// One ancestor-chain entry as shown in the yellow-box confirm list (§6.3): nearest-to-
    /// farthest ordering (deepest hovered child first, root last), each labeled with class name /
    /// title / size.
    /// </summary>
    public sealed class PickerAncestorBoxItem
    {
        public IntPtr Hwnd { get; }
        public string Label { get; }
        public bool IsChildHwndPick { get; }

        public PickerAncestorBoxItem(IntPtr hwnd, string label, bool isChildHwndPick)
        {
            Hwnd = hwnd;
            Label = label;
            IsChildHwndPick = isChildHwndPick;
        }
    }

    /// <summary>
    /// Yellow-box list confirm UI (§6.3) for the ancestor-chain window picker. A small, real,
    /// input-owning window (not a full-screen overlay) positioned adaptively near the cursor
    /// (§6.4) — hovering a box re-highlights the corresponding on-screen rect via
    /// <see cref="BoxHovered"/>; clicking a box is the actual confirm/commit gesture via
    /// <see cref="BoxConfirmed"/>. No "Crop a region instead" entry — crop mode is Phase 2, out of
    /// scope here.
    /// </summary>
    public partial class PickerBoxListWindow : Window
    {
        public event EventHandler<PickerAncestorBoxItem>? BoxHovered;
        public event EventHandler<PickerAncestorBoxItem>? BoxConfirmed;

        public PickerBoxListWindow()
        {
            InitializeComponent();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                    // Toolwindow only — deliberately NOT WS_EX_TRANSPARENT, this window must
                    // receive real mouse input for the click-to-confirm gesture (§6.3).
                    ex |= NativeMethods.WS_EX_TOOLWINDOW;
                    NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
                }
            }
            catch { }
        }

        public void SetItems(IReadOnlyList<PickerAncestorBoxItem> items)
        {
            BoxList.ItemsSource = items;
        }

        /// <summary>
        /// Adaptive box-stack positioning (§6.4): anchors the box list INSIDE the currently
        /// highlighted window's own on-screen rect, overlapping its top-right corner/border,
        /// rather than sitting entirely outside it. Two real usability bugs this fixes together:
        /// (1) anchoring outside the rect and repositioning on every mouse-move tick made the box
        /// list chase/flee the cursor as the user moved toward it; (2) even after fixing (1) by
        /// only repositioning on hover-target change, an outside-the-rect box list still required
        /// crossing the gap between the highlighted rect and the box list, and that gap could sit
        /// over a *different* window, re-triggering ancestor-chain discovery for that other window
        /// (swapping out the highlight/box list) before the cursor ever reached the boxes.
        /// Overlapping the highlighted rect itself removes that gap: the cursor path from
        /// "somewhere over the highlighted window" to "over a box" never leaves the highlighted
        /// window's own bounds. Callers should only invoke this when the hovered target actually
        /// changes, not on every poll tick.
        /// Flips left/up near work-area edges (via <c>MonitorFromPoint</c>/<c>GetMonitorInfo</c>)
        /// so the list never renders off-screen; falls back to anchoring near
        /// <paramref name="fallbackScreenX"/>/<paramref name="fallbackScreenY"/> (the cursor) if
        /// the highlighted window's rect can't be determined (e.g. it just went away).
        /// <paramref name="highlightedHwnd"/>'s rect and the monitor work-area from
        /// <c>GetMonitorInfo</c> are both physical screen pixels; converted to WPF DIPs here (via
        /// this window's own per-monitor DPI, since <c>Left</c>/<c>Top</c>/<c>ActualWidth</c>/
        /// <c>ActualHeight</c> are DIPs) before any arithmetic, so edge-flip comparisons and the
        /// final position are correct at non-100% DPI scaling — mirrors the pixel-to-DIP
        /// conversion already used by <see cref="WindowRectHelper"/> for the highlight window.
        /// </summary>
        public void PositionNearHighlight(IntPtr highlightedHwnd, int fallbackScreenX, int fallbackScreenY)
        {
            const int InsetPx = 8;

            int anchorScreenX;
            int anchorScreenY;
            if (NativeMethods.GetWindowRect(highlightedHwnd, out var targetRect))
            {
                // Anchor so the box list's top-right corner lands just inside the highlighted
                // rect's own top-right corner (overlapping its border), instead of outside it.
                anchorScreenX = targetRect.Right - InsetPx;
                anchorScreenY = targetRect.Top + InsetPx;
            }
            else
            {
                anchorScreenX = fallbackScreenX;
                anchorScreenY = fallbackScreenY;
            }

            var pt = new NativeMethods.POINT { X = anchorScreenX, Y = anchorScreenY };
            IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            bool haveMonitorInfo = monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref mi);

            double dpi = 96.0;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    uint rawDpi = NativeMethods.GetDpiForWindow(hwnd);
                    if (rawDpi > 0)
                    {
                        dpi = rawDpi;
                    }
                }
            }
            catch { }
            double scale = 96.0 / dpi;

            // Base position: top-right corner of the box list lands at the anchor point (i.e.
            // the box list extends leftward/downward from there), so it overlaps inward into the
            // highlighted rect rather than sitting outside it.
            double dipX = anchorScreenX * scale - ActualWidth;
            double dipY = anchorScreenY * scale;

            if (haveMonitorInfo)
            {
                double workRight = mi.rcWork.Right * scale;
                double workBottom = mi.rcWork.Bottom * scale;
                double workLeft = mi.rcWork.Left * scale;
                double workTop = mi.rcWork.Top * scale;

                if (dipX + ActualWidth > workRight)
                {
                    dipX = workRight - ActualWidth;
                }
                if (dipY + ActualHeight > workBottom)
                {
                    dipY = workBottom - ActualHeight;
                }
                if (dipX < workLeft) dipX = workLeft;
                if (dipY < workTop) dipY = workTop;
            }

            Left = dipX;
            Top = dipY;
        }


        private void OnBoxMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PickerAncestorBoxItem item)
            {
                BoxHovered?.Invoke(this, item);
            }
        }

        private void OnBoxClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PickerAncestorBoxItem item)
            {
                BoxConfirmed?.Invoke(this, item);
            }
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const uint MONITOR_DEFAULTTONEAREST = 2;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        }
    }
}
