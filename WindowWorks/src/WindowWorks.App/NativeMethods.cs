using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App
{
    internal static partial class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        public static POINT GetCursorPoint()
        {
            GetCursorPos(out var p);
            return p;
        }

        public static POINT POINT_FROM_GETCURSORPOS()
        {
            GetCursorPos(out var p);
            return p;
        }

        // POINT is already defined in HotkeyManager.NativeMethods; avoid duplicate definition here.
        [StructLayout(LayoutKind.Sequential)]
        public struct RawPOINT { public int X; public int Y; }
    }
}
