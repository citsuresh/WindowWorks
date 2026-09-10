using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WindowWorks.App.UI
{
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
        /// §6.5/§9's three-way fixed-size distinction, item 4): a whole top-level window pick
        /// should remain resizable (default), while an ancestor-chain child-HWND pick defaults to
        /// fixed-size (no resize grips/maximize affordance, drag-to-reposition only) unless the
        /// user has opted into the "Allow resizing reparented child elements" setting. Must be
        /// called before <see cref="OnLoaded"/> runs (i.e. before this window is shown) — this
        /// sets <see cref="ResizeMode"/>/<see cref="WindowState"/>-affecting properties that WPF
        /// expects to be stable at load time, and the plan's toggle-timing rule means this is
        /// decided once, at reparent time, not re-evaluated later for an already-open host frame.
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
        public void AttachTarget(IntPtr targetHwnd)
        {
            TargetHwnd = targetHwnd;

            // Reset resize tracking for the newly-attached target: its initial size is established
            // via ResizeHostToFitContent below (host frame sized to fit it), not via the
            // socket-resize path in PositionSocket(), so there is no "last applied" size yet.
            _lastAppliedTargetWidth = -1;
            _lastAppliedTargetHeight = -1;

            if (targetHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(targetHwnd, out var targetRect))
            {
                int targetWidth = targetRect.Right - targetRect.Left;
                int targetHeight = targetRect.Bottom - targetRect.Top;
                ResizeHostToFitContent(targetWidth, targetHeight);

                // The host frame is now sized to exactly fit the target at its original dimensions,
                // so the socket's resulting size (computed in PositionSocket below) should already
                // match the target's current size — record it as "already applied" so the first
                // PositionSocket() call below doesn't immediately (and redundantly) resize the
                // target again.
                _lastAppliedTargetWidth = targetWidth;
                _lastAppliedTargetHeight = targetHeight;
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
        /// BUG FIX (destroy-on-failed-restore report): the handler must set
        /// <see cref="RestoreOutcomeEventArgs.UnparentSucceeded"/> to false if the restore's
        /// SetParent-back-to-original-parent step failed, so <see cref="OnClosing"/> knows not to
        /// destroy the socket window while the target might still be a WS_CHILD of it — see
        /// <see cref="OnClosing"/> for why.
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
        private bool _unparentSucceeded = true;

        /// <summary>
        /// Lets a caller that already performed the restore itself (outside the normal
        /// Close/Restore-button or Alt+F4/chrome-close paths — currently only
        /// <c>ReparentController.RestoreAll()</c>, which calls <c>RestoreEntry</c> directly before
        /// calling <see cref="Window.Close"/>) record the real outcome up front, so
        /// <see cref="OnClosing"/> doesn't re-raise <see cref="RestoreRequested"/> and get a
        /// second, misleading "already handled" result back (§ destroy-on-failed-restore fix).
        /// Without this, a second <c>RestoreEntry</c> call for an already-removed tracking-list
        /// entry always returns true (its no-op branch), which would incorrectly report success
        /// and let <see cref="OnClosing"/> destroy the socket even if the *first*, real restore
        /// attempt had actually failed to unparent the target.
        /// </summary>
        public void NotifyRestoreAlreadyHandled(bool unparentSucceeded)
        {
            _restoreRequestedOnClose = true;
            _unparentSucceeded = unparentSucceeded;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Safety net: if the user closes the host frame via the window chrome/Alt+F4 instead
            // of the Close/Restore button, still ask the caller to restore the target rather than
            // leaving it orphaned as a WS_CHILD of a window that's about to be destroyed.
            if (!_restoreRequestedOnClose)
            {
                _restoreRequestedOnClose = true;

                // Defaults to true: if RestoreRequested has no subscriber, or the target was
                // never actually reparented in the first place (nothing to unparent), there is no
                // known unparent failure, so the socket is still safe to destroy below.
                var args = new RestoreOutcomeEventArgs();
                RestoreRequested?.Invoke(this, args);
                _unparentSucceeded = args.UnparentSucceeded;
            }

            _hwndSource?.RemoveHook(WndProc);

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
                if (_unparentSucceeded)
                {
                    NativeMethods.DestroyWindow(_socketHwnd);
                    _socketHwnd = IntPtr.Zero;
                }
                else
                {
                    DebugLog($"OnClosing: restore reported unparent failure; leaking socket={_socketHwnd} instead of destroying it, to avoid destroying a still-embedded target.");
                }
            }
        }

        private void OnCloseRestoreClick(object sender, RoutedEventArgs e)
        {
            Close();
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
            if (TargetHwnd != IntPtr.Zero && NativeMethods.IsWindow(TargetHwnd))
            {
                bool sizeChanged = width != _lastAppliedTargetWidth || height != _lastAppliedTargetHeight;

                if (!sizeChanged)
                {
                    // Reposition-only (or no-op): apply immediately, same as before.
                    bool repositionOk = NativeMethods.SetWindowPos(
                        TargetHwnd,
                        IntPtr.Zero,
                        0,
                        0,
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
            if (TargetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(TargetHwnd))
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

            IntPtr target = TargetHwnd;
            if (target == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

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

            [DllImport("user32.dll")]
            public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetFocus(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr GetFocus();

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
    /// target was actually successfully unparented, so <see cref="ReparentHostWindow.OnClosing"/>
    /// can decide whether it's safe to destroy the socket window (destroying a window destroys its
    /// WS_CHILD children, including a target that failed to unparent).
    /// </summary>
    public sealed class RestoreOutcomeEventArgs : EventArgs
    {
        /// <summary>
        /// True (the default) unless the handler explicitly reports that the restore's
        /// SetParent-back-to-original-parent step failed for a target that was actually
        /// reparented into this host's socket. Defaulting to true covers the "nothing was ever
        /// reparented" / "no subscriber" cases, where there's no known failure and the socket
        /// remains safe to destroy.
        /// </summary>
        public bool UnparentSucceeded { get; set; } = true;
    }
}
