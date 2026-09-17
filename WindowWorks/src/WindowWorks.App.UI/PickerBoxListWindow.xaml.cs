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
        public bool IsCropEntry { get; }

        /// <summary>
        /// Set for the pale-orange "View Element Tree" mode-switch entry (docs/
        /// REPARENT_FEATURE_PLAN.md Phase 6, section 6.7) appended to the box list whenever the
        /// hovered top-level window is a Chromium-family browser. Unlike <see cref="IsCropEntry"/>
        /// and a real DOM/native pick, clicking this entry does not confirm a pick at all -- it is
        /// a mode switch that opens a separate tree-view picker surface (Piece B/C).
        /// </summary>
        public bool IsElementTreeEntry { get; }
        public uint ProcessId { get; }
        public DateTime ProcessStartTimeUtc { get; }
        public string? ClassName { get; }
        public string? AutomationRuntimeId { get; }
        public object? CapturedIdentity { get; }

        /// <summary>
        /// Set when this box represents a UI Automation DOM element pick (docs/
        /// REPARENT_FEATURE_PLAN.md §Phase 6) rather than a native ancestor-chain HWND pick.
        /// DOM entries have no HWND of their own (<see cref="Hwnd"/> is <see cref="IntPtr.Zero"/>
        /// for them) — the containing browser HWND and clipped screen rect live on the entry
        /// itself.
        /// </summary>
        public object? DomEntry { get; }

        /// <summary>
        /// Nesting depth used for visual indentation only (docs/REPARENT_FEATURE_PLAN.md §Phase
        /// 6): 0 for the shallowest/root-most entry, increasing toward the deepest/nearest-
        /// hovered entry. Kept deliberately small/low-cost (a per-box left margin, see
        /// <c>PickerBoxListWindow.xaml</c>'s <c>IndentLevelToMarginConverter</c> usage) rather than a
        /// real tree-view control, since this is still the flat hover-box list (§6.3), just with
        /// a hint of hierarchy — a full tree view is planned separately (§6.7).
        /// </summary>
        public int IndentLevel { get; }

        /// <summary>
        /// Untruncated label text shown as a tooltip on hover, and as the actual displayed text
        /// (the box's <c>TextBlock</c> uses <c>TextTrimming="CharacterEllipsis"</c> to trim it
        /// visually rather than the text itself being pre-shortened). Defaults to <see cref="Label"/>
        /// itself when no separate full text is supplied (native ancestor-chain / crop entries,
        /// whose labels are never truncated).
        /// </summary>
        public string FullLabel { get; }

        public PickerAncestorBoxItem(
            IntPtr hwnd,
            string label,
            bool isChildHwndPick,
            bool isCropEntry = false,
            uint processId = 0,
            DateTime processStartTimeUtc = default,
            string? className = null,
            string? automationRuntimeId = null,
            object? capturedIdentity = null,
            object? domEntry = null,
            int indentLevel = 0,
            string? fullLabel = null,
            bool isElementTreeEntry = false)
        {
            Hwnd = hwnd;
            Label = label;
            IsChildHwndPick = isChildHwndPick;
            IsCropEntry = isCropEntry;
            ProcessId = processId;
            ProcessStartTimeUtc = processStartTimeUtc;
            ClassName = className;
            AutomationRuntimeId = automationRuntimeId;
            CapturedIdentity = capturedIdentity;
            DomEntry = domEntry;
            IndentLevel = indentLevel;
            FullLabel = fullLabel ?? label;
            IsElementTreeEntry = isElementTreeEntry;
        }
    }

    /// <summary>
    /// Yellow-box list confirm UI (§6.3) for the ancestor-chain window picker. A small, real,
    /// input-owning window (not a full-screen overlay) positioned adaptively near the cursor
    /// (§6.4) — hovering a box re-highlights the corresponding on-screen rect via
    /// <see cref="BoxHovered"/>; clicking a box is the actual confirm/commit gesture via
    /// <see cref="BoxConfirmed"/>.
    /// </summary>
    public partial class PickerBoxListWindow : Window
    {
        public event EventHandler<PickerAncestorBoxItem>? BoxHovered;
        public event EventHandler<PickerAncestorBoxItem>? BoxConfirmed;

        /// <summary>
        /// Raised when the user clicks the top-right Close button, to let the owning session
        /// cancel the whole picker (mirrors pressing Escape).
        /// </summary>
        public event EventHandler? CloseRequested;

        public PickerBoxListWindow()
        {
            InitializeComponent();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
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

            PositionNearAnchor(anchorScreenX, anchorScreenY);
        }

        /// <summary>
        /// Same adaptive anchoring as <see cref="PositionNearHighlight"/>, but anchored to an
        /// arbitrary screen-pixel rect rather than an HWND's own bounds (docs/
        /// REPARENT_FEATURE_PLAN.md §Phase 6) — used for UI Automation DOM element picks, which
        /// have no HWND of their own to query via <c>GetWindowRect</c>.
        /// <paramref name="dpiReferenceHwnd"/> supplies the per-monitor DPI for the box list's own
        /// pixel-to-DIP conversion (typically the containing browser window).
        ///
        /// Deliberately does NOT reuse <see cref="PositionNearHighlight"/>'s "overlap inward from
        /// the top-right corner" placement: for a small, arbitrary DOM element rect (as opposed to
        /// a whole native window's rect) that placement fully covers the element the user is
        /// trying to preview before picking it — a real usability problem found during manual
        /// testing. Per explicit user direction, the box list instead sits mostly OUTSIDE the
        /// highlighted rect, extending to its right, overlapping only the rightmost ~10% of the
        /// rect's own width — enough to still visually anchor the list to what's highlighted,
        /// without hiding the highlighted content. The list is intentionally still kept
        /// overlapping (not fully external) so the cursor's path from "over the highlighted
        /// element" to "over a box" never leaves the highlighted rect's own bounds (otherwise
        /// moving toward a fully-external box list would cross over other page content first,
        /// which would re-trigger hover discovery for that other content mid-move).
        /// </summary>
        public void PositionNearScreenRect(
            IntPtr dpiReferenceHwnd,
            int screenLeft,
            int screenTop,
            int screenRight,
            int screenBottom,
            int fallbackScreenX,
            int fallbackScreenY)
        {
            const double OverlapFraction = 0.10;

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

            double dipX;
            double dipY;
            if (screenRight > screenLeft && screenBottom > screenTop)
            {
                double rectWidthDip = (screenRight - screenLeft) * scale;
                double overlapDip = rectWidthDip * OverlapFraction;
                double rectRightDip = screenRight * scale;
                double rectLeftDip = screenLeft * scale;

                // Box list's own left edge lands just inside the rect's right edge by
                // OverlapFraction of the rect's width, then extends rightward (outside the rect)
                // from there — the opposite extension direction from PositionNearHighlight, which
                // intentionally extends leftward/inward instead.
                dipX = rectRightDip - overlapDip;
                dipY = screenTop * scale;

                // If the work area doesn't have room to the right (e.g. the highlighted element
                // sits near a monitor's right edge), naive clamping alone would slide the list
                // back to fully cover the rect again. Flip to anchor from the rect's LEFT edge
                // instead (list extends further left, overlapping only the leftmost
                // OverlapFraction of the rect's width) whenever the right-anchored position
                // wouldn't fit in the current monitor's work area.
                if (TryGetWorkAreaDip(screenLeft, screenTop, scale, out var workLeft, out _, out var workRight, out _)
                    && dipX + ActualWidth > workRight)
                {
                    double flippedDipX = (rectLeftDip + overlapDip) - ActualWidth;
                    if (flippedDipX >= workLeft)
                    {
                        dipX = flippedDipX;
                    }
                }
            }
            else
            {
                dipX = fallbackScreenX * scale;
                dipY = fallbackScreenY * scale;
            }

            ClampToNearestMonitorWorkArea(ref dipX, ref dipY, screenLeft, screenTop, scale);

            Left = dipX;
            Top = dipY;
        }

        private bool TryGetWorkAreaDip(int referenceScreenX, int referenceScreenY, double scale, out double workLeft, out double workTop, out double workRight, out double workBottom)
        {
            workLeft = workTop = workRight = workBottom = 0;
            var pt = new NativeMethods.POINT { X = referenceScreenX, Y = referenceScreenY };
            IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref mi))
            {
                return false;
            }

            workLeft = mi.rcWork.Left * scale;
            workTop = mi.rcWork.Top * scale;
            workRight = mi.rcWork.Right * scale;
            workBottom = mi.rcWork.Bottom * scale;
            return true;
        }

        private void ClampToNearestMonitorWorkArea(ref double dipX, ref double dipY, int referenceScreenX, int referenceScreenY, double scale)
        {
            var pt = new NativeMethods.POINT { X = referenceScreenX, Y = referenceScreenY };
            IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref mi))
            {
                return;
            }

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

        private void PositionNearAnchor(int anchorScreenX, int anchorScreenY)
        {
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
