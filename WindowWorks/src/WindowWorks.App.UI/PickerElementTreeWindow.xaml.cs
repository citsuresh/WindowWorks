using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// Standalone lazy-loaded UIA element tree picker (docs/REPARENT_FEATURE_PLAN.md §6.7, Piece
    /// B) — a tree-navigable alternative to the hover-and-click DOM box list (§6.6), for
    /// deep/branchy pages where precisely hovering the exact desired element is fiddly.
    ///
    /// Deliberately built and exercised standalone in this piece: it is not yet wired into
    /// <c>WindowPickerSession</c> (that is Piece C). This window only knows about
    /// <see cref="ElementTreeNodeItem"/> (already UIA-agnostic, see that class's own doc comment)
    /// so it has no direct dependency on <c>System.Windows.Automation</c> or WindowWorks.App,
    /// consistent with the existing project-reference direction (WindowWorks.App ->
    /// WindowWorks.App.UI, not the reverse).
    ///
    /// Selecting (single-click / arrow-navigating to) a node raises <see cref="NodeSelected"/> so
    /// a caller can re-highlight the corresponding on-screen rect — mirrors the box list's
    /// hover-to-highlight behavior, just keyed off tree selection instead of mouse hover.
    /// Confirming a node (double-click OR the "Select" button — both intentionally offered, per
    /// the plan) raises <see cref="NodeConfirmed"/> exactly once and does not close itself; the
    /// caller (eventually <c>WindowPickerSession</c>, Piece C) owns closing this window in
    /// response, same ownership pattern as <see cref="PickerBoxListWindow"/>'s
    /// <c>BoxConfirmed</c>/<c>BoxHovered</c> events.
    /// </summary>
    public partial class PickerElementTreeWindow : Window
    {
        /// <summary>Raised when the tree selection changes to a node with a resolvable screen rect.</summary>
        public event EventHandler<ElementTreeNodeItem>? NodeSelected;

        /// <summary>Raised once when the user confirms a node (double-click or the Select button).</summary>
        public event EventHandler<ElementTreeNodeItem>? NodeConfirmed;

        /// <summary>
        /// Raised when the user clicks "Refresh" to re-fetch a fresh tree from the live page.
        /// This window cannot rebuild the UIA-backed roots itself (that logic lives in
        /// <c>WindowWorks.App</c>, which this project must not reference — see the class doc
        /// comment), so the caller is expected to build a new root list and call
        /// <see cref="SetRoots"/> again in response.
        /// </summary>
        public event EventHandler? RefreshRequested;

        /// <summary>
        /// Raised when the user clicks the top-right Close button, to let the owning session
        /// cancel the whole picker operation (mirrors <see cref="PickerBoxListWindow"/>'s
        /// CloseRequested and pressing Escape) rather than just dismissing this tree window on
        /// its own.
        /// </summary>
        public event EventHandler? CloseRequested;

        private IntPtr _navKeyboardHook;
        private NativeMethods.LowLevelKeyboardProc? _navKeyboardProc;

        public PickerElementTreeWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => EnsureNavKeyboardHook();
            Closed += (s, e) => RemoveNavKeyboardHook();
        }

        /// <summary>
        /// Sets the tree's root nodes (typically a single root, the page's Document element, but
        /// a list to allow multiple roots if ever needed). The subtree under each root is loaded
        /// eagerly up to <see cref="MaxEagerLoadNodes"/> total nodes
        /// (<see cref="ElementTreeNodeItem.EnsureSubtreeLoaded"/>) and every loaded node with
        /// children is expanded, so the tree opens already showing as much of the page as that
        /// budget covers rather than requiring manual expansion at each level. Nodes beyond the
        /// budget remain lazily expandable as normal (their placeholder is left in place). This
        /// deliberately trades some of the normal lazy-loading design's UIA-call savings for
        /// up-front visibility, per explicit user request, while the budget keeps a single huge
        /// page (which can have tens of thousands of DOM elements) from making this call block for
        /// a very long time. See <see cref="OnRefreshClick"/> for re-running this after the live
        /// page changes underneath an already-open tree.
        /// </summary>
        public void SetRoots(IReadOnlyList<ElementTreeNodeItem> roots)
        {
            Tree.ItemsSource = roots;

            int remainingNodeBudget = MaxEagerLoadNodes;
            foreach (var root in roots)
            {
                if (remainingNodeBudget <= 0)
                {
                    break;
                }
                root.EnsureSubtreeLoaded(ref remainingNodeBudget);
            }

            // TreeViewItem containers are generated asynchronously as each ancestor's IsExpanded
            // flips true, so expansion has to cascade down one generated level at a time rather
            // than all at once synchronously right after the ItemsSource assignment above.
            Dispatcher.BeginInvoke(new Action(() => ExpandAllContainers(Tree)), DispatcherPriority.Loaded);

            // Select the first root once its container exists, purely for visual feedback (which
            // row looks "current"), and give it actual keyboard focus so the selection highlight
            // (see the TreeViewItem style in PickerElementTreeWindow.xaml) is visible immediately
            // without requiring the user to click into the tree first. Arrow-key navigation itself
            // does not depend on this -- it's driven by a dedicated low-level keyboard hook (see
            // NavKeyboardProc) that acts whenever this window is the foreground window, regardless
            // of which child control has WPF keyboard focus -- but the *visual* selected-row
            // highlight only renders once a TreeViewItem is both IsSelected and has real focus.
            Dispatcher.BeginInvoke(new Action(() => SelectAndFocusFirstItem(roots, attemptsRemaining: 10)), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Retries across several dispatcher passes (not just one) because the first root's
        /// <see cref="TreeViewItem"/> container is not guaranteed to exist yet by the time a
        /// single <see cref="DispatcherPriority.Loaded"/> callback runs -- e.g. right after this
        /// window's <c>WindowChrome</c>/resize-related layout passes settle, container generation
        /// can still be pending for one more dispatcher cycle.
        /// </summary>
        private void SelectAndFocusFirstItem(IReadOnlyList<ElementTreeNodeItem> roots, int attemptsRemaining)
        {
            if (roots.Count == 0)
            {
                return;
            }

            if (Tree.ItemContainerGenerator.ContainerFromItem(roots[0]) is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                return;
            }

            if (attemptsRemaining > 0)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => SelectAndFocusFirstItem(roots, attemptsRemaining - 1)),
                    DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Drives Up/Down/Home/End/Left/Right navigation entirely from a dedicated low-level
        /// keyboard hook (see <see cref="NavKeyboardProc"/>) instead of WPF's normal
        /// PreviewKeyDown routing. Extensive diagnostic tracing (a global WH_KEYBOARD_LL hook,
        /// plus a raw HwndSource.AddHook on this window's own HWND) proved that this window
        /// genuinely has real Win32 foreground/focus status when arrow keys are pressed, and the
        /// OS-level input stream sees the raw key events -- yet the arrow-key WM_KEYDOWN messages
        /// specifically never reached this window's own WndProc (other keys, e.g. RightShift,
        /// came through fine). Something between the OS input queue and this window's WndProc
        /// swallows arrow-key WM_KEYDOWN before normal dispatch/routing, so a low-level keyboard
        /// hook -- which observes keys before that point entirely -- is used instead, mirroring
        /// the same proven pattern already used by <c>ReparentHostWindow.AltF4KeyboardProc</c>.
        /// Drives selection directly against a flattened list of currently-visible (i.e.
        /// all-ancestors-expanded) items built from the live container tree, so it reflects
        /// whatever is actually expanded/visible at the moment a key is pressed. Left/Right still
        /// delegate to the container's own IsExpanded for simplicity.
        /// </summary>
        private void HandleNavKey(Key key)
        {
            switch (key)
            {
                case Key.Down:
                case Key.Up:
                case Key.Home:
                case Key.End:
                    break;
                case Key.Left:
                case Key.Right:
                    if (Tree.SelectedItem is ElementTreeNodeItem selectedForExpand
                        && FindContainer(Tree, selectedForExpand) is TreeViewItem expandItem)
                    {
                        if (key == Key.Right && expandItem.HasItems && !expandItem.IsExpanded)
                        {
                            expandItem.IsExpanded = true;
                        }
                        else if (key == Key.Left && expandItem.IsExpanded)
                        {
                            expandItem.IsExpanded = false;
                        }
                    }
                    return;
                default:
                    return;
            }

            var flat = new List<(ElementTreeNodeItem Node, TreeViewItem Container)>();
            CollectVisible(Tree, flat);
            if (flat.Count == 0)
            {
                return;
            }

            int currentIndex = Tree.SelectedItem is ElementTreeNodeItem selected
                ? flat.FindIndex(entry => ReferenceEquals(entry.Node, selected))
                : -1;

            int targetIndex = key switch
            {
                Key.Down => currentIndex < 0 ? 0 : Math.Min(currentIndex + 1, flat.Count - 1),
                Key.Up => currentIndex < 0 ? 0 : Math.Max(currentIndex - 1, 0),
                Key.Home => 0,
                Key.End => flat.Count - 1,
                _ => currentIndex,
            };

            if (targetIndex < 0 || targetIndex >= flat.Count)
            {
                return;
            }

            var (_, targetContainer) = flat[targetIndex];
            targetContainer.IsSelected = true;
            targetContainer.BringIntoView();
        }

        private void EnsureNavKeyboardHook()
        {
            if (_navKeyboardHook != IntPtr.Zero)
            {
                return;
            }

            _navKeyboardProc = NavKeyboardProc;
            _navKeyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _navKeyboardProc,
                IntPtr.Zero,
                0);
        }

        private void RemoveNavKeyboardHook()
        {
            if (_navKeyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_navKeyboardHook);
                _navKeyboardHook = IntPtr.Zero;
            }

            _navKeyboardProc = null;
        }

        private static readonly Dictionary<uint, Key> NavVkMap = new()
        {
            [0x25] = Key.Left,
            [0x26] = Key.Up,
            [0x27] = Key.Right,
            [0x28] = Key.Down,
            [0x24] = Key.Home,
            [0x23] = Key.End,
        };

        private IntPtr NavKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == new IntPtr(NativeMethods.WM_KEYDOWN))
            {
                var key = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (NavVkMap.TryGetValue(key.vkCode, out var mappedKey) && IsThisWindowForeground())
                {
                    Dispatcher.BeginInvoke(new Action(() => HandleNavKey(mappedKey)));
                }
            }

            return NativeMethods.CallNextHookEx(_navKeyboardHook, nCode, wParam, lParam);
        }

        private bool IsThisWindowForeground()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == hwnd;
        }

        /// <summary>
        /// Recursively collects every currently-realized, currently-visible (all ancestors
        /// expanded) node/container pair in document order, for <see cref="HandleNavKey"/>'s
        /// manual Up/Down/Home/End navigation. A node whose own container hasn't been generated
        /// yet, or whose parent isn't expanded, is not visible and is correctly excluded.
        /// </summary>
        private static void CollectVisible(ItemsControl parent, List<(ElementTreeNodeItem, TreeViewItem)> results)
        {
            foreach (var obj in parent.Items)
            {
                if (obj is not ElementTreeNodeItem node
                    || ReferenceEquals(node, ElementTreeNodeItem.PlaceholderNode)
                    || parent.ItemContainerGenerator.ContainerFromItem(obj) is not TreeViewItem container)
                {
                    continue;
                }

                results.Add((node, container));

                if (container.IsExpanded)
                {
                    CollectVisible(container, results);
                }
            }
        }

        /// <summary>Finds the realized <see cref="TreeViewItem"/> for a given node anywhere in the tree, if any.</summary>
        private static TreeViewItem? FindContainer(ItemsControl parent, ElementTreeNodeItem target)
        {
            foreach (var obj in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(obj) is not TreeViewItem container)
                {
                    continue;
                }
                if (ReferenceEquals(obj, target))
                {
                    return container;
                }
                if (FindContainer(container, target) is TreeViewItem found)
                {
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Total node cap for <see cref="SetRoots"/>'s eager "expand all" walk (see that method's
        /// doc comment for why this exists). Chosen generously enough to fully expand most real
        /// pages while still bounding worst-case UIA call volume on a pathologically large page.
        /// </summary>
        private const int MaxEagerLoadNodes = 4000;

        /// <summary>
        /// Recursively expands every already-materialized <see cref="TreeViewItem"/> under
        /// <paramref name="parent"/> whose bound <see cref="ElementTreeNodeItem"/> has children,
        /// re-scheduling itself at <see cref="DispatcherPriority.Loaded"/> for each item's own
        /// children so newly-generated containers (which only exist once a parent's IsExpanded is
        /// set) get picked up and expanded in turn.
        /// </summary>
        private void ExpandAllContainers(ItemsControl parent)
        {
            foreach (var obj in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(obj) is not TreeViewItem item)
                {
                    continue;
                }

                if (obj is ElementTreeNodeItem { HasChildren: true })
                {
                    item.IsExpanded = true;
                }

                Dispatcher.BeginInvoke(new Action(() => ExpandAllContainers(item)), DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Re-fetches a fresh tree from the live page for when it changed since the tree was
        /// opened (e.g. an SPA re-rendered its DOM). This window has no way to rebuild UIA-backed
        /// nodes itself, so it just raises <see cref="RefreshRequested"/> and lets the caller
        /// supply new roots via <see cref="SetRoots"/>.
        /// </summary>
        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Cancels the whole picker operation, not just this tree window (mirrors
        /// <see cref="PickerBoxListWindow"/>'s Close button / Escape). Raises
        /// <see cref="CloseRequested"/> so the owning <c>WindowPickerSession</c> can cancel
        /// itself; this window still closes as a result of that (via the session's existing
        /// tear-down path), so no separate <see cref="Window.Close"/> call is needed here.
        /// </summary>
        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                    ex |= NativeMethods.WS_EX_TOOLWINDOW;
                    NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
                }
            }
            catch { }

            // Bring this window to the foreground once right after it's shown. Arrow-key
            // navigation (see NavKeyboardProc/IsThisWindowForeground) only acts while this
            // window is the real Win32 foreground window, so without this the tree can open
            // without focus (e.g. behind/alongside the anchor window it was opened from) and
            // arrow keys would silently do nothing until the user manually clicks into it.
            Activate();
        }

        /// <summary>
        /// Positions the window near an anchor screen rect (typically the hovered element's own
        /// rect, or the box list's former position) using the same DPI-aware clamp-to-work-area
        /// approach as <see cref="PickerBoxListWindow.PositionNearScreenRect"/>, so this window
        /// never renders off-screen. Kept intentionally simple (no overlap/flip heuristics) since
        /// this is a standalone, user-repositionable-in-spirit surface, not a hover-following one.
        /// </summary>
        public void PositionNear(int screenX, int screenY)
        {
            double dpi = 96.0;
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
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

            double dipX = screenX * scale;
            double dipY = screenY * scale;

            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref mi))
            {
                double workRight = mi.rcWork.Right * scale;
                double workBottom = mi.rcWork.Bottom * scale;
                double workLeft = mi.rcWork.Left * scale;
                double workTop = mi.rcWork.Top * scale;

                if (dipX + Width > workRight) dipX = workRight - Width;
                if (dipY + Height > workBottom) dipY = workBottom - Height;
                if (dipX < workLeft) dipX = workLeft;
                if (dipY < workTop) dipY = workTop;
            }

            Left = dipX;
            Top = dipY;
        }

        private void OnItemExpanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem { DataContext: ElementTreeNodeItem node })
            {
                node.EnsureChildrenLoaded();
            }
        }

        private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            SelectButton.IsEnabled = Tree.SelectedItem is ElementTreeNodeItem;

            if (Tree.SelectedItem is ElementTreeNodeItem { HasScreenRect: true } node)
            {
                NodeSelected?.Invoke(this, node);
            }
        }

        private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem { DataContext: ElementTreeNodeItem node } item && item.IsSelected)
            {
                // Mark handled so the double-click doesn't bubble further (e.g. to an ancestor
                // TreeViewItem/TreeView handler) once it's already been treated as a confirm
                // gesture here.
                e.Handled = true;
                NodeConfirmed?.Invoke(this, node);
            }
        }

        private void OnSelectClick(object sender, RoutedEventArgs e)
        {
            if (Tree.SelectedItem is ElementTreeNodeItem node)
            {
                NodeConfirmed?.Invoke(this, node);
            }
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const int WH_KEYBOARD_LL = 13;
            public const int WM_KEYDOWN = 0x0100;

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr hwnd);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetForegroundWindow();

            public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct KBDLLHOOKSTRUCT
            {
                public uint vkCode;
                public uint scanCode;
                public uint flags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        }
    }
}
