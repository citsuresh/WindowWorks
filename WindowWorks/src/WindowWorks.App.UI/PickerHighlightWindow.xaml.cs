using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// Yellow-box highlight rectangle used only during an active picker session
    /// (docs/REPARENT_FEATURE_PLAN.md §6.2/§6.3). Deliberately a new, separate class from
    /// <see cref="HighlightOverlay"/> (which is a fire-and-forget, auto-closing "flash" shown
    /// after a preset is applied) — this window has no auto-close timer and is driven entirely by
    /// the picker session's mouse-move/click loop via <see cref="ShowAround"/>/<see cref="Hide"/>.
    /// </summary>
    public partial class PickerHighlightWindow : Window
    {
        public PickerHighlightWindow()
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
                    // Toolwindow (no alt-tab entry) + transparent (click-through — this highlight
                    // box must never itself become the WindowFromPoint result during hover
                    // discovery, per §6.2).
                    ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
                    NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
                }
            }
            catch { }
        }

        /// <summary>
        /// Positions and shows the highlight rect around <paramref name="hwnd"/>'s current screen
        /// bounds. Safe to call repeatedly as the hovered ancestor changes.
        /// </summary>
        public void ShowAround(IntPtr hwnd)
        {
            if (!WindowRectHelper.TryGetFrameBoundsDip(hwnd, out var left, out var top, out var width, out var height))
            {
                Hide();
                return;
            }

            double borderThicknessDip = BorderHighlight.BorderThickness.Left;
            double expandDip = Math.Min(6, borderThicknessDip / 2.0);

            Left = left - expandDip;
            Top = top - expandDip;
            Width = Math.Max(1, width + expandDip * 2.0);
            Height = Math.Max(1, height + expandDip * 2.0);

            BorderHighlight.Width = Width;
            BorderHighlight.Height = Height;
            Canvas.SetLeft(BorderHighlight, 0);
            Canvas.SetTop(BorderHighlight, 0);
            BorderHighlight.Visibility = Visibility.Visible;

            if (!IsVisible)
            {
                Show();
            }
        }

        public new void Hide()
        {
            try { BorderHighlight.Visibility = Visibility.Collapsed; } catch { }
            try { base.Hide(); } catch { }
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const int WS_EX_TRANSPARENT = 0x00000020;

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        }
    }
}
