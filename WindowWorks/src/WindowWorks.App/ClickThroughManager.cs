using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WindowWorks.App
{
    /// <summary>
    /// Manages click-through state for windows. Phase 1 skeleton: Gesture Mode.
    /// Responsibilities:
    /// - Enable/disable click-through (WS_EX_TRANSPARENT) for target windows
    /// - Apply optional transparency via SetLayeredWindowAttributes
    /// - Track modified windows and restore on reset
    /// - Raise notifications / HUD updates when state changes
    ///
    /// NOTE: This is a skeleton with safe P/Invoke wrappers and method stubs. Implement logic and error handling as needed.
    /// </summary>
    public class ClickThroughManager : IDisposable
    {
        private readonly Models.AppSettings _settings;
        private readonly AuditLog _auditLog;
        // track modified windows (hwnd -> original style and/or transparency)
        private readonly Dictionary<IntPtr, WindowOriginalState> _modifiedWindows = new();

        public ClickThroughManager(Models.AppSettings settings, AuditLog auditLog)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        }

        #region P/Invoke helpers
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        private struct WindowOriginalState
        {
            public int ExStyle;
            public byte? Alpha;
            public bool LayeredWasSet;
        }
        #endregion

        /// <summary>
        /// Enable click-through on the specified window (apply WS_EX_TRANSPARENT).
        /// Stores original state so it can be restored later.
        /// </summary>
        public bool EnableClickThrough(IntPtr hwnd, bool applyTransparency = false, int transparencyPercent = 50)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (!_modifiedWindows.ContainsKey(hwnd))
                {
                    _modifiedWindows[hwnd] = new WindowOriginalState { ExStyle = ex, Alpha = null, LayeredWasSet = (ex & WS_EX_LAYERED) != 0 };
                }

                int newEx = ex | WS_EX_TRANSPARENT | WS_EX_LAYERED;
                SetWindowLong(hwnd, GWL_EXSTYLE, newEx);

                if (applyTransparency)
                {
                    byte alpha = (byte)(255 * Math.Clamp(transparencyPercent, 0, 100) / 100);
                    // LWA_ALPHA = 0x02
                    SetLayeredWindowAttributes(hwnd, 0, alpha, 0x02);
                }

                // TODO: Show HUD or tray notification
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restore windows modified by EnableClickThrough and clear tracking list.
        /// </summary>
        public void ResetAllClickThrough()
        {
            foreach (var kv in _modifiedWindows)
            {
                try
                {
                    var hwnd = kv.Key;
                    var orig = kv.Value;
                    SetWindowLong(hwnd, GWL_EXSTYLE, orig.ExStyle);
                    if (orig.LayeredWasSet && orig.Alpha.HasValue)
                    {
                        // restore alpha if needed - left as future work
                    }
                }
                catch { }
            }
            _modifiedWindows.Clear();
            // TODO: Show HUD/tray notification
        }

        public void Dispose()
        {
            // ensure we restore modified windows
            try { ResetAllClickThrough(); } catch { }
        }
    }
}
