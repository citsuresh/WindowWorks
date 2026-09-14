using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
namespace WindowWorks.App.UI
{
    public readonly record struct CapturedIdentitySnapshot(
        IntPtr Hwnd,
        uint ProcessId,
        DateTime ProcessStartTimeUtc,
        string? ClassName,
        string? AutomationRuntimeId,
        Func<bool> IsInvalidated);

    /// <summary>
    /// Phase 0 minimal WPF host frame for the Window Reparenting feature
    /// (docs/REPARENT_FEATURE_PLAN.md §14 Phase 0, Part 3).
    ///
    /// Deliberately unstyled beyond a single Close/Restore button — no control-strip polish,
    /// no drag/resize chrome, no crop mode, no multi-target tracking. Those are Phase 1+ scope.
    ///
    /// Hosts a plain native WS_CHILD "socket" window (not a WPF element) that the reparented
    /// target is actually SetParent'd into, and bridges WM_MOUSEACTIVATE/WM_ACTIVATE via
    /// HwndSource.AddHook so the embedded target receives activation/focus correctly (§8 step 7),
    /// per the resolved host-frame-technology decision (WPF Window + HwndSource.AddHook bridge).
    ///
    /// Deliberately does NOT reference WindowWorks.App.ReparentEngine directly: the existing
    /// project-reference direction is WindowWorks.App -> WindowWorks.App.UI (not the reverse), so
    /// this class only exposes the raw mechanics an orchestrator in WindowWorks.App needs
    /// (the socket HWND to reparent into, and a settable TargetHwnd for activation forwarding).
    /// The actual SaveOriginalState/Reparent/RestoreOriginalState calls are made by the caller.
    /// </summary>
    public partial class ReparentHostWindow : Window
    {
        private IntPtr _socketHwnd = IntPtr.Zero;
        private IntPtr _overlayHwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private HwndSource? _overlayContentSource;
        private EventHandler? _overlayLocationChangedHandler;
        private SizeChangedEventHandler? _overlaySizeChangedHandler;
        private bool _overlayResizeRepositionPending;
        private DispatcherTimer? _overlayHoverPollTimer;
        private DispatcherTimer? _overlayIdleHideTimer;
        private DispatcherTimer? _overlayAnimationTimer;
        private bool _isCursorInOverlayTriggerZone;

        private bool _isCursorOverOverlayClientArea;

        private bool _isCursorOverOverlayContentWindow;

        private bool _isCursorOverOverlayWindow => _isCursorOverOverlayClientArea || _isCursorOverOverlayContentWindow;
        private bool _overlayIdleHidePending;
        private bool _overlayMouseLeaveTrackingActive;
        private bool _overlayContentMouseLeaveTrackingActive;
        private bool _overlayAwaitingMotionToReshow;
        private NativeMethods.POINT? _lastOverlayObservedCursorPoint;
        private int _overlayAnimationGeneration;
        private byte _overlayCurrentAlpha;
        private int _overlayCurrentOffsetY;
        private int _overlayRestingLeft;
        private int _overlayRestingTop;
        private int _overlayRestingWidth = 1;
        private int _overlayRestingHeight = 1;
        private Button? _reopenOriginalButton;
        private Button? _minimizeButton;
        private Button? _maximizeRestoreButton;
        private Button? _closeRestoreButton;
        private Grid? _overlayRootGrid;
        private TextBlock? _reopenOriginalGlyph;
        private TextBlock? _maximizeRestoreGlyph;

        // BUG FIX (resize-doesn't-propagate report): tracks the last width/height actually applied
        // to the target via SetWindowPos, so PositionSocket() can tell a genuine socket *resize*
        // (host frame resized) apart from a mere reposition (host frame moved without resizing).
        // See the long comment on PositionSocket() below for why this is different from the
        // earlier "always resize the target" bug that was reverted.
        private int _lastAppliedTargetWidth = -1;
        private int _lastAppliedTargetHeight = -1;
        private int _targetOffsetX;
        private int _targetOffsetY;
        private bool _resizeTargetToSocket = true;
        private uint _targetProcessId;
        private DateTime _targetProcessStartTimeUtc;
        private string? _targetClassName;
        private string? _targetAutomationRuntimeId;
        private CapturedIdentitySnapshot? _capturedTargetIdentity;
        private IntPtr _altF4Hook;
        private NativeMethods.LowLevelKeyboardProc? _altF4Proc;
        private bool _altF4ClosePending;

