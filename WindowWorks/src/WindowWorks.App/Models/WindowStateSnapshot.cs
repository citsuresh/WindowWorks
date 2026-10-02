using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App.Models
{
    /// <summary>
    /// Snapshot of a window state before modification. Minimal fields required to restore opacity and topmost.
    /// </summary>
    public class WindowStateSnapshot
    {
        public IntPtr Hwnd { get; set; }
        public int ExStyle { get; set; }
        public int Opacity { get; set; } // 0-100
        public bool IsTopmost { get; set; }
        internal WindowWorks.App.WindowManager.WindowIdentity? Identity { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static WindowStateSnapshot FromWindow(IntPtr hwnd)
        {
            var s = new WindowStateSnapshot();
            s.Hwnd = hwnd;
            s.Identity = WindowWorks.App.WindowManager.CaptureIdentity(hwnd);
            s.ExStyle = WindowWorks.App.WindowManager.Native.GetWindowLong(hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE);
            // Determine opacity by querying layered window attributes when available.
            try
            {
                if (WindowWorks.App.WindowManager.Native.GetLayeredWindowAttributes(hwnd, out uint key, out byte alpha, out uint flags))
                {
                    // If layered alpha flag present, convert 0-255 alpha to 0-100 percent
                    if ((flags & WindowWorks.App.WindowManager.Native.LWA_ALPHA) != 0)
                    {
                        s.Opacity = (int)Math.Round(alpha * 100.0 / 255.0);
                    }
                    else
                    {
                        s.Opacity = 100;
                    }
                }
                else
                {
                    s.Opacity = 100;
                }
            }
            catch
            {
                s.Opacity = 100;
            }
            s.IsTopmost = WindowWorks.App.WindowManager.IsTopmost(hwnd);
            return s;
        }

        public void Restore(WindowWorks.App.WindowManager wm)
        {
            if (Identity is null || WindowWorks.App.WindowManager.CaptureIdentity(Hwnd) != Identity) return;
            if (Identity.Value.HostId != 0)
            {
                wm.SetTopmost(Hwnd, IsTopmost);
                return;
            }
            // Restore exstyle (preserves WS_EX_TRANSPARENT flag as part of ExStyle)
            int currentStyle = WindowWorks.App.WindowManager.Native.GetWindowLong(Hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE);
            int restoredStyle = (ExStyle & ~WindowWorks.App.WindowManager.Native.WS_EX_TOPMOST) |
                (currentStyle & WindowWorks.App.WindowManager.Native.WS_EX_TOPMOST);
            WindowWorks.App.WindowManager.Native.SetWindowLong(Hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE, restoredStyle);
            // Restore opacity
            wm.ApplyOpacity(Hwnd, Opacity);
            // Restore topmost
            wm.SetTopmost(Hwnd, IsTopmost);
        }
    }
}
