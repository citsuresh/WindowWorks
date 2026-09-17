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

    public sealed class DomPickConfirmedEventArgs : EventArgs
    {
        public DomPickConfirmedEventArgs(DomElementEntry domEntry, AncestorChainEntry browserTopLevelEntry)
        {
            DomEntry = domEntry ?? throw new ArgumentNullException(nameof(domEntry));
            BrowserTopLevelEntry = browserTopLevelEntry ?? throw new ArgumentNullException(nameof(browserTopLevelEntry));
        }

        public DomElementEntry DomEntry { get; }
        public AncestorChainEntry BrowserTopLevelEntry { get; }
    }

    public sealed class WindowPickerSession : IDisposable
    {
        private const int PollIntervalMs = 40;
        private const int VK_ESCAPE = 0x1B;

        private readonly DispatcherTimer _timer;
        private readonly PickerHighlightWindow _highlight = new();
        private readonly PickerBoxListWindow _boxList = new();
        private readonly bool _includePopOutPicks;
        private readonly bool _includeCropEntry;
        private readonly uint _ownProcessId;

        /// <summary>
        /// True when crop is the ONLY enabled entry point ("Pop Out and Reparent" is off, "Crop
        /// and Reparent" is on). In this mode there is nothing else the picker could ever offer
        /// (the box list would only ever contain the single "Crop a region" entry, and clicking
        /// it was an unnecessary extra step), so <see cref="Start"/> skips the hover/box-list loop
        /// entirely and goes straight into crop-rect selection over the top-level window under the
        /// cursor at the moment the hotkey was pressed.
        /// </summary>
        private readonly bool _cropOnlyMode;

        private NativeMethods.POINT _lastPoint = new() { X = int.MinValue, Y = int.MinValue };
        private IntPtr _lastHoveredHwnd = IntPtr.Zero;
        private bool _lastHoveredIsChildHwndPick;
        private AncestorChainEntry? _lastHoveredEntry;
        private AncestorChainEntry? _lastHoveredBrowserTopLevelEntry;
        private System.Collections.Generic.List<AncestorChainEntry> _lastDiscoveredChain = new();
        private bool _disposed;

        /// <summary>
        /// Raised once when the user confirms a pick (clicks a box). The session disposes itself
        /// immediately after raising this.
        /// </summary>
        public event EventHandler<AncestorChainEntry>? Confirmed;

        /// <summary>
        /// Raised once when the user selects the crop entry appended to the yellow-box stack.
        /// The entry identifies the currently highlighted ancestor-chain target.
        /// </summary>
        public event EventHandler<AncestorChainEntry>? CropRequested;

        /// <summary>
        /// Raised once when the user confirms a DOM element box so the caller can crop-and-
        /// reparent the whole browser HWND using that element's clipped screen rect while still
        /// re-verifying the browser window's own identity.
        /// </summary>
        public event EventHandler<DomPickConfirmedEventArgs>? DomPickConfirmed;

        /// <summary>
        /// Raised once if the session ends without a pick (Escape, or re-invocation cancel). The
        /// session disposes itself immediately after raising this.
        /// </summary>
        public event EventHandler? Cancelled;

        public WindowPickerSession(uint ownProcessId, bool includePopOutPicks, bool includeCropEntry)
        {
            _ownProcessId = ownProcessId;
            _includePopOutPicks = includePopOutPicks;
            _includeCropEntry = includeCropEntry;
            _cropOnlyMode = !includePopOutPicks && includeCropEntry;
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
            if (_cropOnlyMode)
            {
                // Crop-only mode (Pop Out off, Crop on) skips the hover/box-list/click sequence
                // entirely: go straight into crop-rect selection over the top-level window under
                // the cursor at the moment the hotkey was pressed, so there is no extra "click to
                // confirm" step before the drag-to-select overlay appears.
                if (NativeMethods.GetCursorPos(out var pt))
                {
                    var chain = AncestorChainWalker.Discover(pt.X, pt.Y, _ownProcessId);
                    var topLevel = FindTopLevelEntry(chain);
                    if (topLevel is not null)
                    {
                        Dispose();
                        CropRequested?.Invoke(this, topLevel);
                        return;
                    }
                }

                // No usable window under the cursor at hotkey time — cancel rather than leaving
                // the caller waiting on a session that will never raise anything.
                Cancel();
                return;
            }

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
                _lastDiscoveredChain = chain;
                if (chain.Count == 0 || (!_includePopOutPicks && !_includeCropEntry))
                {
                    _highlight.Hide();
                    _boxList.Hide();
                    _lastHoveredHwnd = IntPtr.Zero;
                    _lastHoveredIsChildHwndPick = false;
                    _lastHoveredEntry = null;
                    _lastHoveredBrowserTopLevelEntry = null;
                    return;
                }

                // Phase 6 (bare-minimum slice, docs/REPARENT_FEATURE_PLAN.md §Phase 6): if the
                // hovered top-level window is a Chromium-family browser, switch discovery from
                // the native ancestor-chain walk to a UI Automation DOM-tree walk rooted at the
                // page's Document element, still driving the same yellow-box list/highlight UX.
                // This slice is visual-only — confirming a DOM box does not yet wire into
                // crop-and-reparent (that's a later piece); it currently just cancels the session.
                var topLevelEntry = FindTopLevelEntry(chain);
                if (topLevelEntry is not null && BrowserClassifier.IsChromiumFamily(topLevelEntry.ClassName))
                {
                    var domEntries = BrowserDomTreeWalker.Discover(topLevelEntry.Hwnd, pt.X, pt.Y);
                    if (domEntries.Count == 0)
                    {
                        _highlight.Hide();
                        _boxList.Hide();
                        _lastHoveredHwnd = IntPtr.Zero;
                        _lastHoveredIsChildHwndPick = false;
                        _lastHoveredEntry = null;
                        _lastHoveredBrowserTopLevelEntry = null;
                        return;
                    }

                    _boxList.SetItems(BuildDomItems(domEntries, _includeCropEntry));
                    if (!_boxList.IsVisible)
                    {
                        _boxList.Show();
                    }

                    var nearestDom = domEntries[0];
                    _lastHoveredHwnd = topLevelEntry.Hwnd;
                    _lastHoveredIsChildHwndPick = false;
                    _lastHoveredEntry = topLevelEntry;
                    _lastHoveredBrowserTopLevelEntry = topLevelEntry;
                    _highlight.ShowAroundScreenRect(
                        topLevelEntry.Hwnd,
                        nearestDom.ClippedScreenRect.Left,
                        nearestDom.ClippedScreenRect.Top,
                        nearestDom.ClippedScreenRect.Right,
                        nearestDom.ClippedScreenRect.Bottom);
                    // Anchor near the highlighted DOM rect itself (not the whole browser window)
                    // so the box list sits next to what's actually highlighted, consistent with
                    // the native-picker case anchoring to the highlighted HWND's own bounds.
                    _boxList.PositionNearScreenRect(
                        topLevelEntry.Hwnd,
                        nearestDom.ClippedScreenRect.Left,
                        nearestDom.ClippedScreenRect.Top,
                        nearestDom.ClippedScreenRect.Right,
                        nearestDom.ClippedScreenRect.Bottom,
                        pt.X,
                        pt.Y);
                    return;
                }

                _boxList.SetItems(BuildItems(chain, _includePopOutPicks, _includeCropEntry));
                if (!_boxList.IsVisible)
                {
                    _boxList.Show();
                }

                // Default highlight to the nearest (deepest) entry, matching the first box.
                var nearest = chain[0];
                if (nearest.Hwnd != _lastHoveredHwnd)
                {
                    _lastHoveredHwnd = nearest.Hwnd;
                    _lastHoveredIsChildHwndPick = !nearest.IsTopLevel;
                    _lastHoveredEntry = nearest;
                    _lastHoveredBrowserTopLevelEntry = null;
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

        /// <summary>
        /// Finds the top-level (root) entry in a discovered ancestor chain — used by crop-only
        /// mode to crop the whole target window regardless of which specific nested control the
        /// cursor happens to be resting on at hotkey-press time.
        /// </summary>
        private static AncestorChainEntry? FindTopLevelEntry(System.Collections.Generic.List<AncestorChainEntry> chain)
        {
            foreach (var entry in chain)
            {
                if (entry.IsTopLevel)
                {
                    return entry;
                }
            }
            return chain.Count > 0 ? chain[^1] : null;
        }

        private static System.Collections.Generic.List<PickerAncestorBoxItem> BuildItems(
            System.Collections.Generic.List<AncestorChainEntry> chain,
            bool includePopOutPicks,
            bool includeCropEntry)
        {
            var items = new System.Collections.Generic.List<PickerAncestorBoxItem>((includePopOutPicks ? chain.Count : 0) + (includeCropEntry ? 1 : 0));
            if (includePopOutPicks)
            {
                // Indentation hint (docs/REPARENT_FEATURE_PLAN.md §Phase 6): the chain is
                // nearest/deepest-first, so the deepest entry (index 0) gets the highest indent,
                // decreasing toward the root (last entry) — mirrors DOM nesting depth without
                // needing a real tree structure, since the list order already reflects it.
                for (int i = 0; i < chain.Count; i++)
                {
                    var entry = chain[i];
                    int indentLevel = chain.Count - 1 - i;
                    string title = string.IsNullOrWhiteSpace(entry.Title) ? entry.ClassName : entry.Title;
                    string label = entry.IsTopLevel ? title : $"{title} ({entry.ClassName})";
                    items.Add(new PickerAncestorBoxItem(
                        entry.Hwnd,
                        label,
                        isChildHwndPick: !entry.IsTopLevel,
                        processId: entry.ProcessId,
                        processStartTimeUtc: entry.ProcessStartTimeUtc,
                        className: entry.ClassName,
                        automationRuntimeId: entry.AutomationRuntimeId,
                        capturedIdentity: entry.CapturedIdentity,
                        indentLevel: indentLevel));
                }
            }
            if (includeCropEntry)
            {
                items.Add(new PickerAncestorBoxItem(
                    IntPtr.Zero,
                    "Crop a region",
                    isChildHwndPick: false,
                    isCropEntry: true));
            }
            return items;
        }

        /// <summary>
        /// Builds yellow-box items for a Phase 6 UI Automation DOM-tree walk result (bare-minimum
        /// slice: labeled purely by control type + name, no crop entry wiring yet beyond simply
        /// listing it per <paramref name="includeCropEntry"/>'s existing toggle so the stack looks
        /// consistent with the native-picker case). Real DOM element names/control-type text can
        /// be much longer than a native ancestor entry's, which was making individual boxes render
        /// very wide. Originally handled here via manual character-budget truncation (with an
        /// adaptive per-indent-level budget), but that approach kept under- or over-truncating
        /// rows relative to how much space the panel's own auto-sizing actually gave each row.
        /// Replaced with WPF's own <c>TextTrimming="CharacterEllipsis"</c> on a width-capped
        /// <c>TextBlock</c> (see <c>PickerBoxListWindow.xaml</c>) bound directly to the full label
        /// — the layout engine measures and trims per-row using the real rendered font/width,
        /// which manual character counting can't replicate exactly. The full label is always
        /// available via a tooltip on hover (<see cref="PickerAncestorBoxItem.FullLabel"/>), same
        /// as before.
        ///
        /// Indentation (added per explicit user request, kept deliberately small — see
        /// <see cref="IndentLevelToMarginConverter"/>'s step size) hints at DOM nesting depth: the
        /// deepest/nearest-hovered entry (index 0) gets the highest indent, decreasing toward the
        /// Document root (last entry) — same "list order already reflects depth" approach as the
        /// native ancestor-chain case above.
        /// </summary>
        private static System.Collections.Generic.List<PickerAncestorBoxItem> BuildDomItems(
            System.Collections.Generic.List<DomElementEntry> domEntries,
            bool includeCropEntry)
        {
            var items = new System.Collections.Generic.List<PickerAncestorBoxItem>(domEntries.Count + (includeCropEntry ? 1 : 0));
            for (int i = 0; i < domEntries.Count; i++)
            {
                var entry = domEntries[i];
                int indentLevel = domEntries.Count - 1 - i;
                string label = string.IsNullOrWhiteSpace(entry.Name)
                    ? entry.ControlTypeName
                    : $"{entry.Name} ({entry.ControlTypeName})";
                items.Add(new PickerAncestorBoxItem(
                    IntPtr.Zero,
                    label,
                    isChildHwndPick: false,
                    domEntry: entry,
                    indentLevel: indentLevel,
                    fullLabel: label));
            }
            if (includeCropEntry)
            {
                items.Add(new PickerAncestorBoxItem(
                    IntPtr.Zero,
                    "Crop a region",
                    isChildHwndPick: false,
                    isCropEntry: true));
            }
            return items;
        }

        private void OnBoxHovered(object? sender, PickerAncestorBoxItem item)
        {
            if (_disposed)
            {
                return;
            }
            if (item.IsCropEntry)
            {
                return;
            }
            if (item.DomEntry is DomElementEntry domEntry)
            {
                _highlight.ShowAroundScreenRect(
                    domEntry.BrowserHwnd,
                    domEntry.ClippedScreenRect.Left,
                    domEntry.ClippedScreenRect.Top,
                    domEntry.ClippedScreenRect.Right,
                    domEntry.ClippedScreenRect.Bottom);
                return;
            }
            _lastHoveredHwnd = item.Hwnd;
            _lastHoveredIsChildHwndPick = item.IsChildHwndPick;
            _lastHoveredEntry = FindDiscoveredEntry(item.Hwnd);
            _highlight.ShowAround(item.Hwnd);
        }

        private void OnBoxConfirmed(object? sender, PickerAncestorBoxItem item)
        {
            if (_disposed)
            {
                return;
            }

            if (item.DomEntry is DomElementEntry domEntry)
            {
                if (_lastHoveredBrowserTopLevelEntry is null)
                {
                    Cancel();
                    return;
                }

                var browserTopLevelEntry = _lastHoveredBrowserTopLevelEntry;
                Dispose();
                DomPickConfirmed?.Invoke(this, new DomPickConfirmedEventArgs(domEntry, browserTopLevelEntry));
                return;
            }

            if (item.IsCropEntry)
            {
                if (_lastHoveredEntry is null)
                {
                    Cancel();
                    return;
                }

                var cropEntry = _lastHoveredEntry;
                Dispose();
                CropRequested?.Invoke(this, cropEntry);
                return;
            }

            var match = new AncestorChainEntry(
                item.Hwnd,
                item.ClassName ?? string.Empty,
                string.Empty,
                isTopLevel: !item.IsChildHwndPick,
                item.ProcessId,
                item.ProcessStartTimeUtc,
                item.AutomationRuntimeId,
                item.CapturedIdentity as ReparentEngine.CapturedWindowIdentity
                    ?? throw new InvalidOperationException("The selected window's captured identity is unavailable."));
            Dispose();
            Confirmed?.Invoke(this, match);
        }

        private AncestorChainEntry ResolveEntry(IntPtr hwnd, bool isChildHwndPick)
        {
            var chain = AncestorChainWalker.Discover(_lastPoint.X, _lastPoint.Y, _ownProcessId);
            AncestorChainEntry? match = null;
            foreach (var entry in chain)
            {
                if (entry.Hwnd == hwnd)
                {
                    match = entry;
                    break;
                }
            }
            // Fall back to a synthesized entry if the chain changed between hover and click
            // (target moved/closed mid-confirm) — still honor the click using what we know.
            return match ?? throw new InvalidOperationException("The selected window is no longer in the discovered ancestor chain.");
        }

        private AncestorChainEntry? FindDiscoveredEntry(IntPtr hwnd)
        {
            foreach (var entry in _lastDiscoveredChain)
            {
                if (entry.Hwnd == hwnd)
                {
                    return entry;
                }
            }

            return null;
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
