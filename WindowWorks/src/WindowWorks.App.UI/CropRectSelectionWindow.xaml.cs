using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// Crop-rect drag-to-select overlay (docs/REPARENT_FEATURE_PLAN.md §6.5, Phase 2 "Crop-rect
    /// drag UI" slice). Reuses PowerToys Crop-and-Lock-style drag-to-select rectangle UX, scoped
    /// to a single target HWND: shown positioned exactly over that target's current on-screen
    /// frame bounds (via <see cref="WindowRectHelper"/>, the same DPI-aware helper already used
    /// by <see cref="PickerHighlightWindow"/>/<see cref="PickerBoxListWindow"/>), so the user drags
    /// a rectangle directly over the target's live visible content.
    ///
    /// This window is purely a UI/geometry concern: it does not know about
    /// <see cref="WindowWorks.App.ReparentEngine"/> or the reparent/restore mechanics at all — it
    /// only resolves a user-drawn rectangle in physical screen pixels and raises
    /// <see cref="RectConfirmed"/> with that rect. The picker-integration slice
    /// (docs/REPARENT_FEATURE_PLAN.md Phase 2, "picker integration") is responsible for feeding
    /// the confirmed rect into <see cref="WindowWorks.App.CropRectGeometry.TryCompute"/> and then
    /// the existing reparent mechanics.
    ///
    /// Deliberately NOT a full-screen overlay (unlike a naive crop tool): sized/positioned to
    /// exactly the target's own frame bounds, consistent with this feature's existing "avoid a
    /// full-screen input-owning overlay" design choice (§6.2's WindowFromPoint self-occlusion
    /// rationale, already applied to <see cref="WindowWorks.App.WindowPickerSession"/>) and with
    /// the fact that a crop selection is only ever meaningful relative to the target's own visible
    /// bounds, not the whole screen.
    /// </summary>
    public partial class CropRectSelectionWindow : Window
    {
        private readonly IntPtr _targetHwnd;
        private bool _dragging;
        private Point _dragStartDip;

        /// <summary>
        /// The target's frame bounds at the time this window was shown, in physical screen
        /// pixels — used to translate a drag gesture (tracked in this window's own DIPs) back
        /// into the absolute screen-pixel rect <see cref="CropRectGeometry.TryCompute"/> expects.
        /// </summary>
        private double _targetLeftPx, _targetTopPx;
        private double _dipToPixelScaleX = 1.0, _dipToPixelScaleY = 1.0;

        /// <summary>
        /// Raised once the user confirms a drawn rect (Enter, or releasing the mouse after a
        /// drag large enough to be meaningful). The rect is in physical screen pixels, ready to
        /// pass to <see cref="WindowWorks.App.CropRectGeometry.TryCompute"/>. This window closes
        /// itself immediately after raising this.
        /// </summary>
        public event EventHandler<System.Drawing.Rectangle>? RectConfirmed;

        /// <summary>
        /// Raised once if the user cancels (Escape, or closes the window) without confirming a
        /// rect. This window closes itself immediately after raising this.
        /// </summary>
        public event EventHandler? Cancelled;

        private bool _resolved;

        public CropRectSelectionWindow(IntPtr targetHwnd)
        {
            _targetHwnd = targetHwnd;
            InitializeComponent();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!NativeMethods.GetWindowRect(_targetHwnd, out var targetRect))
            {
                // Target went away before the crop UI could even show — cancel immediately
                // rather than showing a nonsensical/zero-size overlay.
                RaiseCancelledOnce();
                Close();
                return;
            }

            uint targetDpi = NativeMethods.GetDpiForWindow(_targetHwnd);
            double targetScale = targetDpi > 0 ? targetDpi / 96.0 : 1.0;
            double left = targetRect.Left / targetScale;
            double top = targetRect.Top / targetScale;
            double width = Math.Max(1, targetRect.Right - targetRect.Left) / targetScale;
            double height = Math.Max(1, targetRect.Bottom - targetRect.Top) / targetScale;

            Left = left;
            Top = top;
            Width = width;
            Height = height;

            Scrim.Width = width;
            Scrim.Height = height;

            Canvas.SetLeft(HintText, 8);
            Canvas.SetTop(HintText, 8);

            // Compute the DIP->pixel scale and the target's pixel-space origin so a drag gesture
            // (tracked in this window's own DIPs, matching WPF mouse-event coordinates) can be
            // translated back into an absolute screen-pixel rect for CropRectGeometry.
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
            {
                _dipToPixelScaleX = source.CompositionTarget.TransformToDevice.M11;
                _dipToPixelScaleY = source.CompositionTarget.TransformToDevice.M22;
            }
            _targetLeftPx = targetRect.Left;
            _targetTopPx = targetRect.Top;

            Activate();
            Focus();
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStartDip = e.GetPosition(RootCanvas);
            SelectionBorder.Visibility = Visibility.Visible;
            UpdateSelectionRect(_dragStartDip, _dragStartDip);
            CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            var current = e.GetPosition(RootCanvas);
            UpdateSelectionRect(_dragStartDip, current);
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            _dragging = false;
            try { ReleaseMouseCapture(); } catch { }

            var current = e.GetPosition(RootCanvas);
            TryConfirmDrag(_dragStartDip, current);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RaiseCancelledOnce();
                Close();
                return;
            }

            if (e.Key == Key.Enter && SelectionBorder.Visibility == Visibility.Visible)
            {
                double left = Canvas.GetLeft(SelectionBorder);
                double top = Canvas.GetTop(SelectionBorder);
                var start = new Point(left, top);
                var end = new Point(left + SelectionBorder.Width, top + SelectionBorder.Height);
                TryConfirmDrag(start, end);
            }
        }

        private const double MinDragDip = 8.0;

        private void TryConfirmDrag(Point start, Point end)
        {
            start = ClampToCanvas(start);
            end = ClampToCanvas(end);

            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);
            if (width < MinDragDip || height < MinDragDip)
            {
                // Too small to be a meaningful crop selection (e.g. an accidental click) — treat
                // as "not yet confirmed" rather than cancelling outright, so the user can just
                // try dragging again without having to re-invoke the crop flow from the picker.
                return;
            }

            double left = Math.Min(start.X, end.X);
            double top = Math.Min(start.Y, end.Y);

            int screenLeftPx = (int)Math.Round(_targetLeftPx + left * _dipToPixelScaleX);
            int screenTopPx = (int)Math.Round(_targetTopPx + top * _dipToPixelScaleY);
            int screenWidthPx = (int)Math.Round(width * _dipToPixelScaleX);
            int screenHeightPx = (int)Math.Round(height * _dipToPixelScaleY);

            var rect = new System.Drawing.Rectangle(screenLeftPx, screenTopPx, screenWidthPx, screenHeightPx);

            _resolved = true;
            RectConfirmed?.Invoke(this, rect);
            Close();
        }

        private void UpdateSelectionRect(Point start, Point end)
        {
            start = ClampToCanvas(start);
            end = ClampToCanvas(end);

            double left = Math.Min(start.X, end.X);
            double top = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);

            Canvas.SetLeft(SelectionBorder, left);
            Canvas.SetTop(SelectionBorder, top);
            SelectionBorder.Width = width;
            SelectionBorder.Height = height;
        }

        private Point ClampToCanvas(Point point)
        {
            return new Point(
                Math.Clamp(point.X, 0, RootCanvas.ActualWidth),
                Math.Clamp(point.Y, 0, RootCanvas.ActualHeight));
        }

        private void RaiseCancelledOnce()
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Safety net: closing via any other path (e.g. Alt+F4) without having raised
            // RectConfirmed must still surface as a cancel, so a caller waiting on this session
            // never hangs with neither event ever firing.
            RaiseCancelledOnce();
            base.OnClosed(e);
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);
        }
    }
}
