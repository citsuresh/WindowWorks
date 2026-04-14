using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using WindowWorks.App.Models;
using System.Diagnostics;

namespace WindowWorks.App
{
    /// <summary>
    /// Responsible for interacting with external windows. Uses P/Invoke only from this process and
    /// provides safe wrappers for common operations such as opacity and topmost.
    /// NOTE: No code injection or remote thread manipulation is performed.
    /// </summary>

    public class WindowManager : IDisposable
    {
        // (no debug events)
        private readonly AuditLog _auditLog;
        // Active highlight overlays keyed by target window handle
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, WindowWorks.App.UI.HighlightOverlay> _overlays = new();
        // Cache of topmost state for windows we've changed so toggles can report accurate state.
        // This is best-effort and reflects changes made by this process.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, bool> _topmostCache = new();

        private readonly Models.AppSettings _settings;
        // (no runtime blacklist)

        public WindowManager(AuditLog auditLog, Models.AppSettings? settings = null)
        {
            _auditLog = auditLog;
            _settings = settings ?? new Models.AppSettings();
        }

        /// <summary>
        /// Try to get a friendly label for the window: window title if available, otherwise process name.
        /// Returns empty string if nothing found.
        /// </summary>
        public string GetWindowLabel(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            try
            {
                int len = Native.GetWindowTextLengthW(hwnd);
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    int got = Native.GetWindowTextW(hwnd, sb, sb.Capacity);
                    if (got > 0)
                    {
                        var txt = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(txt)) return txt.Trim();
                    }
                }

                // Fallback to process name
                if (Native.GetWindowThreadProcessId(hwnd, out uint pid) != 0)
                {
                    try
                    {
                        var p = Process.GetProcessById((int)pid);
                        if (p != null)
                        {
                            return p.ProcessName;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// Read the current layered alpha on a window and return as 0-100 percent.
        /// Falls back to 100 if not layered or on error.
        /// </summary>
        public int GetOpacityPercent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 100;
            try
            {
                if (Native.GetLayeredWindowAttributes(hwnd, out uint key, out byte alpha, out uint flags))
                {
                    if ((flags & Native.LWA_ALPHA) != 0)
                    {
                        return (int)Math.Round(alpha * 100.0 / 255.0);
                    }
                }
            }
            catch { }
            return 100;
        }

        public IntPtr GetForegroundWindowHandle()
        {
            return Native.GetForegroundWindow();
        }

        /// <summary>
        /// Returns the window handle under the current cursor position, or IntPtr.Zero on failure.
        /// </summary>
        public IntPtr GetWindowUnderCursor()
        {
            try
            {
                if (Native.GetCursorPos(out var p))
                {
                    return Native.WindowFromPoint(p);
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        public void AdjustOpacity(IntPtr hwnd, int deltaPercent, bool saveSnapshot = true)
        {
            if (hwnd == IntPtr.Zero) return;
            var snap = WindowStateSnapshot.FromWindow(hwnd);
            int newOpacity = Math.Clamp(snap.Opacity + deltaPercent, 0, 100);
            ApplyOpacity(hwnd, newOpacity);
            if (saveSnapshot) _auditLog.RecordSnapshot(snap);
        }

        public bool ToggleTopmost(IntPtr hwnd, bool saveSnapshot = true)
        {
            if (hwnd == IntPtr.Zero) return false;
            var snap = WindowStateSnapshot.FromWindow(hwnd);
            // Determine current topmost: prefer cached value for windows we've modified, otherwise fall back to snapshot.
            bool current = _topmostCache.TryGetValue(hwnd, out var cached) ? cached : snap.IsTopmost;
            bool newTop = !current;
            SetTopmost(hwnd, newTop);
            _topmostCache[hwnd] = newTop;
            if (saveSnapshot) _auditLog.RecordSnapshot(snap);
            return newTop;
        }



        public void ApplyOpacity(IntPtr hwnd, int opacityPercent)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var desktop = Native.GetDesktopWindow();
                if (hwnd == desktop) return; // never apply to desktop
            }
            catch { }

            // Avoid changing windows that belong to this process (e.g., HUD/overlay)
            try
            {
                if (Native.GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    return; // skip applying opacity to our own windows
                }
            }
            catch { }

            // If we have a transient highlight overlay for this hwnd, remove it while changing opacity to
            // avoid composition interference that can produce a black fill on some GPU-backed windows.
            try { RemoveHighlight(hwnd); } catch { }

            // Ensure layered style and set alpha
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_LAYERED) == 0)
            {
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
            }
            byte alpha = (byte)(Math.Clamp(opacityPercent, 0, 100) * 255 / 100);
            try { Native.SetLayeredWindowAttributes(hwnd, 0, alpha, Native.LWA_ALPHA); } catch { }
        }

        public void SetTopmost(IntPtr hwnd, bool topmost)
        {
            if (hwnd == IntPtr.Zero) return;
            try { if (hwnd == Native.GetDesktopWindow()) return; } catch { }
            Native.SetWindowPos(hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0,0,0,0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);
        }

        // Click-through helpers removed.

        public void Dispose()
        {
            // no unmanaged handles held here; placeholder
        }

        // Composition-based opacity and runtime blacklisting features removed — use simple layered alpha.

        /// <summary>
        /// Show a brief highlight around the specified window using the UI overlay.
        /// This is a thin helper that attempts to create the overlay window in the UI process.
        /// </summary>
        public void ShowHighlight(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                // If a WPF Application exists (unlikely in WinForms process), use its dispatcher.
                System.Windows.Application? app = System.Windows.Application.Current;
                if (app != null)
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        var overlay = new WindowWorks.App.UI.HighlightOverlay();
                        // Configure visuals from settings when available
                        try
                        {
                            // use injected settings when present, otherwise fall back to persisted
                            var settings = _settings ?? new Persistence().LoadSettings();
                            if (settings != null)
                            {
                                // Always use configured highlight color; system-accent option was removed.
                                overlay.SetBorderColor(settings.HighlightBorderColor);
                                overlay.SetBorderThickness(settings.HighlightBorderThickness);
                                overlay.SetCornerRadius(settings.HighlightCornerRadius);
                            }
                        }
                        catch { }
                        overlay.ShowAround(hwnd, _settings.HighlightDurationMs);
                        // Track overlay to allow lifetime extension
                        _overlays[hwnd] = overlay;
                    });
                    return;
                }

