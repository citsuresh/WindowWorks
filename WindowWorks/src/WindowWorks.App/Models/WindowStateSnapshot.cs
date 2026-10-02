using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App.Models
{
    public enum WindowChangeKind { Opacity, Topmost }

    /// <summary>
    /// Snapshot of a window state before modification. Minimal fields required to restore opacity and topmost.
    /// </summary>
    public class WindowStateSnapshot
    {
        public IntPtr Hwnd { get; set; }
        public int ExStyle { get; set; }
        public int Opacity { get; set; } // 0-100
        public bool IsTopmost { get; set; }
        public WindowChangeKind ChangeKind { get; private set; }
        internal WindowWorks.App.WindowManager.WindowIdentity? Identity { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static WindowStateSnapshot FromWindow(IntPtr hwnd, WindowChangeKind kind = WindowChangeKind.Opacity)
        {
            var s = new WindowStateSnapshot { ChangeKind = kind };
            s.Hwnd = hwnd;
            s.Identity = WindowWorks.App.WindowManager.CaptureIdentity(hwnd);
            if (kind == WindowChangeKind.Topmost)
            {
                System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
                int topmostStyle = WindowWorks.App.WindowManager.Native.GetWindowLong(hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE);
                if (topmostStyle == 0 && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0)
                    throw new InvalidOperationException("Cannot read the original topmost state; topmost was not changed.");
                s.IsTopmost = (topmostStyle & WindowWorks.App.WindowManager.Native.WS_EX_TOPMOST) != 0;
                return s;
            }
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
            s.ExStyle = WindowWorks.App.WindowManager.Native.GetWindowLong(hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE);
            if (s.ExStyle == 0 && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0)
                throw new InvalidOperationException("Cannot read the original window style; opacity was not changed.");
            // Determine opacity by querying layered window attributes when available.
            if (WindowWorks.App.WindowManager.Native.GetLayeredWindowAttributes(hwnd, out uint key, out byte alpha, out uint flags))
            {
                if ((flags & WindowWorks.App.WindowManager.Native.LWA_ALPHA) != 0)
                {
                    s.Opacity = (int)Math.Round(alpha * 100.0 / 255.0);
                }
                else
                {
                    s.Opacity = 100;
                }
            }
            else if ((s.ExStyle & WindowWorks.App.WindowManager.Native.WS_EX_LAYERED) != 0)
            {
                throw new InvalidOperationException("Cannot read the original layered opacity; opacity was not changed.");
            }
            else s.Opacity = 100;
            s.IsTopmost = WindowWorks.App.WindowManager.IsTopmost(hwnd);
            return s;
        }

        public void Restore(WindowWorks.App.WindowManager wm)
            => TryRestore(wm, out _);

        public bool TryRestore(WindowWorks.App.WindowManager wm, out string? failure)
            => TryRestore(wm, out failure, out _);

        internal bool TryRestore(WindowWorks.App.WindowManager wm, out string? failure, out bool stale)
        {
            failure = null;
            stale = false;
            if (Identity is null || WindowWorks.App.WindowManager.CaptureIdentity(Hwnd) != Identity)
            {
                failure = "Undo skipped: the original window could not be verified (closed, replaced, elevated, or tracking unavailable).";
                stale = WindowWorks.App.WindowManager.IsIdentityPermanentlyStale(Hwnd, Identity);
                return false;
            }
            try
            {
                if (ChangeKind == WindowChangeKind.Topmost)
                {
                    wm.SetTopmost(Hwnd, IsTopmost, Identity);
                    return true;
                }
                if (Identity.Value.HostId != 0)
                    throw new InvalidOperationException("Opacity cannot be restored on a reparent host.");
                // Restore exstyle (preserves WS_EX_TRANSPARENT flag as part of ExStyle)
                System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
                int currentStyle = WindowWorks.App.WindowManager.Native.GetWindowLong(Hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE);
                if (currentStyle == 0 && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0)
                    throw new InvalidOperationException("Windows rejected reading the window style.");
                int restoredStyle = (ExStyle & ~WindowWorks.App.WindowManager.Native.WS_EX_TOPMOST) |
                    (currentStyle & WindowWorks.App.WindowManager.Native.WS_EX_TOPMOST);
                WindowWorks.App.WindowManager.RequireIdentity(Hwnd, Identity);
                if (currentStyle != restoredStyle)
                {
                    System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
                    if (WindowWorks.App.WindowManager.Native.SetWindowLong(Hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE, restoredStyle) == 0 &&
                        System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0)
                        throw new InvalidOperationException("Windows rejected restoring the window style.");
                    WindowWorks.App.WindowManager.RequireIdentity(Hwnd, Identity);
                    if (WindowWorks.App.WindowManager.Native.GetWindowLong(Hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE) != restoredStyle)
                        throw new InvalidOperationException("Windows did not restore the original window style.");
                }
                if ((ExStyle & WindowWorks.App.WindowManager.Native.WS_EX_LAYERED) != 0)
                    wm.ApplyOpacity(Hwnd, Opacity, Identity);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                failure = "Undo could not complete: " + ex.Message;
                stale = WindowWorks.App.WindowManager.IsIdentityPermanentlyStale(Hwnd, Identity);
                return false;
            }
        }
    }
}
