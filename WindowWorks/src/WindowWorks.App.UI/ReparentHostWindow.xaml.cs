using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
        private HwndSource? _hwndSource;

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

        public ReparentHostWindow()
        {
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
            CapturedIdentitySnapshot? capturedTargetIdentity = null)
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            CreateSocket(hwnd);

            if (_socketHwnd == IntPtr.Zero)
            {
                DebugLog("Socket CreateWindowEx failed; GetLastError=" + Marshal.GetLastWin32Error());
                return;
            }

            SizeChanged += (_, _) => PositionSocket();

            // BUG FIX (blank-window report): calling PositionSocket() synchronously here reads
            // SocketHost.ActualWidth/ActualHeight and PointToScreen before WPF has finished
            // committing layout for this Loaded pass, so the socket was frequently being sized to
            // near-zero. Defer the first call to DispatcherPriority.Loaded (runs after layout/
            // render for this pass completes) so the socket gets its real, final size before
            // SocketReady fires and the caller reparents into it.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                PositionSocket();
                SocketReady?.Invoke(this, EventArgs.Empty);
            }));
        }

        private bool _restoreRequestedOnClose;
        private RestoreOutcome _restoreOutcome;

        /// <summary>
        /// Lets a caller that already restored a target itself record its explicit outcome before
        /// closing the host, avoiding a second restore attempt during <see cref="OnClosing"/>.
        /// </summary>
        public void NotifyRestoreAlreadyHandled(RestoreOutcome outcome)
        {
            _restoreRequestedOnClose = true;
            _restoreOutcome = outcome;
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

            _hwndSource?.RemoveHook(WndProc);
            RemoveAltF4Hook();

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
            if (nCode >= 0 &&
                wParam == new IntPtr(NativeMethods.WM_SYSKEYDOWN) &&
                IsTargetAttachedToSocket() &&
                IsTargetFocused())
            {
                var key = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (key.vkCode == NativeMethods.VK_F4 &&
                    (key.flags & NativeMethods.LLKHF_ALTDOWN) != 0)
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

            return NativeMethods.CallNextHookEx(_altF4Hook, nCode, wParam, lParam);
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

        private static bool s_socketClassRegistered;

        // Must stay rooted for the lifetime of the process: RegisterClass stores a native
        // function pointer derived from this delegate instance's marshalling thunk. If this
        // delegate were only a transient local, the GC could collect it later, leaving the
        // registered window class pointing at freed memory the next time a message is
        // dispatched to a socket window of this class.
        private static NativeMethods.WndProcDelegate? s_wndProcDelegate;

        private static void RegisterSocketClassOnce(string className)
        {
            if (s_socketClassRegistered)
            {
                return;
            }

            s_wndProcDelegate = NativeMethods.DefWindowProc;
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = s_wndProcDelegate,
                lpszClassName = className
            };
            ushort atom = NativeMethods.RegisterClass(ref wc);
            if (atom == 0)
            {
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

            s_socketClassRegistered = true;
        }

        /// <summary>
        /// Sizes/positions the native socket window to exactly fill the <c>SocketHost</c> Border's
        /// current layout rect (device pixels), so the embedded target fills the host frame's
        /// content area below the control strip. Re-run on every SizeChanged so a Phase 1
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

            int width = (int)Math.Max(1, SocketHost.ActualWidth * dpiScaleX);
            int height = (int)Math.Max(1, SocketHost.ActualHeight * dpiScaleY);

            bool moveOk = NativeMethods.MoveWindow(_socketHwnd, hostTopLeft.X, hostTopLeft.Y, width, height, true);
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
            const int MA_NOACTIVATE = 3;
            const int WA_ACTIVE = 1;
            const int WM_DPICHANGED = 0x02E0;

            // Plan requirement (§8's "New finding — host frame WM_DPICHANGED handling", ~line
            // 1092): when the host frame moves to a monitor with different DPI scaling, Windows
            // sends WM_DPICHANGED with the OS-suggested new window rect packed into lParam as a
            // RECT*. Mirrors PowerToys' own DesktopWindow<T> base-class default handler
            // (SetWindowPos to that rect, SWP_NOZORDER | SWP_NOACTIVATE), then re-applies the
            // target's fill within the host's now-differently-scaled client area — otherwise the
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
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionSocket));

                handled = true;
                return IntPtr.Zero;
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

            if (msg == WM_ACTIVATE && (wParam.ToInt32() & 0xFFFF) == WA_ACTIVE)
            {
                bool fgOk = NativeMethods.SetForegroundWindow(target);
                if (!fgOk)
                {
                    DebugLog($"WndProc: WM_ACTIVATE SetForegroundWindow(target={target}) failed, GetLastError={Marshal.GetLastWin32Error()}");
                }
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
            public const uint WS_CHILD = 0x40000000;
            public const uint WS_VISIBLE = 0x10000000;
            public const uint WS_CLIPCHILDREN = 0x02000000;
            public const uint WS_CLIPSIBLINGS = 0x04000000;
            public const int WH_KEYBOARD_LL = 13;
            public const int WM_SYSKEYDOWN = 0x0104;
            public const uint VK_F4 = 0x73;
            public const uint LLKHF_ALTDOWN = 0x20;

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

            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_FRAMECHANGED = 0x0020;

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

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

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
            public static extern bool IsWindow(IntPtr hWnd);
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