                // Otherwise create the overlay on a dedicated STA thread with its own dispatcher so it can show independently.
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var overlay = new WindowWorks.App.UI.HighlightOverlay();
                        // Configure visuals from settings when available
                        try
                        {
                            var settings = _settings ?? new Persistence().LoadSettings();
                            if (settings != null)
                            {
                                overlay.SetBorderColor(settings.HighlightBorderColor);
                                overlay.SetBorderThickness(settings.HighlightBorderThickness);
                                overlay.SetCornerRadius(settings.HighlightCornerRadius);
                            }
                        }
                        catch { }
                        // ShowAround will call Show() and schedule a shutdown when done
                        overlay.ShowAround(hwnd, _settings.HighlightDurationMs);
                        _overlays[hwnd] = overlay;
                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
            }
            catch { }
        }

        /// <summary>
        /// Extend the highlight overlay lifetime for the provided hwnd to durationMs.
        /// If no overlay exists for the hwnd, this is a no-op.
        /// </summary>
        public void ExtendHighlight(IntPtr hwnd, int durationMs)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (_overlays.TryGetValue(hwnd, out var overlay))
                {
                    try
                    {
                        overlay.Extend(durationMs);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void RemoveHighlight(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (_overlays.TryRemove(hwnd, out var overlay))
                {
                    try { overlay.Close(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Native interop signatures and constants.
        /// Example usage comments included as requested.
        /// </summary>
        internal static class Native
        {
            // Definitions for composition API (SetWindowCompositionAttribute)
            [StructLayout(LayoutKind.Sequential)]
            public struct ACCENT_POLICY
            {
                public int AccentState;
                public int AccentFlags;
                public int GradientColor;
                public int AnimationId;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct WINDOWCOMPOSITIONATTRIBDATA
            {
                public int Attribute;
                public IntPtr Data;
                public int SizeOfData;
            }

            public enum AccentState
            {
                ACCENT_DISABLED = 0,
                ACCENT_ENABLE_GRADIENT = 1,
                ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
                ACCENT_ENABLE_BLURBEHIND = 3,
                ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
                ACCENT_INVALID_STATE = 5
            }

            public enum WindowCompositionAttribute
            {
                WCA_ACCENT_POLICY = 19
            }

            [DllImport("user32.dll")]
            public static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_LAYERED = 0x00080000;
            public const int LWA_ALPHA = 0x02;

            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_FRAMECHANGED = 0x0020;
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();
            // Usage: var hwnd = Native.GetForegroundWindow();

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
            public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
            public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            // For convenience on both x86/x64, provide int-based wrappers used above
            public static int GetWindowLong(IntPtr hWnd, int nIndex)
            {
                return (int)GetWindowLongPtr(hWnd, nIndex).ToInt64();
            }
            public static int SetWindowLong(IntPtr hWnd, int nIndex, int newValue)
            {
                return (int)SetWindowLongPtr(hWnd, nIndex, new IntPtr(newValue)).ToInt64();
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
            // Usage: Native.SetLayeredWindowAttributes(hwnd, 0, 200, Native.LWA_ALPHA);

            [DllImport("user32.dll", SetLastError = true)]
            // Retrieves the layered window attributes (color key, alpha, flags) for a layered window.
            // Usage: uint key; byte alpha; uint flags; if (GetLayeredWindowAttributes(hwnd, out key, out alpha, out flags)) { /* use alpha */ }
            public static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
            // Usage: Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0,0,0,0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);

            // Low-level mouse hook, register/unregister (used in HotkeyManager)
            public const int WH_MOUSE_LL = 14;
            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
            [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
            [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT Point);
            [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
            [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int GetWindowTextLengthW(IntPtr hWnd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

            public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

            [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
            [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll")]
            public static extern IntPtr GetCapture();

            // Utilities
            public static int GET_WINDOW_WIDTH(RECT r) => r.Right - r.Left;
            public static int GET_WINDOW_HEIGHT(RECT r) => r.Bottom - r.Top;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

            // ChildWindowFromPointEx flags
            public const uint CWP_SKIPINVISIBLE = 0x0001;
            public const uint CWP_SKIPDISABLED = 0x0002;
            public const uint CWP_SKIPTRANSPARENT = 0x0004;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetDesktopWindow();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT pt, uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            private const uint GA_ROOT = 2;

            // Return the topmost non-transparent window under the current cursor position.
            public static IntPtr GetTopmostWindowUnderCursor()
            {
                if (!GetCursorPos(out var p)) return IntPtr.Zero;
                try
                {
                    // First try ChildWindowFromPointEx which skips transparent/disabled/invisible children
                    IntPtr desktop = GetDesktopWindow();
                    IntPtr child = ChildWindowFromPointEx(desktop, p, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
                    if (child != IntPtr.Zero)
                    {
                        IntPtr top = GetAncestor(child, GA_ROOT);
                        if (top != IntPtr.Zero && top != desktop) return top;
                        return child;
                    }
                    // If that didn't yield a usable window (or was the desktop), enumerate top-level windows
                    IntPtr found = FindTopLevelWindowAtPoint(p);
                    if (found != IntPtr.Zero) return found;
                }
                catch { }

                // Fallback: WindowFromPoint then return its root ancestor
                IntPtr hwnd = WindowFromPoint(p);
                if (hwnd == IntPtr.Zero) return IntPtr.Zero;
                try
                {
                    IntPtr top = GetAncestor(hwnd, GA_ROOT);
                    if (top != IntPtr.Zero) return top;
                }
                catch { }
                return hwnd;
            }

            // EnumWindows and helpers to find a top-level window containing the point, skipping our own process and invisible/minimized windows.
            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsWindowVisible(IntPtr hWnd);
            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsIconic(IntPtr hWnd);

            // (EnumChildWindows removed to restore original click-through behavior)

            private static IntPtr FindTopLevelWindowAtPoint(POINT p)
            {
                IntPtr result = IntPtr.Zero;
                // Enumerate top-level windows in z-order from topmost to bottommost
                EnumWindows((hwnd, lParam) => {
                    try
                    {
                        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true; // continue
                        if (GetWindowRect(hwnd, out var r))
                        {
                            if (p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom)
                            {
                                // skip desktop and shell windows
                                IntPtr desktop = GetDesktopWindow();
                                if (hwnd == desktop) return true;
                                // skip windows that belong to this process
                                if (GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;
                                // ensure not transparent via layered alpha
                                if (GetLayeredWindowAttributes(hwnd, out uint key, out byte alpha, out uint flags))
                                {
                                    if ((flags & LWA_ALPHA) != 0 && alpha == 0) return true; // fully transparent, skip
                                }
                                // found suitable window
                                IntPtr top = GetAncestor(hwnd, GA_ROOT);
                                result = top != IntPtr.Zero ? top : hwnd;
                                return false; // stop enumeration
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                return result;
            }
        }
    }
}
