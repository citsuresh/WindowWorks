using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Drives one interactive ancestor-chain picker session (docs/REPARENT_FEATURE_PLAN.md
    /// §6.1-§6.4): polls the cursor position on a lightweight timer (avoids a full-screen
    /// input-owning overlay's WindowFromPoint self-occlusion problem entirely, per §6.2 — there
    /// is no full-screen overlay window here at all, only the small yellow-box list and highlight
    /// windows, so background hover discovery is never blocked), re-discovers the ancestor chain
    /// under the cursor as it moves, updates the highlight + yellow-box list accordingly, and
    /// resolves when the user clicks a box (confirm) or presses Escape (cancel).
    ///
    /// Re-invoking the hotkey mid-session cancels the current session first (§6.1) — enforced by
    /// <see cref="ReparentController"/> only ever holding one active session at a time and calling
    /// <see cref="Cancel"/> on the previous one before starting a new one.
    /// </summary>
    public sealed class WindowPickerSession : IDisposable
    {
        private const int PollIntervalMs = 40;
        private const int VK_ESCAPE = 0x1B;

        private readonly DispatcherTimer _timer;
        private readonly PickerHighlightWindow _highlight = new();
        private readonly PickerBoxListWindow _boxList = new();
        private readonly uint _ownProcessId;

        private NativeMethods.POINT _lastPoint = new() { X = int.MinValue, Y = int.MinValue };
        private IntPtr _lastHoveredHwnd = IntPtr.Zero;
        private bool _disposed;

        /// <summary>
        /// Raised once when the user confirms a pick (clicks a box). The session disposes itself
        /// immediately after raising this.
        /// </summary>
        public event EventHandler<AncestorChainEntry>? Confirmed;

        /// <summary>
        /// Raised once if the session ends without a pick (Escape, or re-invocation cancel). The
        /// session disposes itself immediately after raising this.
        /// </summary>
        public event EventHandler? Cancelled;

        public WindowPickerSession(uint ownProcessId)
        {
            _ownProcessId = ownProcessId;
            _boxList.BoxHovered += OnBoxHovered;
            _boxList.BoxConfirmed += OnBoxConfirmed;

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _timer.Start();
        }

        public void Cancel()
        {
            if (_disposed)
            {
                return;
            }
            Dispose();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if ((NativeMethods.GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                {
                    Cancel();
                    return;
                }

                if (!NativeMethods.GetCursorPos(out var pt))
                {
                    return;
                }

                // Skip re-discovery while the cursor is over the box list itself — otherwise
                // hovering a box would re-trigger a fresh ancestor-chain walk under the box list
                // window instead of leaving the already-discovered chain (and its highlight) as-is.
                if (_boxList.IsVisible && IsPointOverWindow(_boxList, pt.X, pt.Y))
                {
                    return;
                }

                if (pt.X == _lastPoint.X && pt.Y == _lastPoint.Y)
                {
                    return;
                }
                _lastPoint = pt;

                var chain = AncestorChainWalker.Discover(pt.X, pt.Y, _ownProcessId);
                if (chain.Count == 0)
                {
                    _highlight.Hide();
                    _boxList.Hide();
                    _lastHoveredHwnd = IntPtr.Zero;
                    return;
                }

                _boxList.SetItems(BuildItems(chain));
                if (!_boxList.IsVisible)
                {
                    _boxList.Show();
                }

                // Default highlight to the nearest (deepest) entry, matching the first box.
                var nearest = chain[0];
                if (nearest.Hwnd != _lastHoveredHwnd)
                {
                    _lastHoveredHwnd = nearest.Hwnd;
                    _highlight.ShowAround(nearest.Hwnd);

                    // Anchor the box list next to the highlighted window's own bounds rather than
                    // the live cursor position: repositioning on every mouse-move tick made the
                    // box list chase/flee the cursor, since a reposition-on-every-move plus the
                    // cursor moving toward the box list meant it could never be reached. Only
                    // reposition when the hovered target actually changes, and anchor relative to
                    // its highlighted rect (a fixed point) instead of the cursor (a moving one).
                    _boxList.PositionNearHighlight(nearest.Hwnd, pt.X, pt.Y);
                }
            }
            catch
            {
                // Best-effort: a picker session should never crash the app if a target window
                // races away mid-poll.
            }
        }

        private static System.Collections.Generic.List<PickerAncestorBoxItem> BuildItems(System.Collections.Generic.List<AncestorChainEntry> chain)
        {
            var items = new System.Collections.Generic.List<PickerAncestorBoxItem>(chain.Count);
            foreach (var entry in chain)
            {
                string title = string.IsNullOrWhiteSpace(entry.Title) ? entry.ClassName : entry.Title;
                string label = entry.IsTopLevel ? title : $"{title} ({entry.ClassName})";
                items.Add(new PickerAncestorBoxItem(entry.Hwnd, label, isChildHwndPick: !entry.IsTopLevel));
            }
            return items;
        }

        private void OnBoxHovered(object? sender, PickerAncestorBoxItem item)
        {
            if (_disposed)
            {
                return;
            }
            _lastHoveredHwnd = item.Hwnd;
            _highlight.ShowAround(item.Hwnd);
        }

        private void OnBoxConfirmed(object? sender, PickerAncestorBoxItem item)
        {
            if (_disposed)
            {
                return;
            }

            var chain = AncestorChainWalker.Discover(_lastPoint.X, _lastPoint.Y, _ownProcessId);
            AncestorChainEntry? match = null;
            foreach (var entry in chain)
            {
                if (entry.Hwnd == item.Hwnd)
                {
                    match = entry;
                    break;
                }
            }
            // Fall back to a synthesized entry if the chain changed between hover and click
            // (target moved/closed mid-confirm) — still honor the click using what we know.
            match ??= new AncestorChainEntry(item.Hwnd, string.Empty, string.Empty, isTopLevel: !item.IsChildHwndPick);

            Dispose();
            Confirmed?.Invoke(this, match);
        }

        /// <summary>
        /// Whether the given screen point (physical pixels, e.g. from <c>GetCursorPos</c>) falls
        /// within <paramref name="window"/>'s current on-screen bounds. Compares physical pixels
        /// on both sides via <c>GetWindowRect</c> on the window's own HWND — deliberately NOT
        /// <see cref="System.Windows.Window.Left"/>/<see cref="System.Windows.Window.Top"/> (WPF
        /// DIPs), whose mismatch against a physical-pixel cursor position was the root cause of a
        /// real bug: at non-100% DPI scaling this check silently failed, so the box list was
        /// never recognized as "under the cursor" and kept re-centering itself away from the
        /// cursor on every poll tick, making it impossible to reach.
        /// </summary>
        private static bool IsPointOverWindow(System.Windows.Window window, int screenX, int screenY)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var r))
                {
                    return false;
                }
                return screenX >= r.Left && screenX <= r.Right && screenY >= r.Top && screenY <= r.Bottom;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try { _timer.Stop(); } catch { }
            try { _boxList.BoxHovered -= OnBoxHovered; _boxList.BoxConfirmed -= OnBoxConfirmed; } catch { }
            try { _highlight.Close(); } catch { }
            try { _boxList.Close(); } catch { }
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        }
    }
}
