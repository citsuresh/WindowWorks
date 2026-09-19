using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Defines the action performed after a native ancestor-chain picker confirmation.
    /// </summary>
    public enum WindowPickerMode
    {
        Reparenting,
        PropertyInspector
    }

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
        private static readonly TimeSpan NativeTreeBuildTimeout = TimeSpan.FromSeconds(3);
        private const int NativeTreeNodeBudget = 4000;

        private readonly DispatcherTimer _timer;
        private readonly PickerHighlightWindow _highlight = new();
        private readonly PickerBoxListWindow _boxList = new();
        private PickerElementTreeWindow? _elementTreeWindow;
        private PickerElementTreeWindow? _programmaticallyClosingElementTree;
        private IntPtr _elementTreeBrowserHwnd = IntPtr.Zero;
        private IntPtr _elementTreeNativeHwnd = IntPtr.Zero;
        private AncestorChainEntry? _elementTreeNativeTopLevelEntry;
        private long _nativeTreeBuildGeneration;
        private int _nativeTreeBuildInProgress;
        private readonly bool _includePopOutPicks;
        private readonly bool _includeCropEntry;
        private readonly WindowPickerMode _mode;
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
        private AncestorChainEntry? _lastHoveredNativeTopLevelEntry;
        private System.Collections.Generic.List<AncestorChainEntry> _lastDiscoveredChain = new();
        private bool _disposed;

        /// <summary>
        /// Raised once when the user confirms a pick (clicks a box). The session disposes itself
        /// immediately after raising this.
        /// </summary>
        public event EventHandler<AncestorChainEntry>? Confirmed;

        /// <summary>
        /// Raised in inspector mode after either a native HWND confirmation or a native element
        /// tree confirmation. Tree selections retain their exact UIA element through
        /// <see cref="PropertyInspectorSelection.SelectedElement"/>.
        /// </summary>
        public event EventHandler<PropertyInspectorSelection>? PropertyInspectorSelectionConfirmed;
        public event EventHandler? PropertyInspectorSelectionUnavailable;

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

        public WindowPickerSession(
            uint ownProcessId,
            bool includePopOutPicks,
            bool includeCropEntry,
            WindowPickerMode mode = WindowPickerMode.Reparenting)
        {
            _ownProcessId = ownProcessId;
            _includePopOutPicks = includePopOutPicks;
            _includeCropEntry = includeCropEntry;
            _mode = mode;
            _cropOnlyMode = !includePopOutPicks && includeCropEntry;
            _boxList.BoxHovered += OnBoxHovered;
            _boxList.BoxConfirmed += OnBoxConfirmed;
            _boxList.CloseRequested += OnBoxListCloseRequested;

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
                    var chain = AncestorChainWalker.Discover(
                        pt.X,
                        pt.Y,
                        _ownProcessId,
                        includeAutomationRuntimeId: _mode != WindowPickerMode.PropertyInspector);
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

        private void OnBoxListCloseRequested(object? sender, EventArgs e)
        {
            Cancel();
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

                // Hover-discovery pause while the tree view is open (docs/REPARENT_FEATURE_PLAN.md
                // §6.7, Piece C): tree navigation should never race with hover-driven highlight/box
                // list changes, so the whole hover-poll loop is skipped entirely while the tree
                // window is up, rather than only special-casing cursor-over-tree-window like the
                // narrower box-list check below.
                if (_elementTreeWindow is not null)
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

                var chain = AncestorChainWalker.Discover(
                    pt.X,
                    pt.Y,
                    _ownProcessId,
                    includeAutomationRuntimeId: _mode != WindowPickerMode.PropertyInspector);
                _lastDiscoveredChain = chain;
                if (chain.Count == 0 || (!_includePopOutPicks && !_includeCropEntry))
                {
                    _highlight.Hide();
                    _boxList.Hide();
                    _lastHoveredHwnd = IntPtr.Zero;
                    _lastHoveredIsChildHwndPick = false;
                    _lastHoveredEntry = null;
                    _lastHoveredBrowserTopLevelEntry = null;
                    _lastHoveredNativeTopLevelEntry = null;
                    return;
                }

                // Phase 6 (docs/REPARENT_FEATURE_PLAN.md §Phase 6): if the hovered top-level
                // window is a Chromium-family browser, switch discovery from the native
                // ancestor-chain walk to a UI Automation DOM-tree walk rooted at the page's
                // Document element, still driving the same yellow-box list/highlight UX.
                // Confirming a DOM box (or a tree-node confirm, §6.7 Piece C) drives a real
                // crop-and-reparent via DomPickConfirmed.
                var topLevelEntry = FindTopLevelEntry(chain);
                if (topLevelEntry is not null
                    && BrowserClassifier.IsChromiumFamily(topLevelEntry.ClassName))
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
                        _lastHoveredNativeTopLevelEntry = null;
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
                    _lastHoveredNativeTopLevelEntry = null;
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

                _boxList.SetItems(BuildItems(
                    chain,
                    _includePopOutPicks,
                    _includeCropEntry,
                    includeElementTreeEntry: _mode == WindowPickerMode.Reparenting
                        || (_mode == WindowPickerMode.PropertyInspector && topLevelEntry is not null)));
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
                    _lastHoveredNativeTopLevelEntry = topLevelEntry;
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
            bool includeCropEntry,
            bool includeElementTreeEntry)
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

                // §6.7 Piece D: same "View Element Tree" mode-switch entry already offered on the
                // browser DOM path (see BuildDomItems below), now also offered for native windows
                // — opens a tree rooted at the top-level window's own AutomationElement instead of
                // a browser page's Document element.
                if (includeElementTreeEntry)
                {
                    items.Add(new PickerAncestorBoxItem(
                        IntPtr.Zero,
                        "View Element Tree",
                        isChildHwndPick: false,
                        isElementTreeEntry: true));
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
            var items = new System.Collections.Generic.List<PickerAncestorBoxItem>(domEntries.Count + 1 + (includeCropEntry ? 1 : 0));
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

            items.Add(new PickerAncestorBoxItem(
                IntPtr.Zero,
                "View Element Tree",
                isChildHwndPick: false,
                isElementTreeEntry: true));

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
            if (item.IsElementTreeEntry)
            {
                // Mode-switch entry (docs/REPARENT_FEATURE_PLAN.md §6.7): not a highlight target
                // itself, hovering it leaves whatever was already highlighted as-is.
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

                if (_mode == WindowPickerMode.PropertyInspector)
                {
                    if (!PropertyInspectorSelection.TryCapture(
                            browserTopLevelEntry, domEntry.Element, out var selection))
                    {
                        Dispose();
                        PropertyInspectorSelectionUnavailable?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    Dispose();
                    Confirmed?.Invoke(this, browserTopLevelEntry);
                    PropertyInspectorSelectionConfirmed?.Invoke(this, selection!);
                    return;
                }

                Dispose();
                DomPickConfirmed?.Invoke(this, new DomPickConfirmedEventArgs(domEntry, browserTopLevelEntry));
                return;
            }

            if (item.IsElementTreeEntry)
            {
                // §6.7 Piece C: switches to the tree-view picker surface. The box list itself is
                // hidden (not disposed -- the session stays alive so Escape/re-invocation still
                // works while the tree window is open) and hover-poll re-discovery is paused via
                // the _elementTreeWindow-not-null check at the top of OnTick.
                OpenElementTree();
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
                item.CapturedIdentity as ReparentEngine.CapturedWindowIdentity);
            if (_mode == WindowPickerMode.Reparenting && match.CapturedIdentity is null)
            {
                throw new InvalidOperationException("The selected window's captured identity is unavailable.");
            }
            if (_mode == WindowPickerMode.PropertyInspector)
            {
                AutomationElement? selectedElement;
                try
                {
                    selectedElement = AutomationElement.FromHandle(match.Hwnd);
                }
                catch
                {
                    selectedElement = null;
                }

                var rootWindowEntry = FindTopLevelEntry(_lastDiscoveredChain);
                if (rootWindowEntry is null
                    || !PropertyInspectorSelection.TryCapture(rootWindowEntry, selectedElement, out var selection))
                {
                    Dispose();
                    PropertyInspectorSelectionUnavailable?.Invoke(this, EventArgs.Empty);
                    return;
                }

                Dispose();
                Confirmed?.Invoke(this, match);
                PropertyInspectorSelectionConfirmed?.Invoke(this, selection!);
                return;
            }

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

        /// <summary>
        /// Opens the tree-view picker surface (docs/REPARENT_FEATURE_PLAN.md §6.7, Piece C/D) for
        /// whatever is currently hovered: a Chromium-family browser (rooted at the same Document
        /// element the hover-based box list, §6.6, would walk to under the last-known cursor
        /// position) or, per §6.7 Piece D, any other native top-level window (rooted at the
        /// window's own AutomationElement via <see cref="DomElementTreeBuilder.TryBuildRootForWindow"/>).
        /// Hides (not disposes) the box list so a single picker surface is visually active at a
        /// time, per the plan's "one picker UI active" model.
        /// </summary>
        private void OpenElementTree()
        {
            if (_mode == WindowPickerMode.PropertyInspector
                && _lastHoveredNativeTopLevelEntry is not null)
            {
                BeginNativeInspectorElementTreeBuild(_lastHoveredNativeTopLevelEntry, refreshWindow: null);
                return;
            }

            ElementTreeNodeItem? root;
            if (_lastHoveredBrowserTopLevelEntry is not null)
            {
                var browserHwnd = _lastHoveredBrowserTopLevelEntry.Hwnd;
                root = DomElementTreeBuilder.TryBuildRoot(browserHwnd, _lastPoint.X, _lastPoint.Y);
                if (root is null)
                {
                    return;
                }

                _elementTreeBrowserHwnd = browserHwnd;
                _elementTreeNativeHwnd = IntPtr.Zero;
                _elementTreeNativeTopLevelEntry = null;
            }
            else if (_lastHoveredNativeTopLevelEntry is not null)
            {
                var nativeHwnd = _lastHoveredNativeTopLevelEntry.Hwnd;
                root = DomElementTreeBuilder.TryBuildRootForWindow(nativeHwnd);
                if (root is null)
                {
                    return;
                }

                _elementTreeBrowserHwnd = IntPtr.Zero;
                _elementTreeNativeHwnd = nativeHwnd;
                _elementTreeNativeTopLevelEntry = _lastHoveredNativeTopLevelEntry;
            }
            else
            {
                // Nothing sensible to root the tree at (e.g. hover state changed between the
                // click landing and this running) -- leave the box list as the active surface
                // rather than opening an empty/unusable tree window.
                return;
            }

            var treeWindow = new PickerElementTreeWindow();
            treeWindow.SetRoots(new[] { root });
            treeWindow.NodeSelected += OnElementTreeNodeSelected;
            treeWindow.NodeConfirmed += OnElementTreeNodeConfirmed;
            treeWindow.Closed += OnElementTreeWindowClosed;
            treeWindow.RefreshRequested += OnElementTreeRefreshRequested;
            treeWindow.CloseRequested += OnElementTreeCloseRequested;
            _elementTreeWindow = treeWindow;

            // Show the tree at the same screen location the box list was occupying (§6.7 user
            // request) rather than re-anchoring near the raw cursor point, so switching from the
            // box list to the tree view feels like an in-place mode swap, not a jump. The box
            // list's Left/Top are already DIPs (WPF window coordinates), so no DPI conversion is
            // needed here unlike PositionNear's screen-pixel-based anchoring.
            treeWindow.Left = _boxList.Left;
            treeWindow.Top = _boxList.Top;
            _boxList.Hide();

            treeWindow.Show();
        }

        /// <summary>
        /// Rebuilds the tree from a fresh live UIA walk at the same anchor point the tree was
        /// originally opened at, for when the underlying page has changed since (e.g. an SPA
        /// re-rendered its DOM). <see cref="ElementTreeNodeItem"/> nodes are immutable snapshots
        /// captured at build time, not live-bound to the underlying UIA element, so refreshing
        /// means building a whole new root rather than mutating the existing one.
        /// </summary>
        private void OnElementTreeRefreshRequested(object? sender, EventArgs e)
        {
            if (_disposed || _elementTreeWindow is not PickerElementTreeWindow treeWindow)
            {
                return;
            }

            if (_mode == WindowPickerMode.PropertyInspector
                && _elementTreeNativeTopLevelEntry is not null)
            {
                BeginNativeInspectorElementTreeBuild(_elementTreeNativeTopLevelEntry, treeWindow);
                return;
            }

            var root = _elementTreeNativeHwnd != IntPtr.Zero
                ? DomElementTreeBuilder.TryBuildRootForWindow(_elementTreeNativeHwnd)
                : DomElementTreeBuilder.TryBuildRoot(_elementTreeBrowserHwnd, _lastPoint.X, _lastPoint.Y);
            if (root is null)
            {
                // Leave the previous (now possibly stale) tree displayed rather than clearing it
                // to nothing -- a failed refresh should not be more disruptive than no refresh.
                return;
            }

            treeWindow.SetRoots(new[] { root });
        }

        private async void BeginNativeInspectorElementTreeBuild(
            AncestorChainEntry topLevelEntry,
            PickerElementTreeWindow? refreshWindow)
        {
            if (_disposed
                || Interlocked.CompareExchange(ref _nativeTreeBuildInProgress, 1, 0) != 0)
            {
                return;
            }

            long generation = Interlocked.Increment(ref _nativeTreeBuildGeneration);
            Task<ElementTreeNodeItem?> buildTask;
            try
            {
                buildTask = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            if (!TryGetVerifiedInspectorRootRuntimeId(
                                topLevelEntry,
                                out var rootRuntimeIdBeforeBuild))
                            {
                                return null;
                            }

                            var root = DomElementTreeBuilder.TryBuildMaterializedRootForWindow(
                                topLevelEntry.Hwnd,
                                NativeTreeNodeBudget);
                            if (root?.Tag is not AutomationElement rootElement
                                || !string.Equals(
                                    FormatAutomationRuntimeId(rootElement.GetRuntimeId()),
                                    rootRuntimeIdBeforeBuild,
                                    StringComparison.Ordinal)
                                || !TryGetVerifiedInspectorRootRuntimeId(
                                    topLevelEntry,
                                    out var rootRuntimeIdAfterBuild)
                                || !string.Equals(
                                    rootRuntimeIdBeforeBuild,
                                    rootRuntimeIdAfterBuild,
                                    StringComparison.Ordinal))
                            {
                                return null;
                            }

                            return root;
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _nativeTreeBuildInProgress, 0);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Exchange(ref _nativeTreeBuildInProgress, 0);
                return;
            }

            if (!ReferenceEquals(await Task.WhenAny(buildTask, Task.Delay(NativeTreeBuildTimeout)), buildTask))
            {
                ObserveFault(buildTask);
                if (!_disposed && generation == Volatile.Read(ref _nativeTreeBuildGeneration))
                {
                    ShowNativeTreeUnavailableMessage();
                }
                return;
            }

            ElementTreeNodeItem? root;
            try
            {
                root = await buildTask;
            }
            catch
            {
                return;
            }

            if (_disposed
                || generation != Volatile.Read(ref _nativeTreeBuildGeneration)
                || root is null)
            {
                return;
            }

            if (refreshWindow is not null)
            {
                if (ReferenceEquals(_elementTreeWindow, refreshWindow))
                {
                    refreshWindow.SetRoots(new[] { root });
                }
                return;
            }

            _elementTreeBrowserHwnd = IntPtr.Zero;
            _elementTreeNativeHwnd = topLevelEntry.Hwnd;
            _elementTreeNativeTopLevelEntry = topLevelEntry;

            var treeWindow = new PickerElementTreeWindow();
            treeWindow.SetRoots(new[] { root });
            treeWindow.NodeSelected += OnElementTreeNodeSelected;
            treeWindow.NodeConfirmed += OnElementTreeNodeConfirmed;
            treeWindow.Closed += OnElementTreeWindowClosed;
            treeWindow.RefreshRequested += OnElementTreeRefreshRequested;
            treeWindow.CloseRequested += OnElementTreeCloseRequested;
            _elementTreeWindow = treeWindow;
            treeWindow.Left = _boxList.Left;
            treeWindow.Top = _boxList.Top;
            _boxList.Hide();
            treeWindow.Show();
        }

        private static bool TryGetVerifiedInspectorRootRuntimeId(
            AncestorChainEntry entry,
            out string? runtimeId)
        {
            runtimeId = null;
            return ReparentEngine.TryGetWindowIdentity(
                       entry.Hwnd,
                       out uint liveProcessId,
                       out DateTime liveProcessStartTimeUtc,
                       out string? liveClassName,
                       out runtimeId)
                && liveProcessId == entry.ProcessId
                && liveProcessStartTimeUtc == entry.ProcessStartTimeUtc
                && string.Equals(liveClassName, entry.ClassName, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(runtimeId);
        }

        private static string? FormatAutomationRuntimeId(int[]? runtimeIdParts)
        {
            return runtimeIdParts is null || runtimeIdParts.Length == 0
                ? null
                : string.Join(
                    ",",
                    runtimeIdParts.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void ShowNativeTreeUnavailableMessage()
        {
            System.Windows.MessageBox.Show(
                "The selected UI element tree is unavailable or did not respond in time.",
                "Inspect UI Element",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        private void OnElementTreeNodeSelected(object? sender, ElementTreeNodeItem node)
        {
            if (_disposed || !node.HasScreenRect)
            {
                return;
            }

            var highlightHwnd = _elementTreeNativeHwnd != IntPtr.Zero ? _elementTreeNativeHwnd : _elementTreeBrowserHwnd;
            _highlight.ShowAroundScreenRect(
                highlightHwnd,
                node.ScreenRect.Left,
                node.ScreenRect.Top,
                node.ScreenRect.Right,
                node.ScreenRect.Bottom);
        }

        private void OnElementTreeNodeConfirmed(object? sender, ElementTreeNodeItem node)
        {
            if (_disposed)
            {
                return;
            }

            if (_mode == WindowPickerMode.PropertyInspector)
            {
                // Two element-tree flavors reach this handler in Property Inspector mode: a
                // native-window tree (rooted via BeginNativeInspectorElementTreeBuild,
                // _elementTreeNativeTopLevelEntry set) and a browser DOM tree (rooted via
                // OpenElementTree's DomElementTreeBuilder.TryBuildRoot path,
                // _lastHoveredBrowserTopLevelEntry set instead). Both cases hand off the node's
                // own AutomationElement (node.Tag) to PropertyInspectorSelection.TryCapture --
                // only the top-level entry used for identity capture differs. Previously only the
                // native-tree case was handled here, so confirming a DOM element via "View Element
                // Tree" silently fell through to the crop/reparent DomPickConfirmed path instead
                // of opening the Property Inspector at all.
                var topLevelEntryForCapture = _elementTreeNativeTopLevelEntry ?? _lastHoveredBrowserTopLevelEntry;
                if (topLevelEntryForCapture is null
                    || node.Tag is not AutomationElement selectedElement)
                {
                    return;
                }

                if (!PropertyInspectorSelection.TryCapture(
                        topLevelEntryForCapture,
                        selectedElement,
                        out var selection))
                {
                    CloseElementTree();
                    Dispose();
                    PropertyInspectorSelectionUnavailable?.Invoke(this, EventArgs.Empty);
                    return;
                }

                CloseElementTree();
                Dispose();
                PropertyInspectorSelectionConfirmed?.Invoke(this, selection!);
                return;
            }

            AncestorChainEntry topLevelEntry;
            IntPtr rootHwnd;
            if (!node.HasScreenRect)
            {
                return;
            }

            if (_elementTreeNativeHwnd != IntPtr.Zero && _elementTreeNativeTopLevelEntry is not null)
            {
                // §6.7 Piece D: a native-rooted tree confirm is mechanically identical to the
                // browser DOM case -- both ultimately crop-and-reparent a real top-level HWND
                // using a clipped screen rect -- so this reuses DomPickConfirmed/DomElementEntry
                // rather than introducing a parallel native-specific event/args pair. The
                // "BrowserHwnd" field name is a misnomer here, but the mechanism (and the
                // ReparentController.StartDomCropReparent consumer) is exactly what's needed.
                topLevelEntry = _elementTreeNativeTopLevelEntry;
                rootHwnd = _elementTreeNativeHwnd;
            }
            else if (_lastHoveredBrowserTopLevelEntry is not null)
            {
                topLevelEntry = _lastHoveredBrowserTopLevelEntry;
                rootHwnd = _elementTreeBrowserHwnd;
            }
            else
            {
                return;
            }

            var domEntry = new DomElementEntry(
                rootHwnd,
                controlTypeName: string.Empty,
                name: node.Label,
                clippedScreenRect: node.ScreenRect,
                isDocumentRoot: false);

            // Close the tree window (raises OnElementTreeWindowClosed, which clears
            // _elementTreeWindow) before disposing the rest of the session, same ordering as the
            // box-list confirm paths above -- the confirm event is the last thing raised.
            CloseElementTree();
            Dispose();
            DomPickConfirmed?.Invoke(this, new DomPickConfirmedEventArgs(domEntry, topLevelEntry));
        }

        private void OnElementTreeWindowClosed(object? sender, EventArgs e)
        {
            var treeWindow = sender as PickerElementTreeWindow;
            bool programmaticClose = treeWindow is not null
                && ReferenceEquals(_programmaticallyClosingElementTree, treeWindow);
            if (treeWindow is not null)
            {
                treeWindow.NodeSelected -= OnElementTreeNodeSelected;
                treeWindow.NodeConfirmed -= OnElementTreeNodeConfirmed;
                treeWindow.Closed -= OnElementTreeWindowClosed;
                treeWindow.RefreshRequested -= OnElementTreeRefreshRequested;
                treeWindow.CloseRequested -= OnElementTreeCloseRequested;
            }
            if (ReferenceEquals(_elementTreeWindow, treeWindow))
            {
                _elementTreeWindow = null;
            }
            if (programmaticClose)
            {
                _programmaticallyClosingElementTree = null;
            }

            if (!_disposed && !programmaticClose)
            {
                // The tree is a mode of the same picker session. A system/window close must end
                // the session rather than leave its hidden box list stranded.
                Cancel();
            }
        }

        /// <summary>
        /// The tree window's Close button cancels the whole picker operation (mirrors the box
        /// list's Close button / Escape), not just this tree window on its own.
        /// </summary>
        private void OnElementTreeCloseRequested(object? sender, EventArgs e)
        {
            Cancel();
        }

        private void CloseElementTree()
        {
            if (_elementTreeWindow is PickerElementTreeWindow treeWindow)
            {
                // Closing raises OnElementTreeWindowClosed synchronously, which unsubscribes and
                // clears _elementTreeWindow -- no separate cleanup needed here.
                _programmaticallyClosingElementTree = treeWindow;
                try { treeWindow.Close(); }
                catch { _programmaticallyClosingElementTree = null; }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Interlocked.Increment(ref _nativeTreeBuildGeneration);
            try { _timer.Stop(); } catch { }
            try { _boxList.BoxHovered -= OnBoxHovered; _boxList.BoxConfirmed -= OnBoxConfirmed; _boxList.CloseRequested -= OnBoxListCloseRequested; } catch { }
            try { _highlight.Close(); } catch { }
            try { _boxList.Close(); } catch { }
            CloseElementTree();
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
