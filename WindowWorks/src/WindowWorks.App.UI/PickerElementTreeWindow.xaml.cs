using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private IReadOnlyList<ElementTreeNodeItem>? _currentRoots;
        private bool _hasAutoFocusedOnce;

        /// <summary>
        /// Mirrors the last node <see cref="Tree"/>'s <c>SelectedItemChanged</c> reported as a
        /// real <see cref="ElementTreeNodeItem"/>. Observed live (both via automated UIA clicks
        /// and genuine human mouse clicks): clicking "Select" shortly after clicking a tree row
        /// can land with <c>Tree.SelectedItem</c> already back to <c>null</c> by the time
        /// <see cref="OnSelectClick"/> runs, even though the row still renders as selected and
        /// <see cref="SelectButton"/> is enabled -- i.e. WPF's <c>TreeView.SelectedItem</c>
        /// momentarily/spuriously clears itself around that second click on this transparent/
        /// topmost/tool window (this window class already has other documented input quirks, see
        /// <see cref="SearchBox_PreviewKeyDown"/>'s doc comment). This field is the actual source
        /// of truth <see cref="OnSelectClick"/> confirms against, falling back to
        /// <c>Tree.SelectedItem</c> only if this hasn't been set yet, so Select keeps working
        /// even when the underlying TreeView selection state glitches.
        /// </summary>
        private ElementTreeNodeItem? _lastKnownSelectedNode;

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
            _currentRoots = roots;
            _lastKnownSelectedNode = null;
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

            // Re-apply whatever search text is already in the box (e.g. after a Refresh rebuilds
            // the roots) so the filter stays in effect across a refresh instead of silently
            // reverting to "show everything".
            ApplySearchFilter(SearchBox.Text);

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
            //
            // BUG FIX (SearchBox-never-receives-typed-characters report): this auto-focus must
            // only happen once, on the very first SetRoots call after the window opens -- not on
            // every subsequent SetRoots (e.g. a Refresh, or SetRoots re-applying the filter). Since
            // SelectAndFocusFirstItem retries across several dispatcher passes, and each retry
            // unconditionally steals WPF keyboard focus onto the first TreeViewItem, a user who
            // clicked into SearchBox and started typing during that retry window would silently
            // have focus yanked back to the tree, making their keystrokes appear to go nowhere.
            if (!_hasAutoFocusedOnce)
            {
                _hasAutoFocusedOnce = true;
                Dispatcher.BeginInvoke(new Action(() => SelectAndFocusFirstItem(roots, attemptsRemaining: 10)), DispatcherPriority.Loaded);
            }
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

            // Don't steal focus from a control the user has already interacted with (e.g. clicked
            // into SearchBox and started typing) on a later retry pass -- only claim focus while
            // nothing else in this window has it yet.
            if (Keyboard.FocusedElement is DependencyObject focused && !ReferenceEquals(focused, this))
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
                    || !node.IsVisible
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

                // Check for any real (non-placeholder) children rather than the HasChildren flag:
                // materialized/eager snapshot nodes (Property Inspector's native-window tree,
                // see DomElementTreeBuilder.BuildSnapshotNode) always report HasChildren: false
                // (they have no lazy loader to report), but their Children collection is already
                // fully populated up front — so this node can still legitimately have children to
                // expand into even though HasChildren is false.
                bool hasRealChildren = obj is ElementTreeNodeItem node
                    && node.Children.Count > 0
                    && !ReferenceEquals(node.Children[0], ElementTreeNodeItem.PlaceholderNode);
                if (hasRealChildren)
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

        /// <summary>
        /// Lets the user drag this borderless window by its title-bar-equivalent area (the row
        /// above the search box). Guards against starting a drag when the click landed on a
        /// Button (Refresh/Close) so those remain clickable. Delegates the actual drag to Windows
        /// itself (WM_NCLBUTTONDOWN/HTCAPTION), the same proven pattern used by
        /// ReparentHostWindow's overlay drag handle.
        /// </summary>
        private void OnDragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestorOrSelf<Button>(source) is not null)
            {
                return;
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            const int WM_NCLBUTTONDOWN = 0x00A1;
            const int HTCAPTION = 2;

            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            e.Handled = true;
        }

        private static T? FindAncestorOrSelf<T>(DependencyObject source) where T : DependencyObject
        {
            var current = source;
            while (current is not null)
            {
                if (current is T match)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
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
        /// ROOT CAUSE (confirmed via live UIA trace, see checkpoint history): this window's
        /// <c>AllowsTransparency="True"</c> (a layered/WS_EX_LAYERED window) breaks WPF's TSF-based
        /// text-composition pipeline for this window specifically -- <see cref="PreviewKeyDown"/>
        /// fires normally with the raw key, but no WM_CHAR ever reaches WPF, so
        /// PreviewTextInput/TextChanged never fire and nothing is ever typed. Same class of bug as
        /// the earlier arrow-key routing issue on this window (see <see cref="HandleNavKey"/>'s doc
        /// comment) -- something between the OS input queue and this window's normal message
        /// composition is swallowed. Workaround: manually translate the virtual key + live keyboard
        /// modifier state into a character (mirroring what TranslateMessage/WM_CHAR would otherwise
        /// produce) and insert it directly into the TextBox, bypassing the broken composition path.
        /// </summary>
        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            if (e.Key == Key.Back)
            {
                if (textBox.SelectionLength > 0)
                {
                    textBox.SelectedText = string.Empty;
                }
                else if (textBox.CaretIndex > 0)
                {
                    int index = textBox.CaretIndex - 1;
                    textBox.Text = textBox.Text.Remove(index, 1);
                    textBox.CaretIndex = index;
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete)
            {
                if (textBox.SelectionLength > 0)
                {
                    textBox.SelectedText = string.Empty;
                }
                else if (textBox.CaretIndex < textBox.Text.Length)
                {
                    textBox.Text = textBox.Text.Remove(textBox.CaretIndex, 1);
                }
                e.Handled = true;
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Skip modifier/navigation/control keys entirely -- they either have no printable
            // representation or are already handled elsewhere (arrow keys via NavKeyboardProc).
            if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
                or Key.Tab or Key.Enter or Key.Escape or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.CapsLock or Key.Insert or Key.PageUp or Key.PageDown
                or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
                or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12)
            {
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                return;
            }

            var keyboardState = new byte[256];
            if (!NativeMethods.GetKeyboardState(keyboardState))
            {
                return;
            }

            uint scanCode = NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
            var buffer = new System.Text.StringBuilder(8);
            int result = NativeMethods.ToUnicode(vk, scanCode, keyboardState, buffer, buffer.Capacity, 0);
            if (result <= 0)
            {
                // No printable character for this key/modifier combination (e.g. a dead key or a
                // key with no character mapping) -- let WPF's normal (broken-for-this-window, but
                // harmless) handling continue rather than swallowing the key.
                return;
            }

            string text = buffer.ToString(0, result);
            if (textBox.SelectionLength > 0)
            {
                textBox.SelectedText = string.Empty;
            }
            int caret = textBox.CaretIndex;
            textBox.Text = textBox.Text.Insert(caret, text);
            textBox.CaretIndex = caret + text.Length;
            e.Handled = true;
        }

        /// <summary>
        /// Positions the window near an anchor screen rect
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

            // Only overwrite the last-known-good selection when the TreeView reports a real
            // node; deliberately do NOT clear it back to null when SelectedItem transiently
            // becomes null (see _lastKnownSelectedNode's doc comment) -- Select should still be
            // able to confirm the row the user actually clicked.
            if (Tree.SelectedItem is ElementTreeNodeItem selectedNode)
            {
                _lastKnownSelectedNode = selectedNode;
            }

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
            // Prefer the live TreeView selection when available, but fall back to the last
            // known-good selection if it has spuriously gone null (see
            // _lastKnownSelectedNode's doc comment) -- this is the actual fix for the reported
            // "Select does nothing" bug, confirmed via live debugging (Tree.SelectedItem null at
            // OnSelectClick time despite the row still visually appearing selected and
            // SelectButton.IsEnabled being true).
            var node = Tree.SelectedItem as ElementTreeNodeItem ?? _lastKnownSelectedNode;
            if (node is not null)
            {
                NodeConfirmed?.Invoke(this, node);
            }
        }

        /// <summary>
        /// Search/filter box (docs/REPARENT_FEATURE_PLAN.md §6.7 follow-up): re-applies the
        /// filter on every keystroke against whatever roots are currently loaded. Filtering is
        /// pure visibility toggling via <see cref="ElementTreeNodeItem.IsVisible"/> (see the
        /// <c>TreeViewItem</c> style's <c>DataTrigger</c> in the XAML) — it never mutates the
        /// tree, selection, or lazy-loading state, so clearing the search box always restores the
        /// exact same tree that was there before filtering.
        /// </summary>
        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearchFilter(SearchBox.Text);
        }

        /// <summary>
        /// Recomputes <see cref="ElementTreeNodeItem.IsVisible"/> for every currently-loaded node
        /// under <see cref="_currentRoots"/> against <paramref name="filterText"/>. A node is
        /// visible when the filter is blank/whitespace, when its own label matches
        /// (case-insensitive substring), or when any already-loaded descendant matches — matching
        /// ancestors are also expanded so a deep match is actually visible without the user
        /// manually drilling down to it. Only already-loaded (expanded at least once) subtrees are
        /// searched — nodes never expanded yet are not eagerly loaded just to search them, since
        /// that would defeat the tree's whole lazy-loading design on a large page; this means a
        /// match that exists only under a never-expanded node won't be found until it's expanded
        /// (matching <see cref="SetRoots"/>'s existing "eager expand up to a budget" behavior,
        /// which already loads most of a typical page up front).
        /// </summary>
        private void ApplySearchFilter(string? filterText)
        {
            if (_currentRoots is null)
            {
                return;
            }

            string trimmed = (filterText ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                foreach (var root in _currentRoots)
                {
                    SetAllVisible(root);
                }
                return;
            }

            foreach (var root in _currentRoots)
            {
                FilterNode(root, trimmed);
            }
        }

        private static void SetAllVisible(ElementTreeNodeItem node)
        {
            node.IsVisible = true;
            foreach (var child in node.Children)
            {
                if (!ReferenceEquals(child, ElementTreeNodeItem.PlaceholderNode))
                {
                    SetAllVisible(child);
                }
            }
        }

        /// <summary>Returns true if <paramref name="node"/> itself or any descendant matches, setting <see cref="ElementTreeNodeItem.IsVisible"/> along the way.</summary>
        private bool FilterNode(ElementTreeNodeItem node, string filterText)
        {
            bool selfMatch = node.Label.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                || node.FullLabel.Contains(filterText, StringComparison.OrdinalIgnoreCase);

            bool anyDescendantMatch = false;
            foreach (var child in node.Children)
            {
                if (ReferenceEquals(child, ElementTreeNodeItem.PlaceholderNode))
                {
                    continue;
                }
                if (FilterNode(child, filterText))
                {
                    anyDescendantMatch = true;
                }
            }

            node.IsVisible = selfMatch || anyDescendantMatch;

            // Expand ancestors of a match so it's actually reachable/visible without the user
            // manually drilling down -- a match hidden inside a collapsed branch would otherwise
            // be indistinguishable from "no match".
            if (anyDescendantMatch && FindContainer(Tree, node) is TreeViewItem container)
            {
                container.IsExpanded = true;
            }

            return node.IsVisible;
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const int WH_KEYBOARD_LL = 13;
            public const int WM_KEYDOWN = 0x0100;
            public const uint MAPVK_VK_TO_VSC = 0;

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool GetKeyboardState(byte[] lpKeyState);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern uint MapVirtualKey(uint uCode, uint uMapType);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                System.Text.StringBuilder pwszBuff, int cchBuff, uint wFlags);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ReleaseCapture();
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

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
