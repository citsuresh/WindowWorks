using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App
{
    /// <summary>
    /// DPI-aware crop-rect geometry math for the Crop-and-Reparent feature
    /// (docs/REPARENT_FEATURE_PLAN.md §6.5, Phase 2). Verified against the real, shipped
    /// PowerToys <c>ReparentCropAndLockWindow.cpp</c> / <c>CropAndLock()</c> implementation —
    /// this class intentionally mirrors that approach rather than re-deriving it from scratch.
    ///
    /// This is a pure calculation helper: it has no dependency on the picker UI or
    /// <see cref="ReparentEngine"/>. <see cref="ReparentController"/> (Phase 2 picker-integration
    /// slice) is responsible for calling into this class with a user-drawn crop rect and feeding
    /// the resulting offset/size into <see cref="ReparentEngine.Reparent"/>.
    /// </summary>
    public static class CropRectGeometry
    {
        /// <summary>
        /// Result of <see cref="TryCompute"/>: everything needed to reparent a target window
        /// cropped to a specific screen region.
        /// </summary>
        public sealed class Result
        {
            /// <summary>
            /// The crop rect's width in physical pixels — becomes the host child "socket"
            /// window's exact width (§6.5: "a plain WS_CHILD ... sized to exactly the crop
            /// width/height").
            /// </summary>
            public int CropWidth { get; init; }

            /// <summary>See <see cref="CropWidth"/> (height instead of width).</summary>
            public int CropHeight { get; init; }

            /// <summary>
            /// The x offset to pass as <see cref="ReparentEngine.Reparent"/>'s <c>x</c> parameter
            /// — i.e. the negative of the crop rect's origin translated into the target's own
            /// window-relative coordinate space (§6.5: "the target is moved by the negative
            /// crop-rect origin so that the desired crop region lands at the child window's
            /// (0,0)").
            /// </summary>
            public int TargetOffsetX { get; init; }

            /// <summary>See <see cref="TargetOffsetX"/> (Y instead of X).</summary>
            public int TargetOffsetY { get; init; }

            /// <summary>
            /// The effective per-monitor DPI used for this computation (higher of dpiX/dpiY per
            /// §6.5) — needed by the host frame to size itself correctly via
            /// <c>AdjustWindowRectExForDpi</c>.
            /// </summary>
            public double EffectiveDpi { get; init; } = 96.0;

            /// <summary>
            /// True if <paramref name="targetHwnd"/> was maximized at computation time, meaning
            /// the monitor-work-area special case (§6.5) was used instead of the normal
            /// client/window-rect diff.
            /// </summary>
            public bool WasMaximized { get; init; }
        }

        /// <summary>
        /// Computes the crop geometry for reparenting <paramref name="targetHwnd"/> cropped to
        /// <paramref name="cropRectScreen"/> (a user-drawn selection rectangle in physical screen
        /// pixels, drawn directly over the target's live visible content).
        /// </summary>
        /// <param name="targetHwnd">The window being cropped/reparented.</param>
        /// <param name="cropRectScreen">
        /// The user's selected crop region, in physical screen pixels (e.g. from a drag-to-select
        /// overlay positioned over the target's on-screen bounds).
        /// </param>
        /// <param name="result">The computed geometry, or null if computation failed.</param>
        /// <returns>
        /// False if the target's rect/placement/monitor could not be determined (e.g. the window
        /// went away, or it doesn't currently intersect any monitor — §6.5 explicitly calls this
        /// an edge case to be treated as a failure rather than crashing).
        /// </returns>
        public static bool TryCompute(IntPtr targetHwnd, NativeMethods.RECT cropRectScreen, out Result? result)
        {
            result = null;

            if (targetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(targetHwnd))
            {
                return false;
            }

            if (!NativeMethods.GetWindowRect(targetHwnd, out var windowRect))
            {
                return false;
            }

            var placement = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            bool havePlacement = NativeMethods.GetWindowPlacement(targetHwnd, ref placement);
            bool isMaximized = havePlacement && placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED;

            int diffX;
            int diffY;

            if (isMaximized)
            {
                // §6.5 maximized special case: use the monitor's work-area rect instead of the
                // normal client/window-rect diff, since a maximized window's GetWindowRect can
                // extend slightly beyond the visible work area (the non-client "overhang" trick
                // Windows uses for maximized windows).
                IntPtr monitorForWork = NativeMethods.MonitorFromWindow(targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitorForWork == IntPtr.Zero)
                {
                    return false;
                }

                var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (!NativeMethods.GetMonitorInfo(monitorForWork, ref monitorInfo))
                {
                    return false;
                }

                diffX = monitorInfo.rcWork.Left - windowRect.Left;
                diffY = monitorInfo.rcWork.Top - windowRect.Top;
            }
            else
            {
                // Normal case: diff between the target's client area (in screen space, excluding
                // title bar/borders) and its full window rect.
                if (!NativeMethods.GetClientRect(targetHwnd, out var clientRect))
                {
                    return false;
                }

                var clientOrigin = new NativeMethods.POINT { X = 0, Y = 0 };
                if (!NativeMethods.ClientToScreen(targetHwnd, ref clientOrigin))
                {
                    return false;
                }

                diffX = clientOrigin.X - windowRect.Left;
                diffY = clientOrigin.Y - windowRect.Top;
            }

            // §6.5: crop rect's per-monitor DPI, queried via MonitorFromWindow(target,
            // MONITOR_DEFAULTTONULL) — returns null if the window doesn't currently intersect a
            // monitor, treated as a failure per the plan rather than crashing/defaulting silently.
            IntPtr monitorForDpi = NativeMethods.MonitorFromWindow(targetHwnd, NativeMethods.MONITOR_DEFAULTTONULL);
            if (monitorForDpi == IntPtr.Zero)
            {
                return false;
            }

            double effectiveDpi = 96.0;
            try
            {
                int hr = NativeMethods.GetDpiForMonitor(monitorForDpi, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
                if (hr == 0 /* S_OK */ && dpiX > 0 && dpiY > 0)
                {
                    // Higher of dpiX/dpiY, not an average — per §6.5.
                    effectiveDpi = Math.Max(dpiX, dpiY);
                }
            }
            catch
            {
                // shcore.dll unavailable or call failed — fall back to the 96 DPI default set
                // above rather than failing the whole computation over a DPI-query issue.
            }

            // The selection is screen-relative, while the reparented target's effective origin
            // is its client area (or monitor work area for a maximized target). Convert directly
            // to that content-relative coordinate system before moving the target negatively.
            int contentOriginX = windowRect.Left + diffX;
            int contentOriginY = windowRect.Top + diffY;
            int cropLeftContentRelative = cropRectScreen.Left - contentOriginX;
            int cropTopContentRelative = cropRectScreen.Top - contentOriginY;

            int cropWidth = Math.Max(1, cropRectScreen.Right - cropRectScreen.Left);
            int cropHeight = Math.Max(1, cropRectScreen.Bottom - cropRectScreen.Top);

            result = new Result
            {
                CropWidth = cropWidth,
                CropHeight = cropHeight,
                // §6.5: "the target is moved by the negative crop-rect origin so that the
                // desired crop region lands at the child window's (0,0)".
                TargetOffsetX = -cropLeftContentRelative,
                TargetOffsetY = -cropTopContentRelative,
                EffectiveDpi = effectiveDpi,
                WasMaximized = isMaximized
            };
            return true;
        }

        /// <summary>
        /// Computes the host frame's own window rect (§6.5: "the crop host frame's own window
        /// rect is then computed via <c>AdjustWindowRectExForDpi</c> ... using the higher of
        /// dpiX/dpiY ... as the single effective DPI value for that adjustment call") for a
        /// desired client-area size at the given DPI/style. <paramref name="desiredClientRect"/>
        /// should be a zero-origin rect (0, 0, width, height); the returned rect's width/height
        /// give the outer window size needed to achieve that client area at the given DPI/style.
        /// </summary>
        public static bool TryComputeHostWindowRect(
            NativeMethods.RECT desiredClientRect,
            int style,
            int exStyle,
            double dpi,
            bool hasMenu,
            out NativeMethods.RECT hostWindowRect)
        {
            hostWindowRect = desiredClientRect;
            try
            {
                return NativeMethods.AdjustWindowRectExForDpi(ref hostWindowRect, style, hasMenu, exStyle, (uint)Math.Round(dpi));
            }
            catch
            {
                // AdjustWindowRectExForDpi requires Windows 10 1607+; extremely old OS versions
                // would fail here. Fall back to the un-adjusted client rect rather than throwing.
                hostWindowRect = desiredClientRect;
                return false;
            }
        }

        public static class NativeMethods
        {
            public const int SW_SHOWMAXIMIZED = 3;
            public const uint MONITOR_DEFAULTTONULL = 0;
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const uint MDT_EFFECTIVE_DPI = 0;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct WINDOWPLACEMENT
            {
                public int length;
                public int flags;
                public int showCmd;
                public POINT ptMinPosition;
                public POINT ptMaxPosition;
                public RECT rcNormalPosition;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

            [DllImport("shcore.dll")]
            public static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle, uint dpi);
        }
    }
}
