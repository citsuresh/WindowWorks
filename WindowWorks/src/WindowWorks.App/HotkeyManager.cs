using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;

namespace WindowWorks.App
{
    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 1,
        Ctrl = 2,
        Shift = 4,
        Win = 8
    }

    public class HotkeyEventArgs : EventArgs
    {
        public HotkeyModifiers Modifiers { get; }
        public Keys Key { get; }
        public HotkeyEventArgs(HotkeyModifiers mods, Keys key) { Modifiers = mods; Key = key; }
    }

    public class HotkeyRegistrationFailedEventArgs : EventArgs
    {
        public HotkeyModifiers Modifiers { get; }
        public Keys Key { get; }
        public int ErrorCode { get; }
        public HotkeyRegistrationFailedEventArgs(HotkeyModifiers mods, Keys key, int errorCode) { Modifiers = mods; Key = key; ErrorCode = errorCode; }
    }

    public class OpacityNudgeEventArgs : EventArgs
    {
        public int Delta { get; }
        public OpacityNudgeEventArgs(int delta) { Delta = delta; }
    }

    /// <summary>
    /// Registers system hotkeys and installs a low-level mouse hook (WH_MOUSE_LL) to detect gestures.
    /// Minimal implementation: exposes events for the app to react to.
    /// </summary>
    public class HotkeyManager : IDisposable
    {
        // Event raised when RegisterHotKey fails (e.g., already registered by another app)
        public event EventHandler<HotkeyRegistrationFailedEventArgs>? HotkeyRegistrationFailed;
        // Events for high-level gestures
        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
        public event EventHandler<OpacityNudgeEventArgs>? OpacityNudgeRequested;
        public event EventHandler? ToggleTopmostRequested;
        // Click-through gesture events (Phase 1)
        public event EventHandler? ClickThroughGestureRequested;
        public event EventHandler? ClickThroughResetRequested;

        private IntPtr _mouseHook = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc? _mouseProc;

        private MessageWindow? _msgWindow;
        private System.Threading.SynchronizationContext? _syncContext;
        private readonly Models.AppSettings _settings;
        private readonly System.Collections.Generic.List<int> _registeredHotkeyIds = new();

        // Diagnostic helper: expose whether the low-level mouse hook was installed
        public bool IsMouseHookInstalled => _mouseHook != IntPtr.Zero;

        // Diagnostic logging to temp file for quick checks (append-only)
        private void DebugLog(string message)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windowworks_hotkey_log.txt");
                System.IO.File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        public HotkeyManager(Models.AppSettings? settings = null)
        {
            _settings = settings ?? new Models.AppSettings();
        }

        public void Start()
        {
            // Create a hidden message window to receive WM_HOTKEY messages
            _msgWindow = new MessageWindow(HandleHotkeyMessage);
            // Capture the synchronization context of the thread that started the manager (UI thread)
            _syncContext = System.Threading.SynchronizationContext.Current;
            // Register hotkeys from settings (fall back to sensible defaults)
            ApplyHotkeySettings(_settings);

            // Install low-level mouse hook to detect Ctrl+Wheel and Ctrl+Click combos
            _mouseProc = LowLevelMouseProc;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
            DebugLog($"Start: mouseHook={( _mouseHook != IntPtr.Zero ? _mouseHook.ToString() : "NULL")} msgWindowHandle={( _msgWindow != null ? _msgWindow.Handle.ToString() : "NULL")} syncContext={( _syncContext != null ? "YES" : "NO")} settings.EnableCtrlWheelOpacity={_settings.EnableCtrlWheelOpacity} settings.EnableCtrlAltTopmost={_settings.EnableCtrlAltTopmost} settings.EnableCtrlShiftTopmost={_settings.EnableCtrlShiftTopmost}");
        }

        public void Dispose()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_msgWindow != null)
            {
                _msgWindow.Dispose();
                _msgWindow = null;
            }
        }

        private void HandleHotkeyMessage(int id, HotkeyModifiers mods, Keys key)
        {
            // Handle special registered hotkeys by id when known by convention
            // id==2 is reserved for Click-Through Reset (Phase 1)
            // id-based special handlers removed for Click-Through Reset to avoid conflicts.

            HotkeyPressed?.Invoke(this, new HotkeyEventArgs(mods, key));
        }

        private IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Note: This callback executes on a native thread. Keep it minimal and marshal to the main thread if heavy work is needed.
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                // WM_MOUSEWHEEL = 0x020A, WM_LBUTTONDOWN = 0x0201
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_LBUTTONDOWN = 0x0201;

                if (msg == WM_MOUSEWHEEL)
                {
                    int delta = NativeMethods.GET_WHEEL_DELTA_WPARAM(Marshal.ReadInt32(lParam, 8));
                    bool ctrl = (NativeMethods.GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
                    bool shift = (NativeMethods.GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
                    DebugLog($"Detected WM_MOUSEWHEEL delta={delta} ctrl={ctrl} shift={shift}");
                    if (ctrl && _settings.EnableCtrlWheelOpacity)
                    {
                        // Only trigger opacity nudges when the cursor is on the window titlebar area.
                        try
                        {
                            // Get cursor position
                            if (NativeMethods.GetCursorPos(out var p))
                            {
                                // Find top-level window under cursor
                                IntPtr target = NativeMethods.WindowFromPoint(p);
                                    if (target != IntPtr.Zero)
                                    {
                                        // Walk to the top-level ancestor so we measure against the real window caption
                                        IntPtr top = NativeMethods.GetAncestor(target, NativeMethods.GA_ROOT);
                                        if (top == IntPtr.Zero) top = target;
                                        // Get window rect and system caption/frame sizes
                                        if (NativeMethods.GetWindowRect(top, out var wr))
                                        {
                                            int caption = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYCAPTION);
                                            int frame = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYFRAME);
                                            int titleHeight = caption + frame;
                                            int localY = p.Y - wr.Top;
                                            if (localY <= titleHeight)
                                            {
                                                int step = shift ? _settings.OpacityStepFine : _settings.OpacityStepDefault; // configurable steps
                                                int d = (delta > 0) ? step : -step;
                                                // Check if user configured a custom gesture string and match it
                                                // Trigger opacity nudge when Ctrl+Wheel over title bar and feature enabled.
                                                // Settings may contain a configured gesture string but by default Ctrl+Wheel should work.
                                                if (_syncContext != null)
                                                {
                                                    _syncContext.Post(_ => OpacityNudgeRequested?.Invoke(this, new OpacityNudgeEventArgs(d)), null);
                                                }
                                                else
                                                {
                                                    OpacityNudgeRequested?.Invoke(this, new OpacityNudgeEventArgs(d));
                                                }
                                            SKIP_OPACITY: ;
                                            }
                                        }
                                    }
                            }
                        }
                        catch { /* swallow errors in hook thread */ }
                    }
                }
                else if (msg == WM_LBUTTONDOWN)
                {
                    bool ctrl = (NativeMethods.GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
                    bool alt = (NativeMethods.GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0;
                    bool shift = (NativeMethods.GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
                    // Support topmost / click-through gestures.
                    // Detect Ctrl+Alt+Click for Click-Through Gesture Mode.
                    // Ctrl+Alt+Click is reserved exclusively for Click-Through; when Click-Through is disabled it must do nothing.
                    if (ctrl && alt && _settings.EnableClickThroughGestureMode)
                    {
                        DebugLog($"Detected ClickThrough gesture WM_LBUTTONDOWN ctrl={ctrl} alt={alt} shift={shift}");
                        if (_syncContext != null)
                        {
                            _syncContext.Post(_ => ClickThroughGestureRequested?.Invoke(this, EventArgs.Empty), null);
                        }
                        else
                        {
                            ClickThroughGestureRequested?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else if (ctrl && shift && _settings.EnableCtrlShiftTopmost)
                    {
                        DebugLog($"Detected WM_LBUTTONDOWN ctrl={ctrl} alt={alt} shift={shift}");
                        // Trigger toggle topmost when enabled and matching configured gesture (Ctrl+Shift only).
                        if (_syncContext != null)
                        {
                            _syncContext.Post(_ => ToggleTopmostRequested?.Invoke(this, EventArgs.Empty), null);
                        }
                        else
                        {
                            ToggleTopmostRequested?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else
                    {
                        // No matching gesture. Note: Ctrl+Alt+Click is intentionally ignored here when Click-Through is disabled.
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        #region Hotkey registration helpers

        public bool RegisterHotkey(int id, HotkeyModifiers mods, Keys key)
        {
            if (_msgWindow == null) return false;
            bool ok = NativeMethods.RegisterHotKey(_msgWindow.Handle, id, (uint)mods, (uint)key);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                // Notify listeners on UI context if available
                if (_syncContext != null)
                {
                    _syncContext.Post(_ => HotkeyRegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(mods, key, err)), null);
                }
                else
                {
                    HotkeyRegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(mods, key, err));
                }
            }
            else
            {
                // track successful registration so we can unregister later
                if (!_registeredHotkeyIds.Contains(id)) _registeredHotkeyIds.Add(id);
            }
            return ok;
        }

        public void UnregisterAllHotkeys()
        {
            try
            {
                foreach (var id in _registeredHotkeyIds.ToArray())
                {
                    try { UnregisterHotkey(id); } catch { }
                }
                _registeredHotkeyIds.Clear();
            }
            catch { }
        }

        /// <summary>
        /// Parse hotkey strings from settings and (re)register system hotkeys.
        /// Expected format: Modifier+Modifier+Key (e.g. "Win+`" or "Win+Shift+R").
        /// </summary>
        public void ApplyHotkeySettings(Models.AppSettings settings)
        {
            if (settings == null) return;
            // Unregister previous first
            UnregisterAllHotkeys();

            // Register only the keyboard hotkeys exposed in settings (command palette and reset all)
            // Command palette
            if (!string.IsNullOrWhiteSpace(settings.HotkeyCommandPalette))
            {
                if (ParseHotkeyString(settings.HotkeyCommandPalette, out var m1, out var k1))
                {
                    RegisterHotkey(0, m1, k1);
                }
                else
                {
                    // fallback to Win+`
                    RegisterHotkey(0, HotkeyModifiers.Win, Keys.Oem3);
                }
            }
            else
            {
                RegisterHotkey(0, HotkeyModifiers.Win, Keys.Oem3);
            }

            // Emergency reset
            if (!string.IsNullOrWhiteSpace(settings.HotkeyEmergencyReset))
            {
                if (ParseHotkeyString(settings.HotkeyEmergencyReset, out var m2, out var k2))
                {
                    RegisterHotkey(1, m2, k2);
                }
                else
                {
                    RegisterHotkey(1, HotkeyModifiers.Win | HotkeyModifiers.Shift, Keys.R);
                }
            }
            else
            {
                RegisterHotkey(1, HotkeyModifiers.Win | HotkeyModifiers.Shift, Keys.R);
            }

            // NOTE: Click-Through Reset hotkey registration removed to avoid conflicts. Use tray menu or Gesture reset instead.
        }

        public static bool ParseHotkeyString(string s, out HotkeyModifiers mods, out Keys key)
        {
            mods = HotkeyModifiers.None; key = Keys.None;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try
            {
                var parts = s.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var t = p.Trim();
                    if (string.Equals(t, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Control", StringComparison.OrdinalIgnoreCase)) mods |= HotkeyModifiers.Ctrl;
                    else if (string.Equals(t, "Alt", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Menu", StringComparison.OrdinalIgnoreCase)) mods |= HotkeyModifiers.Alt;
                    else if (string.Equals(t, "Shift", StringComparison.OrdinalIgnoreCase)) mods |= HotkeyModifiers.Shift;
                    else if (string.Equals(t, "Win", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Windows", StringComparison.OrdinalIgnoreCase)) mods |= HotkeyModifiers.Win;
                    else
                    {
                        // try parse as Keys
                        if (Enum.TryParse<Keys>(t, true, out var parsed)) key = parsed;
                        else
                        {
                            // handle common symbols
                            if (t == "`" || t == "~") key = Keys.Oem3;
                            else if (t.Length == 1)
                            {
                                key = (Keys)Enum.Parse(typeof(Keys), t.ToUpper());
                            }
                        }
                    }
                }
                return key != Keys.None;
            }
            catch { return false; }
        }

        public bool UnregisterHotkey(int id)
        {
            if (_msgWindow == null) return false;
            return NativeMethods.UnregisterHotKey(_msgWindow.Handle, id);
        }

        private class MessageWindow : NativeWindow, IDisposable
        {
            private readonly Action<int, HotkeyModifiers, Keys> _onHotkey;
            public MessageWindow(Action<int, HotkeyModifiers, Keys> onHotkey)
            {
                CreateHandle(new CreateParams());
                _onHotkey = onHotkey;
            }

            private const int WM_HOTKEY = 0x0312;
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    int l = m.LParam.ToInt32();
                    HotkeyModifiers mods = (HotkeyModifiers)(l & 0xFFFF);
                    Keys key = (Keys)((l >> 16) & 0xFFFF);
                    _onHotkey(id, mods, key);
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }

        #endregion
    }

    internal static partial class NativeMethods
    {
        public const int WH_MOUSE_LL = 14;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        // GetCursorPos is declared in src/WindowWorks.App/NativeMethods.cs; reuse it via DllImport duplication avoidance.
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CYCAPTION = 4;
        public const int SM_CYFRAME = 32;
        public const uint GA_ROOT = 2;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);

        public static int GET_WHEEL_DELTA_WPARAM(int wparam) => (short)HIWORD(wparam);
        private static int HIWORD(int n) => (n >> 16) & 0xffff;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
    }
}