        // PERF (non-blocking review note): during an interactive drag-resize of the host frame,
        // WPF raises SizeChanged on every intermediate tick, which would otherwise issue a target
        // SetWindowPos resize on every tick too. Debounce the *target* resize specifically (the
        // socket itself is still repositioned/resized live on every tick for visual smoothness)
        // so the target only gets resized once real resizing has paused briefly, avoiding
        // redundant SetWindowPos churn/flicker on a still-top-level-styled native window mid-drag.
        private DispatcherTimer? _targetResizeDebounceTimer;
        private static readonly TimeSpan TargetResizeDebounceDelay = TimeSpan.FromMilliseconds(80);
        // Sized for the future single-row button strip (Phase 3 Piece 4 adds actual buttons).
        private const double OverlayHeightDip = 40.0;
        private const double OverlaySlideOffsetDip = 10.0;
        private const double OverlayButtonSizeDip = 32.0;
        private const double OverlayButtonMarginDip = 8.0;
        private const double OverlayTriggerZoneHeightFraction = 0.2;
        private const int OverlayCursorMotionToleranceDevicePixels = 2;
        private static readonly TimeSpan OverlayHoverPollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan OverlayIdleHideDelay = TimeSpan.FromMilliseconds(2500);
        private static readonly TimeSpan OverlayAnimationDuration = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan OverlayAnimationTickInterval = TimeSpan.FromMilliseconds(15);
        private const byte OverlayTargetAlpha = 180;
        private const double CustomResizeGripThicknessDip = 4.0;
        private const string OverlayWindowClassName = "WindowWorksReparentOverlay";
        private static readonly Brush OverlayPanelBackgroundBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x4A, 0x3C, 0x1E));
        private static readonly Brush OverlayButtonForegroundBrush = Brushes.White;
        private static readonly Brush OverlayButtonBackgroundBrush = Brushes.Transparent;
        private static readonly Brush OverlayButtonBorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        private static readonly Dictionary<IntPtr, WeakReference<ReparentHostWindow>> s_overlayOwnersByHwnd = new();
        private bool _customResizeGripsEnabled;

        public ReparentHostWindow()
        {
            FreezeOverlayBrushes();
            _customResizeGripsEnabled = true;
            InitializeComponent();
        }

        /// <summary>
        /// Raised once the host frame's socket window has been created and is ready to be
        /// reparented into (i.e. after <see cref="OnLoaded"/> runs). Callers must wait for this
        /// before reading <see cref="SocketHwnd"/>.
        /// </summary>
        public event EventHandler? SocketReady;

        /// <summary>
        /// The native WS_CHILD socket window that a target should be SetParent'd into. Valid
        /// only after <see cref="SocketReady"/> has fired (IntPtr.Zero before then).
        /// </summary>
        public IntPtr SocketHwnd => _socketHwnd;

        // Diagnostic logging for failure paths only (appended to a temp file, not
        // Debug.WriteLine, so it's visible even when not running under a debugger) — kept
        // permanently since these are exactly the kind of interop calls that can fail silently
        // against an incompatible target app in the field.
        private static void DebugLog(string message)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windowworks_reparent_log.txt");
                System.IO.File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// The currently-embedded target window, used only for WM_MOUSEACTIVATE/WM_ACTIVATE
        /// forwarding (§8 step 7). The caller is responsible for save/reparent/restore mechanics
        /// via its own ReparentEngine instance; this window does not perform those calls itself.
        /// Set to IntPtr.Zero when nothing is embedded (disables activation forwarding).
        /// Prefer <see cref="AttachTarget"/> over setting this directly once a target has been
        /// SetParent'd into the socket — see that method's remarks for why.
        /// </summary>
        public IntPtr TargetHwnd { get; set; } = IntPtr.Zero;

        /// <summary>
        /// Configures whether this host frame can be resized by the user (docs/REPARENT_FEATURE_PLAN.md
        /// §6.5/§6.7/§9's three-way sizing policy, Phase 2 crop-mode item): a whole top-level
        /// window pick remains resizable (default); an ancestor-chain child-HWND pick defaults to
        /// fixed-size unless the user has opted into the "Allow resizing reparented child elements"
        /// setting; and any crop-mode pick is always fixed-size regardless of that setting because
        /// the crop rect is a one-time pixel mapping and "Adjust crop" is the only supported way
        /// to change what is visible. The fixed-size path uses <see cref="ResizeMode.CanMinimize"/>,
        /// which removes resize grips/maximize affordance while keeping the standard title bar so
        /// the host can still be repositioned by dragging. Must be called before <see cref="OnLoaded"/>
        /// runs (i.e. before this window is shown) — this sets <see cref="ResizeMode"/>/
        /// <see cref="WindowState"/>-affecting properties that WPF expects to be stable at load
        /// time, and the plan's toggle-timing rule means this is decided once, at reparent time,
        /// not re-evaluated later for an already-open host frame.
        /// </summary>
        public void ConfigureResizability(bool resizable)
        {
            ResizeMode = resizable ? ResizeMode.CanResizeWithGrip : ResizeMode.CanMinimize;

            // The custom resize-grip hit-test band (and PositionSocket's matching inset that
            // reserves a thin uncovered strip around the socket for it) is only meaningful when
            // the host is actually resizable. Leaving it enabled for a fixed-size host (crop-mode
            // picks always, and non-resizable child-HWND picks) reserved an unnecessary
            // gripInsetX/gripInsetY margin around the socket with nothing painted into it,
            // producing a visible blank/white border around the embedded content for no reason.
            _customResizeGripsEnabled = resizable;
        }

        /// <summary>
        /// Sets <see cref="TargetHwnd"/>, resizes this host frame to fit the target's original
        /// window dimensions, then re-runs <see cref="PositionSocket"/> to align the socket (and
        /// the already-embedded target) within the newly-sized client area.
        /// </summary>
        /// <remarks>
        /// REVISED (painting/ghosting report): an earlier revision of this method resized the
        /// *target* to fill the socket on every <c>PositionSocket</c> call. That deviated from the
        /// plan's PowerToys-verified mechanics (§8 step 5), which never resize the target at all
        /// (<c>Reparent()</c>'s <c>SetWindowPos</c> uses <c>SWP_NOSIZE</c>) — and repeatedly
        /// resizing a still-top-level-styled (only just re-parented) native window like Notepad
        /// without a full non-client invalidate left stale/cascaded ghost title-bar artifacts.
        /// The correct approach (matching PowerToys) is the other way around: size the *host frame*
        /// to fit the target once at attach time, and never resize the target afterward — only
        /// reposition it (see <see cref="PositionSocket"/>).
        /// </remarks>
        public void AttachTarget(
            IntPtr targetHwnd,
            int? contentWidthPx = null,
            int? contentHeightPx = null,
            int targetOffsetX = 0,
            int targetOffsetY = 0,
            bool resizeTargetToSocket = true,
            uint targetProcessId = 0,
            DateTime targetProcessStartTimeUtc = default,
            string? targetClassName = null,
            string? targetAutomationRuntimeId = null,
            CapturedIdentitySnapshot? capturedTargetIdentity = null,
            bool supportsOriginalReopenToggle = false)
        {
            TargetHwnd = targetHwnd;
            _targetOffsetX = targetOffsetX;
            _targetOffsetY = targetOffsetY;
            _resizeTargetToSocket = resizeTargetToSocket;
            _targetProcessId = targetProcessId;
            _targetProcessStartTimeUtc = targetProcessStartTimeUtc;
            _targetClassName = targetClassName;
            _targetAutomationRuntimeId = targetAutomationRuntimeId;
            _capturedTargetIdentity = capturedTargetIdentity;
            _supportsOriginalReopenToggle = supportsOriginalReopenToggle;
            _isOriginalTemporarilyReopened = false;
            UpdateOriginalReopenUi();
            EnsureAltF4Hook();

            // Reset resize tracking for the newly-attached target: its initial size is established
            // via ResizeHostToFitContent below (host frame sized to fit it), not via the
            // socket-resize path in PositionSocket(), so there is no "last applied" size yet.
            _lastAppliedTargetWidth = -1;
            _lastAppliedTargetHeight = -1;

            if (targetHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(targetHwnd, out var targetRect))
            {
                int targetWidth = targetRect.Right - targetRect.Left;
                int targetHeight = targetRect.Bottom - targetRect.Top;
                int hostContentWidth = contentWidthPx ?? targetWidth;
                int hostContentHeight = contentHeightPx ?? targetHeight;
                ResizeHostToFitContent(hostContentWidth, hostContentHeight);

                // The host frame is now sized to exactly fit the target at its original dimensions,
                // so the socket's resulting size (computed in PositionSocket below) should already
                // match the target's current size — record it as "already applied" so the first
                // PositionSocket() call below doesn't immediately (and redundantly) resize the
                // target again.
                _lastAppliedTargetWidth = resizeTargetToSocket ? targetWidth : -1;
                _lastAppliedTargetHeight = resizeTargetToSocket ? targetHeight : -1;
            }

            PositionSocket();
        }

        /// <summary>
        /// Grows/shrinks this WPF window so that <c>SocketHost</c>'s content area ends up exactly
        /// <paramref name="contentWidthPx"/> x <paramref name="contentHeightPx"/> device pixels —
        /// i.e. big enough to hold the target at its original size without the target itself ever
        /// needing to be resized. Accounts for this window's own chrome (title bar, borders, the
        /// control-strip row above SocketHost) by measuring the current difference between the
        /// window's overall size and SocketHost's current content size.
        /// </summary>
        private void ResizeHostToFitContent(int contentWidthPx, int contentHeightPx)
        {
            if (SocketHost is null)
            {
                return;
            }

            var source = PresentationSource.FromVisual(this);
            double dpiScaleX = 1.0, dpiScaleY = 1.0;
            if (source?.CompositionTarget is not null)
            {
                dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            double contentWidthDip = contentWidthPx / dpiScaleX;
            double contentHeightDip = contentHeightPx / dpiScaleY;

            double chromeWidth = ActualWidth - SocketHost.ActualWidth;
            double chromeHeight = ActualHeight - SocketHost.ActualHeight;

            Width = Math.Max(1, contentWidthDip + chromeWidth);
            Height = Math.Max(1, contentHeightDip + chromeHeight);
        }

        /// <summary>
        /// Raised when the user clicks the Close/Restore button, or when the window is closing
        /// via any other path (Alt+F4, chrome close) — the caller must restore the target's
        /// original state in response to this event (via its own ReparentEngine instance) before
        /// this window finishes closing, so the target is never left orphaned.
        ///
        /// The handler must set <see cref="RestoreOutcomeEventArgs.Outcome"/> so
        /// <see cref="OnClosing"/> can destroy the socket only after detachment or target loss is
        /// proven.
        /// </summary>
        public event EventHandler<RestoreOutcomeEventArgs>? RestoreRequested;
        public event EventHandler? ReopenOriginalToggleRequested;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
            StateChanged += OnWindowStateChanged;

            CreateSocket(hwnd);

            if (_socketHwnd == IntPtr.Zero)
            {
                DebugLog("Socket CreateWindowEx failed; GetLastError=" + Marshal.GetLastWin32Error());
                return;
            }

            _overlayLocationChangedHandler = (_, _) => PositionOwnedOverlay();
            _overlaySizeChangedHandler = (_, _) =>
            {
                if (_overlayResizeRepositionPending)
                {
                    return;
                }

                _overlayResizeRepositionPending = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    try
                    {
                        PositionSocket();
                        PositionOwnedOverlay();
                    }
                    finally
                    {
                        _overlayResizeRepositionPending = false;
                    }
                }));
            };
            LocationChanged += _overlayLocationChangedHandler;
            SizeChanged += _overlaySizeChangedHandler;

            // BUG FIX (blank-window report): calling PositionSocket() synchronously here reads
            // SocketHost.ActualWidth/ActualHeight and PointToScreen before WPF has finished
            // committing layout for this Loaded pass, so the socket was frequently being sized to
            // near-zero. Defer the first call to DispatcherPriority.Loaded (runs after layout/
            // render for this pass completes) so the socket gets its real, final size before
            // SocketReady fires and the caller reparents into it.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                PositionSocket();
                CreateOwnedOverlay(hwnd);
                PositionOwnedOverlay();
                StartOverlayHoverPollingIfNeeded();
                SocketReady?.Invoke(this, EventArgs.Empty);
            }));
        }

        private bool _restoreRequestedOnClose;
        private RestoreOutcome _restoreOutcome;
        private bool _supportsOriginalReopenToggle;
        private bool _isOriginalTemporarilyReopened;

        /// <summary>
        /// Lets a caller that already restored a target itself record its explicit outcome before
        /// closing the host, avoiding a second restore attempt during <see cref="OnClosing"/>.
        /// </summary>
        public void NotifyRestoreAlreadyHandled(RestoreOutcome outcome)
        {
            _restoreRequestedOnClose = true;
            _restoreOutcome = outcome;
        }

        public void SetOriginalTemporarilyReopened(bool isReopened)
        {
            _isOriginalTemporarilyReopened = isReopened;
            if (!isReopened && !IsTargetAttachedToSocket())
            {
                TargetHwnd = IntPtr.Zero;
            }

            if (isReopened)
            {
                // BUG FIX (stale-content report): the caller just detached the target from the
                // socket (SetParent back to the desktop) so the user can pick again from the
                // reopened original. The first attempt at this fix redrew the native socket
                // window itself, but the socket has no background brush (hbrBackground is
                // IntPtr.Zero in its WNDCLASS) and nothing left to paint once its child is gone —
                // redrawing it is a no-op. The stale pixels are actually left behind on the WPF
                // host window's own top-level surface: DWM excluded that screen region while the
                // native child occupied it, and nothing tells the *host* window to repaint that
                // region now that the exclusion is gone. Invalidate/redraw the host's own HWND
                // (not the socket) to force it.
                IntPtr hostHwnd = _hwndSource?.Handle ?? IntPtr.Zero;
                if (hostHwnd != IntPtr.Zero)
                {
                    NativeMethods.RedrawWindow(
                        hostHwnd,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE | NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_UPDATENOW);
                }

                // WPF itself never painted behind the native child (the "airspace" it occupied is
                // excluded from WPF's own render pass), so nudging only the native HWND isn't
                // always sufficient once WPF's cached visual layer is composited back in — also
                // force WPF's own visual tree to re-render the vacated area.
                InvalidateVisual();
                SocketHost?.InvalidateVisual();
            }

            UpdateOriginalReopenUi();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Safety net: if the user closes the host frame via the window chrome/Alt+F4 instead
            // of the Close/Restore button, still ask the caller to restore the target rather than
            // leaving it orphaned as a WS_CHILD of a window that's about to be destroyed.
            if (!_restoreRequestedOnClose)
            {
                _restoreRequestedOnClose = true;

                var args = new RestoreOutcomeEventArgs(
                    TargetHwnd == IntPtr.Zero ? RestoreOutcome.TargetGone : RestoreOutcome.FailedStillAttached);
                RestoreRequested?.Invoke(this, args);
                _restoreOutcome = args.Outcome;
            }

            if (_restoreOutcome == RestoreOutcome.FailedStillAttached)
            {
                // Keep the WPF host, native socket, and controller tracking alive so the user can
                // retry restoration. Closing the socket would destroy a target still attached to it.
                _restoreRequestedOnClose = false;
                e.Cancel = true;
                return;
            }

            if (_overlayLocationChangedHandler is not null)
            {
                LocationChanged -= _overlayLocationChangedHandler;
                _overlayLocationChangedHandler = null;
            }

            if (_overlaySizeChangedHandler is not null)
            {
                SizeChanged -= _overlaySizeChangedHandler;
                _overlaySizeChangedHandler = null;
            }

            _hwndSource?.RemoveHook(WndProc);
            StateChanged -= OnWindowStateChanged;
            RemoveAltF4Hook();
            StopOverlayAnimation();

            TearDownOverlayContent();

            if (_overlayHwnd != IntPtr.Zero)
            {
                s_overlayOwnersByHwnd.Remove(_overlayHwnd);
                NativeMethods.DestroyWindow(_overlayHwnd);
                _overlayHwnd = IntPtr.Zero;
            }

            StopOverlayHoverInfrastructure();

            if (_targetResizeDebounceTimer is not null)
            {
                _targetResizeDebounceTimer.Stop();
                _targetResizeDebounceTimer = null;
            }

            // BUG FIX (destroy-on-failed-restore report): destroying a window also destroys its
            // children (Win32 semantics). If the restore's SetParent-back call failed, the target
            // may still be a WS_CHILD of _socketHwnd at this point — destroying the socket here
            // would silently destroy the user's actual embedded window instead of merely leaving
            // it orphaned/floating. Leak the socket instead (harmless/recoverable — it's just a
            // native window that will go away when this process exits) whenever the restore is
            // known to have failed to unparent the target.
            if (_socketHwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_socketHwnd);
                _socketHwnd = IntPtr.Zero;
            }
        }

        private void OnCloseRestoreClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnReopenOriginalClick(object sender, RoutedEventArgs e)
        {
            ReopenOriginalToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateOriginalReopenUi()
        {
            if (OriginalTemporarilyReopenedNotice is null)
            {
                return;
            }

            bool showToggle = _supportsOriginalReopenToggle;
            if (_reopenOriginalButton is not null)
            {
                _reopenOriginalButton.Visibility = showToggle ? Visibility.Visible : Visibility.Collapsed;
                if (_reopenOriginalGlyph is not null)
                {
                    _reopenOriginalGlyph.Text = _isOriginalTemporarilyReopened ? "\uE8A9" : "\uE8A7";
                }

                _reopenOriginalButton.ToolTip = _isOriginalTemporarilyReopened
                    ? "Hide original again"
                    : "Open original for more picking";
            }

            OriginalTemporarilyReopenedNotice.Visibility = _isOriginalTemporarilyReopened
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void EnsureAltF4Hook()
        {
            if (_altF4Hook != IntPtr.Zero)
            {
                return;
            }

            _altF4Proc = AltF4KeyboardProc;
            _altF4Hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _altF4Proc,
                IntPtr.Zero,
                0);
            if (_altF4Hook == IntPtr.Zero)
            {
                DebugLog($"EnsureAltF4Hook: SetWindowsHookEx failed, GetLastError={Marshal.GetLastWin32Error()}");
            }
        }

        private void RemoveAltF4Hook()
        {
            if (_altF4Hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_altF4Hook);
                _altF4Hook = IntPtr.Zero;
            }

            _altF4Proc = null;
        }

        private IntPtr AltF4KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var key = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                bool isSysKeyDown = wParam == new IntPtr(NativeMethods.WM_SYSKEYDOWN);
                bool targetAttached = IsTargetAttachedToSocket();
                bool targetFocused = targetAttached && IsTargetFocused();
                bool matchedAltF4 = isSysKeyDown && targetFocused && key.vkCode == NativeMethods.VK_F4 && (key.flags & NativeMethods.LLKHF_ALTDOWN) != 0;
                if (matchedAltF4)
                {
                    if (!_altF4ClosePending)
                    {
                        _altF4ClosePending = true;
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            _altF4ClosePending = false;
                            Close();
                        }));
                    }

                    return new IntPtr(1);
                }
            }

            IntPtr nextResult = NativeMethods.CallNextHookEx(_altF4Hook, nCode, wParam, lParam);
            return nextResult;
        }

        private bool IsTargetAttachedToSocket()
        {
            return TargetHwnd != IntPtr.Zero &&
                _socketHwnd != IntPtr.Zero &&
                NativeMethods.IsWindow(TargetHwnd) &&
                NativeMethods.GetParent(TargetHwnd) == _socketHwnd;
        }

        private bool IsTargetFocused()
        {
            var threadInfo = new NativeMethods.GUITHREADINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
            };
            return NativeMethods.GetGUIThreadInfo(0, ref threadInfo) &&
                (threadInfo.hwndFocus == TargetHwnd ||
                 NativeMethods.IsChild(TargetHwnd, threadInfo.hwndFocus));
        }

        private void CreateOwnedOverlay(IntPtr ownerHwnd)
        {
            IntPtr overlayBackgroundBrush = IntPtr.Zero;
            if (!s_registeredWindowClasses.Contains(OverlayWindowClassName))
            {
                overlayBackgroundBrush = NativeMethods.CreateSolidBrush(0x0000FFFF);
            }
            RegisterSocketClassOnce(OverlayWindowClassName, overlayBackgroundBrush);

            _overlayHwnd = NativeMethods.CreateWindowEx(
                NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW,
                OverlayWindowClassName,
                string.Empty,
                NativeMethods.WS_POPUP,
                0, 0, 1, 1,
                ownerHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_overlayHwnd == IntPtr.Zero)
            {
                DebugLog("Overlay CreateWindowEx failed; GetLastError=" + Marshal.GetLastWin32Error());
                return;
            }

            s_overlayOwnersByHwnd[_overlayHwnd] = new WeakReference<ReparentHostWindow>(this);

            _overlayCurrentAlpha = 0;
            _overlayCurrentOffsetY = GetOverlaySlideOffsetPixels();

            bool layeredOk = NativeMethods.SetLayeredWindowAttributes(
                _overlayHwnd,
                0x0000FFFF,
                _overlayCurrentAlpha,
                NativeMethods.LWA_ALPHA);
            if (!layeredOk)
            {
                DebugLog("SetLayeredWindowAttributes failed; GetLastError=" + Marshal.GetLastWin32Error());
            }

            AttachOverlayContent();
        }

        private void AttachOverlayContent()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            // WPF cannot safely attach a RootVisual to an arbitrary top-level HWND created by raw
            // CreateWindowEx; HwndSource.FromHwnd only wraps a WPF-owned source. Keep the existing
            // raw overlay window/WndProc unchanged and host WPF buttons inside it through a child
            // HwndSource so the parent popup still receives the same hover/cursor/MA_NOACTIVATE
            // message stream Piece 2/3 already depend on.
            var parameters = new HwndSourceParameters("WindowWorksReparentOverlayContent")
            {
                ParentWindow = _overlayHwnd,
                WindowStyle = (int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS)
            };

            _overlayContentSource = new HwndSource(parameters)
            {
                RootVisual = BuildOverlayRootVisual()
            };
            _overlayContentSource.AddHook(OverlayContentWndProc);

            UpdateOverlayWindowStateGlyph();
            UpdateOriginalReopenUi();
        }

        private Visual BuildOverlayRootVisual()
        {
            _overlayRootGrid = new Grid
            {
                Height = OverlayHeightDip,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = OverlayPanelBackgroundBrush
            };
            _overlayRootGrid.PreviewMouseLeftButtonDown += OnOverlayRootGridPreviewMouseLeftButtonDown;

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 4, 4)
            };

            _reopenOriginalButton = CreateOverlayIconButton("\uE8A7", "Open original for more picking", OnReopenOriginalClick, out _reopenOriginalGlyph);
            _minimizeButton = CreateOverlayIconButton("\uE921", "Minimize", OnMinimizeClick);
            _maximizeRestoreButton = CreateOverlayIconButton("\uE922", "Maximize", OnMaximizeRestoreClick, out _maximizeRestoreGlyph);
            _closeRestoreButton = CreateOverlayIconButton("\uE8BB", "Close/Restore", OnCloseRestoreClick, applyTrailingMargin: false);

            buttonPanel.Children.Add(_reopenOriginalButton);
            buttonPanel.Children.Add(_minimizeButton);
            buttonPanel.Children.Add(_maximizeRestoreButton);
            buttonPanel.Children.Add(_closeRestoreButton);
            _overlayRootGrid.Children.Add(buttonPanel);
            return _overlayRootGrid;
        }

        private Button CreateOverlayIconButton(string glyph, string toolTip, RoutedEventHandler onClick, bool applyTrailingMargin = true)
        {
            return CreateOverlayIconButton(glyph, toolTip, onClick, out _, applyTrailingMargin);
        }

        private Button CreateOverlayIconButton(string glyph, string toolTip, RoutedEventHandler onClick, out TextBlock glyphBlock, bool applyTrailingMargin = true)
        {
            glyphBlock = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = OverlayButtonForegroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            var button = new Button
            {
                Width = OverlayButtonSizeDip,
                Height = OverlayButtonSizeDip,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, applyTrailingMargin ? OverlayButtonMarginDip : 0, 0),
                Background = OverlayButtonBackgroundBrush,
                BorderBrush = OverlayButtonBorderBrush,
                Foreground = OverlayButtonForegroundBrush,
                BorderThickness = new Thickness(1),
                Focusable = false,
                Content = glyphBlock,
                ToolTip = toolTip
            };
            button.Click += onClick;
            return button;
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnOverlayRootGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source &&
                FindAncestorOrSelf<Button>(source) is not null)
            {
                return;
            }

            if (_hwndSource?.Handle is not IntPtr hostHwnd || hostHwnd == IntPtr.Zero)
            {
                return;
            }

            const int WM_NCLBUTTONDOWN = 0x00A1;
            const int HTCAPTION = 2;

            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(hostHwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            e.Handled = true;
        }

        private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                _overlayIdleHideTimer?.Stop();
                _overlayIdleHidePending = false;
                _overlayAwaitingMotionToReshow = false;
                HideOverlay();
            }

            UpdateOverlayWindowStateGlyph();
        }

        private void UpdateOverlayWindowStateGlyph()
        {
            if (_maximizeRestoreGlyph is null || _maximizeRestoreButton is null)
            {
                return;
            }

            bool maximized = WindowState == WindowState.Maximized;
            _maximizeRestoreGlyph.Text = maximized ? "\uE923" : "\uE922";
            _maximizeRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
        }

        private void EnsureOverlayHoverInfrastructure()
        {
            if (_overlayHoverPollTimer is null)
            {
                _overlayHoverPollTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = OverlayHoverPollInterval
                };
                _overlayHoverPollTimer.Tick += (_, _) => PollOverlayTriggerZone();
            }

            if (_overlayIdleHideTimer is null)
            {
                _overlayIdleHideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = OverlayIdleHideDelay
                };
                _overlayIdleHideTimer.Tick += (_, _) =>
                {
                    _overlayIdleHideTimer!.Stop();
                    _overlayIdleHidePending = false;

                    // Video-player-style autohide: do NOT hide while the cursor is actually
                    // resting over the overlay itself (e.g. hovering a button without moving) —
                    // only idle time spent outside the overlay should trigger autohide. Restart
                    // the timer so a subsequent idle period (once the cursor leaves) still hides
                    // it after the same delay.
                    if (_isCursorOverOverlayWindow)
                    {
                        RestartOverlayIdleHideTimer();
                        return;
                    }

                    if (NativeMethods.IsWindowVisible(_overlayHwnd))
                    {
                        _overlayAwaitingMotionToReshow = true;
                        _isCursorOverOverlayClientArea = false;

                        _isCursorOverOverlayContentWindow = false;

                        _overlayMouseLeaveTrackingActive = false;

                        _overlayContentMouseLeaveTrackingActive = false;
                        HideOverlay();
                    }
                };
            }
        }

        private void StartOverlayHoverPollingIfNeeded()
        {
            if (_overlayHwnd == IntPtr.Zero || _hwndSource?.Handle == IntPtr.Zero)
            {
                return;
            }

            EnsureOverlayHoverInfrastructure();

            if (_overlayHoverPollTimer is not null && !_overlayHoverPollTimer.IsEnabled)
            {
                _overlayHoverPollTimer.Start();
            }

            PollOverlayTriggerZone();
        }

        private void StopOverlayHoverInfrastructure()
        {
            if (_overlayHoverPollTimer is not null)
            {
                _overlayHoverPollTimer.Stop();
                _overlayHoverPollTimer = null;
            }

            if (_overlayIdleHideTimer is not null)
            {
                _overlayIdleHideTimer.Stop();
                _overlayIdleHideTimer = null;
            }

            if (_overlayAnimationTimer is not null)
            {
                _overlayAnimationTimer.Stop();
                _overlayAnimationTimer = null;
            }

            _isCursorInOverlayTriggerZone = false;

            _isCursorOverOverlayClientArea = false;

            _isCursorOverOverlayContentWindow = false;

            _overlayIdleHidePending = false;

            _overlayMouseLeaveTrackingActive = false;

            _overlayContentMouseLeaveTrackingActive = false;
            _overlayAwaitingMotionToReshow = false;
            _lastOverlayObservedCursorPoint = null;
            _overlayCurrentAlpha = 0;
            _overlayCurrentOffsetY = GetOverlaySlideOffsetPixels();
        }

        private void PollOverlayTriggerZone()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr hostHwnd = _hwndSource?.Handle ?? IntPtr.Zero;
            bool inTriggerZone = false;

            if (IsVisible &&
                WindowState != WindowState.Minimized &&
                hostHwnd != IntPtr.Zero &&
                NativeMethods.IsWindowVisible(hostHwnd) &&
                NativeMethods.GetClientRect(hostHwnd, out var clientRect) &&
                NativeMethods.GetCursorPos(out var cursorPoint))
            {
                NativeMethods.POINT motionAnchor = _lastOverlayObservedCursorPoint ?? cursorPoint;
                bool cursorMoved = !_lastOverlayObservedCursorPoint.HasValue ||
                    Math.Abs(motionAnchor.X - cursorPoint.X) > OverlayCursorMotionToleranceDevicePixels ||
                    Math.Abs(motionAnchor.Y - cursorPoint.Y) > OverlayCursorMotionToleranceDevicePixels;
                if (cursorMoved)
                {
                    _lastOverlayObservedCursorPoint = cursorPoint;
                }

                var clientPoint = cursorPoint;
                if (NativeMethods.ScreenToClient(hostHwnd, ref clientPoint))
                {
                    int clientWidth = Math.Max(0, clientRect.Right - clientRect.Left);
                    int clientHeight = Math.Max(0, clientRect.Bottom - clientRect.Top);
                    int triggerHeight = (int)Math.Ceiling(clientHeight * OverlayTriggerZoneHeightFraction);
                    IntPtr windowAtCursor = NativeMethods.WindowFromPoint(cursorPoint);
                    if (windowAtCursor == _overlayHwnd)
                    {
                        inTriggerZone = false;
                        _isCursorInOverlayTriggerZone = false;
                        if (cursorMoved)
                        {
                            OnOverlayCursorMotion(false);
                        }
                        UpdateOverlayVisibilityState();
                        return;
                    }

                    IntPtr cursorRoot = windowAtCursor != IntPtr.Zero
                        ? NativeMethods.GetAncestor(windowAtCursor, NativeMethods.GA_ROOT)
                        : IntPtr.Zero;
                    inTriggerZone =
                        clientWidth > 0 &&
                        clientHeight > 0 &&
                        clientPoint.X >= 0 &&
                        clientPoint.X < clientWidth &&
                        clientPoint.Y >= 0 &&
                        clientPoint.Y < Math.Max(1, triggerHeight) &&
                        cursorRoot == hostHwnd;

                    if (cursorMoved && (inTriggerZone || _isCursorOverOverlayWindow))
                    {
                        _isCursorInOverlayTriggerZone = inTriggerZone;
                        OnOverlayCursorMotion(inTriggerZone);
                    }
                }
            }

            _isCursorInOverlayTriggerZone = inTriggerZone;
            UpdateOverlayVisibilityState();
        }

        private void OnOverlayCursorMotion(bool cursorInTriggerZone)
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            bool canShowFromMotion =
                cursorInTriggerZone ||
                _isCursorOverOverlayWindow ||
                (NativeMethods.IsWindowVisible(_overlayHwnd) && !_overlayAwaitingMotionToReshow);

            if (canShowFromMotion)
            {
                _overlayAwaitingMotionToReshow = false;
                ShowOverlay();
                RestartOverlayIdleHideTimer();
            }
        }

        private void RestartOverlayIdleHideTimer()
        {
            if (_overlayIdleHideTimer is null)
            {
                return;
            }

            _overlayIdleHideTimer.Stop();
            _overlayIdleHideTimer.Start();
            _overlayIdleHidePending = true;
        }

        private void UpdateOverlayVisibilityState()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            bool isCursorEngaged = _isCursorInOverlayTriggerZone || _isCursorOverOverlayWindow;
            if (isCursorEngaged && !NativeMethods.IsWindowVisible(_overlayHwnd) && !_overlayAwaitingMotionToReshow)
            {
                ShowOverlay();
                RestartOverlayIdleHideTimer();
                return;
            }

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                _overlayIdleHideTimer?.Stop();
                _overlayIdleHidePending = false;
                _overlayAwaitingMotionToReshow = false;
                HideOverlay();
                return;
            }

            if (_overlayIdleHideTimer is null)
            {
                return;
            }

            if (isCursorEngaged || !NativeMethods.IsWindowVisible(_overlayHwnd) || _overlayIdleHidePending)
            {
                return;
            }

            RestartOverlayIdleHideTimer();
        }

        private void ShowOverlay()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            if (_overlayCurrentAlpha >= OverlayTargetAlpha &&
                _overlayCurrentOffsetY == 0 &&
                _overlayAnimationTimer is null &&
                NativeMethods.IsWindowVisible(_overlayHwnd))
            {
                return;
            }

            StartOverlayAnimation(show: true);
        }

        private void HideOverlay()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            if (_overlayCurrentAlpha == 0 &&
                _overlayCurrentOffsetY == GetOverlaySlideOffsetPixels() &&
                _overlayAnimationTimer is null &&
                !NativeMethods.IsWindowVisible(_overlayHwnd))
            {
                return;
            }

            StartOverlayAnimation(show: false);
        }

        private void PositionOwnedOverlay()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            var source = PresentationSource.FromVisual(this);
            double dpiScaleY = 1.0;
            if (source?.CompositionTarget is not null)
            {
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            var hostHwnd = _hwndSource?.Handle ?? new WindowInteropHelper(this).Handle;
            var topLeft = PointToScreen(new Point(0, 0));
            int left = (int)Math.Round(topLeft.X);
            int top = (int)Math.Round(topLeft.Y);
            int right = left;
            int bottom = top;

            if (hostHwnd == IntPtr.Zero)
            {
                DebugLog("PositionOwnedOverlay: host HWND unavailable; overlay geometry fell back to degenerate size.");
            }
            else if (NativeMethods.GetClientRect(hostHwnd, out var clientRect))
            {
                var clientTopLeft = new NativeMethods.POINT { X = clientRect.Left, Y = clientRect.Top };
                var clientTopRight = new NativeMethods.POINT { X = clientRect.Right, Y = clientRect.Top };
                var clientBottomLeft = new NativeMethods.POINT
                {
                    X = clientRect.Left,
                    Y = clientRect.Top + (int)Math.Round(OverlayHeightDip * dpiScaleY)
                };

                if (NativeMethods.ClientToScreen(hostHwnd, ref clientTopLeft) &&
                    NativeMethods.ClientToScreen(hostHwnd, ref clientTopRight) &&
                    NativeMethods.ClientToScreen(hostHwnd, ref clientBottomLeft))
                {
                    left = clientTopLeft.X;
                    top = clientTopLeft.Y;
                    right = clientTopRight.X;
                    bottom = clientBottomLeft.Y;
                }
                else
                {
                    DebugLog("PositionOwnedOverlay: ClientToScreen failed; overlay geometry fell back to degenerate size.");
                }
            }
            else
            {
                DebugLog($"PositionOwnedOverlay: GetClientRect failed; overlay geometry fell back to degenerate size. GetLastError={Marshal.GetLastWin32Error()}");
            }

            _overlayRestingLeft = left;
            _overlayRestingTop = top;
            _overlayRestingWidth = Math.Max(1, right - left);
            _overlayRestingHeight = Math.Max(1, bottom - top);
            ResizeOverlayContentHost();
            ApplyOverlayVisualState();
        }

        private void ResizeOverlayContentHost()
        {
            if (_overlayContentSource is null)
            {
                return;
            }

            bool ok = NativeMethods.SetWindowPos(
                _overlayContentSource.Handle,
                IntPtr.Zero,
                0,
                0,
                _overlayRestingWidth,
                _overlayRestingHeight,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            if (!ok)
            {
                DebugLog($"ResizeOverlayContentHost: SetWindowPos(content={_overlayContentSource.Handle}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }

            if (_overlayRootGrid is not null)
            {
                var source = PresentationSource.FromVisual(this);
                double dpiScaleX = 1.0;
                double dpiScaleY = 1.0;
                if (source?.CompositionTarget is not null)
                {
                    dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                    dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
                }

                _overlayRootGrid.Width = _overlayRestingWidth / dpiScaleX;
                _overlayRootGrid.Height = _overlayRestingHeight / dpiScaleY;
            }
        }

        private void TearDownOverlayContent()
        {
            if (_reopenOriginalButton is not null)
            {
                _reopenOriginalButton.Click -= OnReopenOriginalClick;
                _reopenOriginalButton = null;
            }

            if (_minimizeButton is not null)
            {
                _minimizeButton.Click -= OnMinimizeClick;
                _minimizeButton = null;
            }

            if (_maximizeRestoreButton is not null)
            {
                _maximizeRestoreButton.Click -= OnMaximizeRestoreClick;
                _maximizeRestoreButton = null;
            }

            if (_closeRestoreButton is not null)
            {
                _closeRestoreButton.Click -= OnCloseRestoreClick;
                _closeRestoreButton = null;
            }

            _reopenOriginalGlyph = null;
            _maximizeRestoreGlyph = null;
            _overlayRootGrid = null;

            if (_overlayContentSource is not null)
            {
                _overlayContentSource.RemoveHook(OverlayContentWndProc);
                _overlayContentSource.RootVisual = null;
                _overlayContentSource.Dispose();
                _overlayContentSource = null;
            }
        }

        private IntPtr OverlayContentWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_MOUSELEAVE = 0x02A3;
            const int MA_NOACTIVATE = 3;

            switch (msg)
            {
                case WM_MOUSEMOVE:
                    _isCursorOverOverlayContentWindow = true;
                    OnOverlayCursorMotion(_isCursorInOverlayTriggerZone);
                    if (!_overlayContentMouseLeaveTrackingActive)
                    {
                        var track = new NativeMethods.TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                            dwFlags = NativeMethods.TME_LEAVE,
                            hwndTrack = hwnd,
                            dwHoverTime = 0
                        };

                        if (NativeMethods.TrackMouseEvent(ref track))
                        {
                            _overlayContentMouseLeaveTrackingActive = true;
                        }
                    }

                    UpdateOverlayVisibilityState();
                    break;

                case WM_MOUSELEAVE:
                    _isCursorOverOverlayContentWindow = false;

                    _overlayContentMouseLeaveTrackingActive = false;
                    UpdateOverlayVisibilityState();
                    break;

                case WM_MOUSEACTIVATE:
                    handled = true;
                    return new IntPtr(MA_NOACTIVATE);
            }

            return IntPtr.Zero;
        }

        private void RefreshOverlayAnimationForCurrentDpi()
        {
            if (_overlayAnimationTimer is null || _overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            bool show = _overlayCurrentAlpha > 0;
            if (_overlayCurrentAlpha == 0)
            {
                show = NativeMethods.IsWindowVisible(_overlayHwnd);
            }

            StartOverlayAnimation(show);
        }

        private void StartOverlayAnimation(bool show)
        {
            int slideOffset = GetOverlaySlideOffsetPixels();
            bool visibleNow = NativeMethods.IsWindowVisible(_overlayHwnd);
            byte startAlpha = _overlayCurrentAlpha;
            int startOffsetY = _overlayCurrentOffsetY;
            byte targetAlpha = show ? OverlayTargetAlpha : (byte)0;
            int targetOffsetY = show ? 0 : slideOffset;

            if (show && !visibleNow)
            {
                _overlayCurrentAlpha = 0;
                _overlayCurrentOffsetY = slideOffset;
                ApplyOverlayVisualState();
                NativeMethods.ShowWindow(_overlayHwnd, NativeMethods.SW_SHOWNOACTIVATE);
                _overlayCurrentAlpha = startAlpha;
                _overlayCurrentOffsetY = startOffsetY;
                ApplyOverlayVisualState();
            }

            if (startAlpha == targetAlpha &&
                startOffsetY == targetOffsetY &&
                _overlayAnimationTimer is null &&
                visibleNow == show)
            {
                return;
            }

            StopOverlayAnimation();

            int generation = ++_overlayAnimationGeneration;
            var stopwatch = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = OverlayAnimationTickInterval
            };
            _overlayAnimationTimer = timer;
            timer.Tick += (_, _) =>
            {
                if (!ReferenceEquals(_overlayAnimationTimer, timer) || generation != _overlayAnimationGeneration || _overlayHwnd == IntPtr.Zero)
                {
                    timer.Stop();
                    return;
                }

                double rawProgress = stopwatch.Elapsed.TotalMilliseconds / OverlayAnimationDuration.TotalMilliseconds;
                double progress = Math.Max(0.0, Math.Min(1.0, rawProgress));
                double easedProgress = 1.0 - ((1.0 - progress) * (1.0 - progress)); // Ease-out keeps the overlay responsive near the end without a rigid linear feel.

                _overlayCurrentAlpha = (byte)Math.Round(startAlpha + ((targetAlpha - startAlpha) * easedProgress));
                _overlayCurrentOffsetY = (int)Math.Round(startOffsetY + ((targetOffsetY - startOffsetY) * easedProgress));
                ApplyOverlayVisualState();

                if (progress < 1.0)
                {
                    return;
                }

                timer.Stop();
                if (ReferenceEquals(_overlayAnimationTimer, timer))
                {
                    _overlayAnimationTimer = null;
                }

                _overlayCurrentAlpha = targetAlpha;
                _overlayCurrentOffsetY = targetOffsetY;
                ApplyOverlayVisualState();

                if (!show && generation == _overlayAnimationGeneration && _overlayHwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(_overlayHwnd, NativeMethods.SW_HIDE);
                }
            };

            timer.Start();
        }

        private void StopOverlayAnimation()
        {
            if (_overlayAnimationTimer is not null)
            {
                _overlayAnimationTimer.Stop();
                _overlayAnimationTimer = null;
            }
        }

        private void ApplyOverlayVisualState()
        {
            if (_overlayHwnd == IntPtr.Zero)
            {
                return;
            }

            bool layeredOk = NativeMethods.SetLayeredWindowAttributes(
                _overlayHwnd,
                0x0000FFFF,
                _overlayCurrentAlpha,
                NativeMethods.LWA_ALPHA);
            if (!layeredOk)
            {
                DebugLog("ApplyOverlayVisualState: SetLayeredWindowAttributes failed; GetLastError=" + Marshal.GetLastWin32Error());
            }

            bool ok = NativeMethods.SetWindowPos(
                _overlayHwnd,
                IntPtr.Zero,
                _overlayRestingLeft,
                _overlayRestingTop - _overlayCurrentOffsetY,
                _overlayRestingWidth,
                _overlayRestingHeight,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            if (!ok)
            {
                DebugLog($"ApplyOverlayVisualState: SetWindowPos(overlay={_overlayHwnd}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }
        }

        private int GetOverlaySlideOffsetPixels()
        {
            var source = PresentationSource.FromVisual(this);
            double dpiScaleY = 1.0;
            if (source?.CompositionTarget is not null)
            {
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            return Math.Max(1, (int)Math.Round(OverlaySlideOffsetDip * dpiScaleY));
        }

        private void CreateSocket(IntPtr parentHwnd)
        {
            const string className = "WindowWorksReparentSocket";
            RegisterSocketClassOnce(className);

            _socketHwnd = NativeMethods.CreateWindowEx(
                0,
                className,
                string.Empty,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0, 0, 1, 1,
                parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        private static readonly HashSet<string> s_registeredWindowClasses = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, NativeMethods.WndProcDelegate> s_wndProcDelegates = new(StringComparer.Ordinal);

        // Must stay rooted for the lifetime of the process: RegisterClass stores a native
        // function pointer derived from this delegate instance's marshalling thunk. If this
        // delegate were only a transient local, the GC could collect it later, leaving the
        // registered window class pointing at freed memory the next time a message is
        // dispatched to a native window of that class. Root one delegate per class name so a
        // future overlay-specific WndProc cannot overwrite the socket class's rooted delegate.

        private static void RegisterSocketClassOnce(string className, IntPtr backgroundBrush = default)
        {
            if (s_registeredWindowClasses.Contains(className))
            {
                return;
            }

            NativeMethods.WndProcDelegate wndProcDelegate =
                string.Equals(className, OverlayWindowClassName, StringComparison.Ordinal)
                    ? OverlayWndProc
                    : NativeMethods.DefWindowProc;
            s_wndProcDelegates[className] = wndProcDelegate;
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = wndProcDelegate,
                hbrBackground = backgroundBrush,
                lpszClassName = className
            };
            ushort atom = NativeMethods.RegisterClass(ref wc);
            if (atom == 0)
            {
                if (backgroundBrush != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(backgroundBrush);
                }

                // BUG FIX (blank-host-frame report, root cause): RegisterClass's DllImport had no
                // CharSet specified, defaulting to Ansi (RegisterClassA), while WNDCLASS's string
                // fields are marshaled as CharSet.Unicode above (see the [StructLayout] attribute)
                // — that mismatch corrupted the registration, RegisterClass returned 0 (failure),
                // and the failure went unnoticed because the return value was never checked. The
                // subsequent CreateWindowEx call then failed with ERROR_CANNOT_FIND_WND_CLASS
                // (1407) since no class by that name actually existed. Explicit CharSet.Unicode
                // below fixes the mismatch; this check ensures a future regression is logged
                // instead of silently failing again.
                DebugLog("RegisterClass failed; GetLastError=" + Marshal.GetLastWin32Error());
                return;
            }

            s_registeredWindowClasses.Add(className);
        }

        private static IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_MOUSEMOVE = 0x0200;
            const uint WM_MOUSELEAVE = 0x02A3;
            const uint WM_SETCURSOR = 0x0020;
            const uint WM_MOUSEACTIVATE = 0x0021;
            const int HTCLIENT = 1;
            const int MA_NOACTIVATE = 3;

            ReparentHostWindow? hostWindow = FindOverlayOwner(hWnd);
            if (hostWindow is null)
            {
                return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
            }
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    hostWindow._isCursorOverOverlayClientArea = true;
                    hostWindow.OnOverlayCursorMotion(hostWindow._isCursorInOverlayTriggerZone);
                    if (!hostWindow._overlayMouseLeaveTrackingActive)
                    {
                        var track = new NativeMethods.TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                            dwFlags = NativeMethods.TME_LEAVE,
                            hwndTrack = hWnd,
                            dwHoverTime = 0
                        };

                        if (NativeMethods.TrackMouseEvent(ref track))
                        {
                            hostWindow._overlayMouseLeaveTrackingActive = true;
                        }
                    }

                    hostWindow.UpdateOverlayVisibilityState();
                    break;

                case WM_MOUSELEAVE:
                    hostWindow._isCursorOverOverlayClientArea = false;

                    hostWindow._overlayMouseLeaveTrackingActive = false;
                    hostWindow.UpdateOverlayVisibilityState();
                    break;

                case WM_SETCURSOR:
                    if ((int)(lParam.ToInt64() & 0xFFFF) == HTCLIENT)
                    {
                        IntPtr cursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_SIZEALL);
                        if (cursor != IntPtr.Zero)
                        {
                            NativeMethods.SetCursor(cursor);
                            return new IntPtr(1);
                        }
                    }
                    break;

                case WM_MOUSEACTIVATE:
                    return new IntPtr(MA_NOACTIVATE);
            }

            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static ReparentHostWindow? FindOverlayOwner(IntPtr overlayHwnd)
        {
            if (!s_overlayOwnersByHwnd.TryGetValue(overlayHwnd, out var weakReference))
            {
                return null;
            }

            if (weakReference.TryGetTarget(out var host))
            {
                return host;
            }

            s_overlayOwnersByHwnd.Remove(overlayHwnd);
            return null;
        }

        private static T? FindAncestorOrSelf<T>(DependencyObject? source)
            where T : DependencyObject
        {
            while (source is not null)
            {
                if (source is T match)
                {
                    return match;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private static void FreezeOverlayBrushes()
        {
            if (OverlayPanelBackgroundBrush.CanFreeze && !OverlayPanelBackgroundBrush.IsFrozen)
            {
                OverlayPanelBackgroundBrush.Freeze();
            }

            if (OverlayButtonBorderBrush.CanFreeze && !OverlayButtonBorderBrush.IsFrozen)
            {
                OverlayButtonBorderBrush.Freeze();
            }
        }

        /// <summary>
        /// Sizes/positions the native socket window within the <c>SocketHost</c> Border's current
        /// layout rect (device pixels), leaving a thin uncovered band around all four sides for
        /// the host window's custom resize-hit-test area. Re-run on every SizeChanged so a Phase 1
        /// resizable host frame keeps the embedded target aligned (§8 step 5 equivalent applied
        /// continuously, not just at attach time).
        /// </summary>
        private void PositionSocket()
        {
            if (_socketHwnd == IntPtr.Zero || SocketHost is null)
            {
                return;
            }

            var topLeft = SocketHost.PointToScreen(new Point(0, 0));
            var source = PresentationSource.FromVisual(this);
            double dpiScaleX = 1.0, dpiScaleY = 1.0;
            if (source?.CompositionTarget is not null)
            {
                dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            var hostHwnd = new WindowInteropHelper(this).Handle;
            var hostTopLeft = new NativeMethods.POINT { X = (int)topLeft.X, Y = (int)topLeft.Y };
            NativeMethods.ScreenToClient(hostHwnd, ref hostTopLeft);

            int hostWidth = (int)Math.Max(1, SocketHost.ActualWidth * dpiScaleX);
            int hostHeight = (int)Math.Max(1, SocketHost.ActualHeight * dpiScaleY);
            int gripInsetX = _customResizeGripsEnabled
                ? Math.Max(1, (int)Math.Round(CustomResizeGripThicknessDip * dpiScaleX))
                : 0;
            int gripInsetY = _customResizeGripsEnabled
                ? Math.Max(1, (int)Math.Round(CustomResizeGripThicknessDip * dpiScaleY))
                : 0;

            int maxInsetX = Math.Max(0, (hostWidth - 1) / 2);
            int maxInsetY = Math.Max(0, (hostHeight - 1) / 2);
            gripInsetX = Math.Min(gripInsetX, maxInsetX);
            gripInsetY = Math.Min(gripInsetY, maxInsetY);

            int socketX = hostTopLeft.X + gripInsetX;
            int socketY = hostTopLeft.Y + gripInsetY;
            int width = Math.Max(1, hostWidth - (gripInsetX * 2));
            int height = Math.Max(1, hostHeight - (gripInsetY * 2));

            bool moveOk = NativeMethods.MoveWindow(_socketHwnd, socketX, socketY, width, height, true);
            if (!moveOk)
            {
                DebugLog($"PositionSocket: MoveWindow(socket={_socketHwnd}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }

            // BUG FIX (resize-doesn't-propagate report): the earlier revision here always used
            // SWP_NOSIZE, so once the host frame/socket was genuinely resized (not just moved),
            // the target was left at its stale pre-resize dimensions, producing leftover/misaligned
            // painting artifacts. Per the plan (§8 step 5's "moved up from Phase 4" resize-
            // re-application requirement, and the WM_DPICHANGED note at ~line 1092), the target
            // DOES need to be resized to fill the socket whenever the socket's size actually
            // changes — this is distinct from the earlier "resize target on every PositionSocket
            // call" bug, which resized unconditionally (including on every mere reposition/no-op),
            // repeatedly hitting a still-top-level-styled native window without a full invalidate
            // and producing cascaded ghost artifacts. The fix here only issues a resizing
            // SetWindowPos when width/height actually differ from what was last applied; a
            // reposition-only (or no-op) PositionSocket call still uses SWP_NOSIZE as before.
            if (IsCurrentTargetAttachedToSocket())
            {
                bool sizeChanged = width != _lastAppliedTargetWidth || height != _lastAppliedTargetHeight;

                if (!_resizeTargetToSocket || !sizeChanged)
                {
                    // Reposition-only (or no-op): apply immediately, same as before.
                    bool repositionOk = NativeMethods.SetWindowPos(
                        TargetHwnd,
                        IntPtr.Zero,
                        _targetOffsetX,
                        _targetOffsetY,
                        0,
                        0,
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    if (!repositionOk)
                    {
                        DebugLog($"PositionSocket: target reposition failed, target={TargetHwnd}, GetLastError={Marshal.GetLastWin32Error()}");
                    }
                }
                else
                {
                    // PERF: debounce the actual target resize so rapid intermediate SizeChanged
                    // ticks during an interactive host-frame drag-resize don't each issue their own
                    // target SetWindowPos — only the last size in a burst gets applied, once ticks
                    // pause briefly. The socket window itself was already resized live above.
                    ScheduleTargetResize(width, height);
                }
            }
        }

        private void ScheduleTargetResize(int width, int height)
        {
            _pendingTargetWidth = width;
            _pendingTargetHeight = height;

            if (_targetResizeDebounceTimer is null)
            {
                _targetResizeDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = TargetResizeDebounceDelay,
                };
                _targetResizeDebounceTimer.Tick += (_, _) =>
                {
                    _targetResizeDebounceTimer!.Stop();
                    ApplyPendingTargetResize();
                };
            }

            // Restarting the timer on every call is the debounce: only once ticks stop arriving
            // for TargetResizeDebounceDelay does the resize actually get applied.
            _targetResizeDebounceTimer.Stop();
            _targetResizeDebounceTimer.Start();
        }

        private int _pendingTargetWidth;
        private int _pendingTargetHeight;

        private void ApplyPendingTargetResize()
        {
            if (!_resizeTargetToSocket || !IsCurrentTargetAttachedToSocket())
            {
                return;
            }

            int width = _pendingTargetWidth;
            int height = _pendingTargetHeight;

            bool targetOk = NativeMethods.SetWindowPos(
                TargetHwnd,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            if (targetOk)
            {
                _lastAppliedTargetWidth = width;
                _lastAppliedTargetHeight = height;
            }
            else
            {
                DebugLog($"ApplyPendingTargetResize: SetWindowPos(target={TargetHwnd}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }
        }

        private bool IsCurrentTargetAttachedToSocket()
        {
            if (!IsTargetAttachedToSocket())
            {
                // Do not continue to move, resize, or activate an HWND that is no longer hosted here.
                TargetHwnd = IntPtr.Zero;
                return false;
            }

            if (!HasExpectedTargetIdentity())
            {
                return false;
            }

            return true;
        }

        private bool HasExpectedTargetIdentity()
        {
            if (_targetProcessId == 0 ||
            _targetProcessStartTimeUtc == default ||
            string.IsNullOrWhiteSpace(_targetClassName) ||
            string.IsNullOrWhiteSpace(_targetAutomationRuntimeId) ||
            _capturedTargetIdentity is null)
            {
            return false;
            }

            if (_capturedTargetIdentity.Value.IsInvalidated() ||
            _capturedTargetIdentity.Value.Hwnd != TargetHwnd ||
            _capturedTargetIdentity.Value.ProcessId != _targetProcessId ||
            _capturedTargetIdentity.Value.ProcessStartTimeUtc != _targetProcessStartTimeUtc ||
            !string.Equals(_capturedTargetIdentity.Value.ClassName, _targetClassName, StringComparison.Ordinal) ||
            !string.Equals(_capturedTargetIdentity.Value.AutomationRuntimeId, _targetAutomationRuntimeId, StringComparison.Ordinal))
            {
            return false;
            }

            try
            {
                NativeMethods.GetWindowThreadProcessId(TargetHwnd, out uint liveProcessId);
                if (liveProcessId != _targetProcessId)
                {
                    return false;
                }

                using var process = Process.GetProcessById((int)liveProcessId);
                if (process.StartTime.ToUniversalTime() != _targetProcessStartTimeUtc)
                {
                    return false;
                }

                var className = new StringBuilder(256);
                int length = NativeMethods.GetClassName(TargetHwnd, className, className.Capacity);
                return length > 0 &&
                    !string.IsNullOrWhiteSpace(className.ToString()) &&
                    string.Equals(_targetClassName, className.ToString(), StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                DebugLog($"HasExpectedTargetIdentity: target={TargetHwnd} validation failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int WM_ACTIVATE = 0x0006;
            const int WM_NCCALCSIZE = 0x0083;
            const int WM_NCHITTEST = 0x0084;
            const int MA_NOACTIVATE = 3;
            const int WA_ACTIVE = 1;
            const int WM_DPICHANGED = 0x02E0;

            // Plan requirement (?8's "New finding ? host frame WM_DPICHANGED handling", ~line
            // 1092): when the host frame moves to a monitor with different DPI scaling, Windows
            // sends WM_DPICHANGED with the OS-suggested new window rect packed into lParam as a
            // RECT*. Mirrors PowerToys' own DesktopWindow<T> base-class default handler
            // (SetWindowPos to that rect, SWP_NOZORDER | SWP_NOACTIVATE), then re-applies the
            // target's fill within the host's now-differently-scaled client area ? otherwise the
            // embedded target would be left at its stale pre-DPI-change size/position while the
            // host frame's own chrome resizes around it.
            if (msg == WM_DPICHANGED)
            {
                var suggestedRect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                bool resizeOk = NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    suggestedRect.Left,
                    suggestedRect.Top,
                    suggestedRect.Right - suggestedRect.Left,
                    suggestedRect.Bottom - suggestedRect.Top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                if (!resizeOk)
                {
                    DebugLog($"WndProc: WM_DPICHANGED SetWindowPos failed, GetLastError={Marshal.GetLastWin32Error()}");
                }

                // Re-run PositionSocket() after the host frame itself has been resized/repositioned
                // to the new DPI-suggested rect, so the socket (and, if its size actually changed,
                // the embedded target per the resize fix above) stays aligned/filled at the new
                // scale. Deferred via Dispatcher because WPF's own layout pass in response to the
                // just-issued SetWindowPos hasn't necessarily run yet at this point in WndProc.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    PositionSocket();
                    RefreshOverlayAnimationForCurrentDpi();
                    PositionOwnedOverlay();
                }));

                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                // Remove any residual native non-client frame reservation so the client area
                // fills the entire window; use the native maximized bit here because WPF's
                // WindowState property lags behind the live WM_NCCALCSIZE transition.
                nint style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
                bool isNativeMaximized = (((long)style) & NativeMethods.WS_MAXIMIZE) != 0;
                if (!isNativeMaximized)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            if (msg == WM_NCHITTEST && _customResizeGripsEnabled)
            {
                IntPtr hitTestResult = HitTestCustomResizeGrip(hwnd, lParam);
                if (hitTestResult != IntPtr.Zero)
                {
                    handled = true;
                    return hitTestResult;
                }
            }

            if (!IsCurrentTargetAttachedToSocket())
            {
                return IntPtr.Zero;
            }
            IntPtr target = TargetHwnd;

            if (msg == WM_MOUSEACTIVATE)
            {
                if (NativeMethods.GetForegroundWindow() != target)
                {
                    bool fgOk = NativeMethods.SetForegroundWindow(target);
                    if (!fgOk)
                    {
                        DebugLog($"WndProc: WM_MOUSEACTIVATE SetForegroundWindow(target={target}) failed, GetLastError={Marshal.GetLastWin32Error()}");
                    }
                }

                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            if (msg == WM_ACTIVATE)
            {
                bool isWaActive = (wParam.ToInt32() & 0xFFFF) == WA_ACTIVE;
                bool hostIsMinimizedPerMessage = ((wParam.ToInt64() >> 16) & 0xFFFF) != 0;
                bool allowForegroundForward = isWaActive && !hostIsMinimizedPerMessage && WindowState != WindowState.Minimized;
                if (allowForegroundForward)
                {
                    bool fgOk = NativeMethods.SetForegroundWindow(target);
                    if (!fgOk)
                    {
                        DebugLog($"WndProc: WM_ACTIVATE SetForegroundWindow(target={target}) failed, GetLastError={Marshal.GetLastWin32Error()}");
                    }
                }
            }

            return IntPtr.Zero;
        }

        private IntPtr HitTestCustomResizeGrip(IntPtr hwnd, IntPtr lParam)
        {
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (_overlayHwnd != IntPtr.Zero &&
                NativeMethods.IsWindowVisible(_overlayHwnd) &&
                NativeMethods.GetWindowRect(_overlayHwnd, out var overlayRect))
            {
                int overlayX = unchecked((short)(long)lParam);
                int overlayY = unchecked((short)((long)lParam >> 16));
                if (overlayX >= overlayRect.Left &&
                    overlayX < overlayRect.Right &&
                    overlayY >= overlayRect.Top &&
                    overlayY < overlayRect.Bottom)
                {
                    return IntPtr.Zero;
                }
            }

            if (!NativeMethods.GetClientRect(hwnd, out var clientRect))
            {
                return IntPtr.Zero;
            }

            var topLeft = new NativeMethods.POINT { X = clientRect.Left, Y = clientRect.Top };
            var bottomRight = new NativeMethods.POINT { X = clientRect.Right, Y = clientRect.Bottom };
            if (!NativeMethods.ClientToScreen(hwnd, ref topLeft) ||
                !NativeMethods.ClientToScreen(hwnd, ref bottomRight))
            {
                return IntPtr.Zero;
            }

            int clientWidth = Math.Max(0, bottomRight.X - topLeft.X);
            int clientHeight = Math.Max(0, bottomRight.Y - topLeft.Y);
            if (clientWidth <= 0 || clientHeight <= 0)
            {
                return IntPtr.Zero;
            }

            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            if (_hwndSource?.CompositionTarget is not null)
            {
                dpiScaleX = _hwndSource.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = _hwndSource.CompositionTarget.TransformToDevice.M22;
            }

            int gripWidth = Math.Max(1, (int)Math.Round(CustomResizeGripThicknessDip * dpiScaleX));
            int gripHeight = Math.Max(1, (int)Math.Round(CustomResizeGripThicknessDip * dpiScaleY));

            int x = unchecked((short)(long)lParam) - topLeft.X;
            int y = unchecked((short)((long)lParam >> 16)) - topLeft.Y;
            if (x < 0 || y < 0 || x >= clientWidth || y >= clientHeight)
            {
                return IntPtr.Zero;
            }

            bool onLeft = x < gripWidth;
            bool onRight = x >= clientWidth - gripWidth;
            bool onTop = y < gripHeight;
            bool onBottom = y >= clientHeight - gripHeight;

            if (onTop && onLeft)
            {
                return new IntPtr(HTTOPLEFT);
            }

            if (onTop && onRight)
            {
                return new IntPtr(HTTOPRIGHT);
            }

            if (onBottom && onLeft)
            {
                return new IntPtr(HTBOTTOMLEFT);
            }

            if (onBottom && onRight)
            {
                return new IntPtr(HTBOTTOMRIGHT);
            }

            if (onLeft)
            {
                return new IntPtr(HTLEFT);
            }

            if (onRight)
            {
                return new IntPtr(HTRIGHT);
            }

            if (onTop)
            {
                return new IntPtr(HTTOP);
            }

            if (onBottom)
            {
                return new IntPtr(HTBOTTOM);
            }

            return IntPtr.Zero;
        }

        // REVERTED (cascade/ghosting report): AttachThreadInput + SetFocus (added to try to fix
        // the no-caret/can't-type issue) deviated from the plan's PowerToys-verified activation
        // mechanics (§8 step 7 — SetForegroundWindow on the child target, nothing else) and is the
        // likely cause of a new regression: the host window repeatedly resized itself between its
        // normal size and near-full-screen on every click (see PositionSocket log entries flipping
        // socketRect between the expected size and ~screen size right after each WM_MOUSEACTIVATE/
        // WM_ACTIVATE), producing stacked "ghost" title-bar artifacts. Reverted to exactly what the
        // plan verified against real source — SetForegroundWindow only (see WndProc above). The
        // no-caret/can't-type issue is being investigated separately now that this regression is
        // isolated out.

        private static class NativeMethods
        {
            public const int GWL_STYLE = -16;
            public const uint WS_CHILD = 0x40000000;
            public const uint WS_POPUP = 0x80000000;
            public const uint WS_VISIBLE = 0x10000000;
            public const uint WS_CLIPCHILDREN = 0x02000000;
            public const uint WS_CLIPSIBLINGS = 0x04000000;
            public const uint WS_MAXIMIZE = 0x01000000;
            public const uint WS_EX_LAYERED = 0x00080000;
            public const uint WS_EX_TOOLWINDOW = 0x00000080;
            public const int WH_KEYBOARD_LL = 13;
            public const int WM_SYSKEYDOWN = 0x0104;
            public const uint VK_F4 = 0x73;
            public const uint LLKHF_ALTDOWN = 0x20;
            public const int SW_HIDE = 0;
            public const int SW_SHOWNOACTIVATE = 4;
            public const uint TME_LEAVE = 0x00000002;
            public const uint GA_ROOT = 2;
            public static readonly IntPtr IDC_SIZEALL = new(32646);

            [StructLayout(LayoutKind.Sequential)]
            public struct KBDLLHOOKSTRUCT
            {
                public uint vkCode;
                public uint scanCode;
                public uint flags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct GUITHREADINFO
            {
                public uint cbSize;
                public uint flags;
                public IntPtr hwndActive;
                public IntPtr hwndFocus;
                public IntPtr hwndCapture;
                public IntPtr hwndMenuOwner;
                public IntPtr hwndMoveSize;
                public IntPtr hwndCaret;
                public RECT rcCaret;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct TRACKMOUSEEVENT
            {
                public uint cbSize;
                public uint dwFlags;
                public IntPtr hwndTrack;
                public uint dwHoverTime;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct WINDOWPOS
            {
                public IntPtr hwnd;
                public IntPtr hwndInsertAfter;
                public int x;
                public int y;
                public int cx;
                public int cy;
                public uint flags;
            }

            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_FRAMECHANGED = 0x0020;
            public const uint LWA_ALPHA = 0x00000002;

            public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct WNDCLASS
            {
                public uint style;
                public WndProcDelegate lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                public string? lpszMenuName;
                public string lpszClassName;
            }

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateWindowEx(
                uint dwExStyle,
                string lpClassName,
                string lpWindowName,
                uint dwStyle,
                int x, int y, int nWidth, int nHeight,
                IntPtr hWndParent,
                IntPtr hMenu,
                IntPtr hInstance,
                IntPtr lpParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool DestroyWindow(IntPtr hWnd);

            public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(
                int idHook,
                LowLevelKeyboardProc lpfn,
                IntPtr hMod,
                uint dwThreadId);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

            public const uint RDW_INVALIDATE = 0x0001;
            public const uint RDW_ERASE = 0x0004;
            public const uint RDW_ALLCHILDREN = 0x0080;
            public const uint RDW_UPDATENOW = 0x0100;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

            [DllImport("user32.dll")]
            public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetFocus(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr GetFocus();

            [DllImport("user32.dll")]
            public static extern IntPtr GetParent(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern IntPtr WindowFromPoint(POINT point);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

            [DllImport("user32.dll")]
            public static extern IntPtr SetCursor(IntPtr hCursor);

            [DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern IntPtr CreateSolidBrush(uint color);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern bool DeleteObject(IntPtr hObject);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
            public static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);
        }
    }

    /// <summary>
    /// Event args for <see cref="ReparentHostWindow.RestoreRequested"/> (destroy-on-failed-restore
    /// fix, docs/REPARENT_FEATURE_PLAN.md): lets the restore handler report back whether the
    /// target's structural state, so <see cref="ReparentHostWindow.OnClosing"/> destroys the
    /// socket only after detachment or target loss has been proven.
    /// </summary>
    public sealed class RestoreOutcomeEventArgs : EventArgs
    {
        /// <summary>
        /// The restore outcome supplied by the controller. Defaults to
        /// <see cref="RestoreOutcome.FailedStillAttached"/> so an unhandled restore request is
        /// safe by default and cannot destroy user content.
        /// </summary>
        public RestoreOutcome Outcome { get; set; }

        public RestoreOutcomeEventArgs(RestoreOutcome outcome)
        {
            Outcome = outcome;
        }
    }

    public enum RestoreOutcome
    {
        /// <summary>
        /// Reparenting never attached the live target to this host socket, so the socket can be
        /// destroyed without restoring or affecting the target.
        /// </summary>
        NotAttached,

        TargetGone,
        Restored,
        FailedStillAttached
    }
}
