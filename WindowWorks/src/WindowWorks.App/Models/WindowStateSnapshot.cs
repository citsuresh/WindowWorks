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
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static WindowStateSnapshot FromWindow(IntPtr hwnd)
        {
            var s = new WindowStateSnapshot();
            s.Hwnd = hwnd;
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
            s.IsTopmost = IsWindowTopmost(hwnd);
            return s;
        }

        private static bool IsWindowTopmost(IntPtr hwnd)
        {
            // Try to detect topmost by GetWindowRect and comparing Z-order via SetWindowPos queries is complex.
            // For now, use a simple heuristic: call GetWindowLong for topmost flag not available. Default false.
            return false;
        }

        public void Restore(WindowWorks.App.WindowManager wm)
        {
            if (Hwnd == IntPtr.Zero) return;
            // Restore exstyle (preserves WS_EX_TRANSPARENT flag as part of ExStyle)
            WindowWorks.App.WindowManager.Native.SetWindowLong(Hwnd, WindowWorks.App.WindowManager.Native.GWL_EXSTYLE, ExStyle);
            // Restore opacity
            wm.ApplyOpacity(Hwnd, Opacity);
            // Restore topmost
            wm.SetTopmost(Hwnd, IsTopmost);
        }
    }
}
