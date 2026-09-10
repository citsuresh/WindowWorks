using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App
{
    /// <summary>
    /// Core Win32 SetParent/WS_CHILD save-restore mechanics for the Window Reparenting feature.
    /// Implements the exact sequencing verified against the real PowerToys Crop-and-Lock source
    /// (ReparentCropAndLockWindow.cpp), per docs/REPARENT_FEATURE_PLAN.md §8.
    ///
    /// Phase 0 scope only: whole-window reparent/save/restore mechanics, callable directly
    /// against a manually-picked target window. No picker UI, no tracking list, no crash
    /// recovery, no elevation check, and no ancestor-chain child-HWND original-parent restore
    /// branch (that branch is Phase 1 scope) — those all build on top of this class later.
    /// </summary>
    public sealed class ReparentEngine
    {
        /// <summary>
        /// Saved pre-reparent state for a single target window, produced by
        /// <see cref="SaveOriginalState"/> and consumed by <see cref="RestoreOriginalState"/>.
        /// </summary>
        public sealed class ReparentedWindowState
        {
            public IntPtr TargetHwnd { get; init; }
            public int ExStyle { get; init; }
            public int Style { get; init; }
            public NativeMethods.WINDOWPLACEMENT Placement { get; init; }
            public NativeMethods.RECT OriginalRect { get; init; }
        }

        /// <summary>
        /// Captures the target window's current extended style, style, placement, and screen
        /// rect so it can later be restored via <see cref="RestoreOriginalState"/>. Must be
        /// called before <see cref="Reparent"/> (§8 step 2).
        /// </summary>
        public ReparentedWindowState SaveOriginalState(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero)
            {
                throw new ArgumentException("targetHwnd must not be IntPtr.Zero.", nameof(targetHwnd));
            }

            int exStyle = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_EXSTYLE);
            int style = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_STYLE);

            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
            };
            if (!NativeMethods.GetWindowPlacement(targetHwnd, ref placement))
            {
                throw new InvalidOperationException("GetWindowPlacement failed for the target window.");
            }

            if (!NativeMethods.GetWindowRect(targetHwnd, out var rect))
            {
                throw new InvalidOperationException("GetWindowRect failed for the target window.");
            }

            return new ReparentedWindowState
            {
                TargetHwnd = targetHwnd,
                ExStyle = exStyle,
                Style = style,
                Placement = placement,
                OriginalRect = rect
            };
        }

        /// <summary>
        /// Reparents the target window into <paramref name="hostChildHwnd"/>, following the
        /// exact order verified against the real, shipped PowerToys
        /// <c>ReparentCropAndLockWindow.cpp</c> / <c>CropAndLock()</c> implementation (§8 steps
        /// 4-5): <c>SetParent</c> first, then OR in <c>WS_CHILD</c> (existing style bits,
        /// including any pre-existing <c>WS_POPUP</c>, are left as-is — PowerToys does not clear
        /// it), then <c>SetWindowPos</c> with <c>SWP_FRAMECHANGED</c> to force Windows to
        /// re-evaluate the non-client area after the style change.
        /// </summary>
        /// <param name="targetHwnd">The window being reparented.</param>
        /// <param name="hostChildHwnd">The host frame's child "socket" window.</param>
        /// <param name="x">
        /// X offset within the host's client area. 0 for whole-window mode (Phase 0/1); the
        /// negative crop-rect origin in crop mode (Phase 2 — not used here).
        /// </param>
        /// <param name="y">Y offset within the host's client area. See <paramref name="x"/>.</param>
        /// <returns>
        /// True if the final <c>SetWindowPos</c> call succeeded. A false return means the target
        /// app may not tolerate reparenting well (§8 step 6) — callers should surface this via an
        /// inline notice, not attempt to silently proceed.
        /// </returns>
        public bool Reparent(IntPtr targetHwnd, IntPtr hostChildHwnd, int x = 0, int y = 0)
        {
            if (targetHwnd == IntPtr.Zero)
            {
                throw new ArgumentException("targetHwnd must not be IntPtr.Zero.", nameof(targetHwnd));
            }
            if (hostChildHwnd == IntPtr.Zero)
            {
                throw new ArgumentException("hostChildHwnd must not be IntPtr.Zero.", nameof(hostChildHwnd));
            }

            IntPtr setParentResult = NativeMethods.SetParent(targetHwnd, hostChildHwnd);
            if (setParentResult == IntPtr.Zero)
            {
                DebugLog($"Reparent: SetParent(target={targetHwnd}, host={hostChildHwnd}) failed, GetLastError={Marshal.GetLastWin32Error()}");
                return false;
            }

            // DESIGN CHANGE (per explicit user request, superseding the earlier mouse-hook-based
            // drag/resize-ghosting fix below): rather than reparenting the target's whole window
            // (title bar + resize border still present, just made inert), strip its own
            // WS_CAPTION/WS_THICKFRAME/WS_MINIMIZEBOX/WS_MAXIMIZEBOX/WS_SYSMENU chrome bits
            // entirely, so only its client-area *content* is reparented — filling the host frame's
            // socket with no native title bar/border at all. Only the host frame's own chrome
            // controls move/resize going forward. This mirrors the plan's own §6.5/§8-cited
            // PowerToys crop-mode precedent of stripping WS_THICKFRAME/WS_MAXIMIZEBOX, just applied
            // unconditionally to whole-window reparenting too (not only crop mode), and makes the
            // previous mouse-hook workaround for drag/resize ghosting unnecessary — removed below.
            int style = NativeMethods.GetWindowLong(targetHwnd, NativeMethods.GWL_STYLE);
            int strippedStyle = (style | NativeMethods.WS_CHILD) &
                ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_MINIMIZEBOX |
                  NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU);
            NativeMethods.SetWindowLong(targetHwnd, NativeMethods.GWL_STYLE, strippedStyle);

            bool posOk = NativeMethods.SetWindowPos(
                targetHwnd,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOZORDER);
            if (!posOk)
            {
                DebugLog($"Reparent: SetWindowPos(target={targetHwnd}, x={x}, y={y}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }

            return posOk;
        }


        // Diagnostic logging for failure paths only (appended to a temp file, mirroring
        // HotkeyManager.DebugLog's pattern) — kept permanently, not just for the original
        // blank-host-frame investigation, since these are exactly the kind of interop calls that
        // can fail silently against an incompatible target app in the field.
        private static void DebugLog(string message)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windowworks_reparent_log.txt");
                System.IO.File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " [ReparentEngine] " + message + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// Restores the target window to its original top-level state, following the exact
        /// order verified against the real PowerToys source (§8 step 8): rect (<c>SetWindowPos</c>)
        /// -> unparent (<c>SetParent(..., null)</c>) -> placement (<c>SetWindowPlacement</c>) ->
        /// styles-last. Whole-window case only — reparenting a whole top-level window's original
        /// parent is implicitly the desktop, so unparenting to <c>IntPtr.Zero</c> is always
        /// correct here. Ancestor-chain child-HWND picks (restoring into a real original parent
        /// HWND instead) are Phase 1 scope, not implemented in this method.
        /// </summary>
        public bool RestoreOriginalState(ReparentedWindowState state)
        {
            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            IntPtr hwnd = state.TargetHwnd;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            int width = state.OriginalRect.Right - state.OriginalRect.Left;
            int height = state.OriginalRect.Bottom - state.OriginalRect.Top;
            bool posOk = NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                state.OriginalRect.Left,
                state.OriginalRect.Top,
                width,
                height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            if (!posOk)
            {
                DebugLog($"RestoreOriginalState: SetWindowPos(target={hwnd}) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }

            bool unparented = NativeMethods.SetParent(hwnd, IntPtr.Zero) != IntPtr.Zero;
            if (!unparented)
            {
                DebugLog($"RestoreOriginalState: SetParent(target={hwnd}, null) failed, GetLastError={Marshal.GetLastWin32Error()}");
            }

            var placement = state.Placement;
            NativeMethods.SetWindowPlacement(hwnd, ref placement);

            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, state.ExStyle);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, state.Style & ~NativeMethods.WS_CHILD);

            // The restore is only considered successful if the target was actually made
            // top-level again (SetParent succeeding is the operation that matters most for
            // correctness); a failed SetWindowPos is a lesser, non-fatal geometry issue but is
            // still folded into the result so callers can surface an inline notice (§8 step 6
            // equivalent) rather than assuming a silent full success.
            return unparented && posOk;
        }

        public static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int GWL_STYLE = -16;
            public const int WS_CHILD = 0x40000000;

            // Chrome bits stripped from the target after reparenting (per explicit user request):
            // reparent only the target's client-area content, not its own window frame — only the
            // host frame's chrome should be visible/interactive going forward.
            public const int WS_CAPTION = 0x00C00000;
            public const int WS_THICKFRAME = 0x00040000;
            public const int WS_MINIMIZEBOX = 0x00020000;
            public const int WS_MAXIMIZEBOX = 0x00010000;
            public const int WS_SYSMENU = 0x00080000;

            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_FRAMECHANGED = 0x0020;

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

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
            private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
            public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            public static int GetWindowLong(IntPtr hWnd, int nIndex) => (int)GetWindowLongPtr(hWnd, nIndex).ToInt64();

            public static int SetWindowLong(IntPtr hWnd, int nIndex, int newValue) =>
                (int)SetWindowLongPtr(hWnd, nIndex, new IntPtr(newValue)).ToInt64();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetParent(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool IsWindow(IntPtr hWnd);
        }
    }
}
