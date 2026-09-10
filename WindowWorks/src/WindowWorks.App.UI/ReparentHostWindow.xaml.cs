using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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

        /// <summary>
        /// The currently-embedded target window, used only for WM_MOUSEACTIVATE/WM_ACTIVATE
        /// forwarding (§8 step 7). The caller is responsible for save/reparent/restore mechanics
        /// via its own ReparentEngine instance; this window does not perform those calls itself.
        /// Set to IntPtr.Zero when nothing is embedded (disables activation forwarding).
        /// </summary>
        public IntPtr TargetHwnd { get; set; } = IntPtr.Zero;

        /// <summary>
        /// Raised when the user clicks the Close/Restore button, or when the window is closing
        /// via any other path (Alt+F4, chrome close) — the caller must restore the target's
        /// original state in response to this event (via its own ReparentEngine instance) before
        /// this window finishes closing, so the target is never left orphaned.
        /// </summary>
        public event EventHandler? RestoreRequested;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            CreateSocket(hwnd);
            PositionSocket();

            SizeChanged += (_, _) => PositionSocket();

            SocketReady?.Invoke(this, EventArgs.Empty);
        }

        private bool _restoreRequestedOnClose;

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Safety net: if the user closes the host frame via the window chrome/Alt+F4 instead
            // of the Close/Restore button, still ask the caller to restore the target rather than
            // leaving it orphaned as a WS_CHILD of a window that's about to be destroyed.
            if (!_restoreRequestedOnClose)
            {
                _restoreRequestedOnClose = true;
                RestoreRequested?.Invoke(this, EventArgs.Empty);
            }

            _hwndSource?.RemoveHook(WndProc);

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
            NativeMethods.RegisterClass(ref wc);
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

            NativeMethods.MoveWindow(_socketHwnd, hostTopLeft.X, hostTopLeft.Y, width, height, true);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int WM_ACTIVATE = 0x0006;
            const int MA_NOACTIVATE = 3;
            const int WA_ACTIVE = 1;

            IntPtr target = TargetHwnd;
            if (target == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            if (msg == WM_MOUSEACTIVATE)
            {
                if (NativeMethods.GetForegroundWindow() != target)
                {
                    NativeMethods.SetForegroundWindow(target);
                }

                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            if (msg == WM_ACTIVATE && (wParam.ToInt32() & 0xFFFF) == WA_ACTIVE)
            {
                NativeMethods.SetForegroundWindow(target);
            }

            return IntPtr.Zero;
        }

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

            [DllImport("user32.dll", SetLastError = true)]
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
        }
    }
}
