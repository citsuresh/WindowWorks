using System;
using System.Runtime.InteropServices;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Orchestrates the Window Reparenting feature's "pop out and reparent" flow
    /// (docs/REPARENT_FEATURE_PLAN.md §14 Phase 1, Parts 1+4): resolves the window under the
    /// cursor when the reparent hotkey is pressed, and drives <see cref="ReparentEngine"/> +
    /// <see cref="ReparentHostWindow"/> to save state, reparent, and (on host close) restore it.
    ///
    /// **Placeholder picking only** — this class resolves the target via naive
    /// <c>WindowFromPoint</c> + <c>GetAncestor(..., GA_ROOT)</c> root-level resolution (Part 4),
    /// with no ancestor-chain discovery and no yellow-box confirm UI. It exists purely to give a
    /// real, pressable end-to-end path so Phase 0's save/reparent/restore mechanics can finally be
    /// manually tested. The real picker (§6.2/§6.3) is expected to supersede this class's picking
    /// logic later within Phase 1 — the <see cref="ReparentEngine"/>/<see cref="ReparentHostWindow"/>
    /// usage below is expected to remain, just driven by a real picker instead of this naive one.
    ///
    /// Whole-window case only, mirroring <see cref="ReparentEngine"/>'s current Phase 0 scope: no
    /// ancestor-chain child-HWND restore-to-original-parent branch, no tracking list, no crash
    /// recovery, no elevation check. Only a single reparent-in-flight at a time is supported here
    /// (single-owner limitation, §4) — a second hotkey press while a host is already open is
    /// ignored rather than allowing two concurrent reparents from this placeholder path.
    /// </summary>
    public sealed class ReparentController
    {
        private readonly ReparentEngine _engine = new();
        private ReparentHostWindow? _activeHost;
        private ReparentEngine.ReparentedWindowState? _activeState;

        /// <summary>
        /// Invoked when the reparent hotkey is pressed. Resolves the window under the cursor
        /// (naive root-level pick, Part 4) and reparents it into a new <see cref="ReparentHostWindow"/>.
        /// No-ops (silently) if a reparent is already in progress, if no usable window is found
        /// under the cursor, or if the resolved window is this process's own UI (never offer to
        /// reparent WindowWorks' own windows).
        /// </summary>
        public void InvokePicker()
        {
            if (_activeHost != null)
            {
                // Single-owner limitation (§4): don't allow a second concurrent reparent from
                // this placeholder entry point.
                return;
            }

            IntPtr target = GetNaiveRootWindowUnderCursor();
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                return;
            }

            if (IsOwnProcessWindow(target))
            {
                return;
            }

            ReparentEngine.ReparentedWindowState state;
            try
            {
                state = _engine.SaveOriginalState(target);
            }
            catch
            {
                // Target went away or state couldn't be read — nothing to reparent.
                return;
            }

            var host = new ReparentHostWindow();
            host.Title = "WindowWorks — Reparented Window";
            bool reparented = false;
            host.SocketReady += (_, _) =>
            {
                if (host.SocketHwnd == IntPtr.Zero)
                {
                    // Socket window creation failed (e.g. RegisterClass/CreateWindowEx error) —
                    // nothing to reparent into. Surface this to the user instead of leaving a
                    // silently blank host frame open, and close the (empty) host.
                    System.Windows.MessageBox.Show(
                        "WindowWorks couldn't create the reparent host frame's internal window. The reparent was not performed.",
                        "Window Reparenting Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    host.Close();
                    return;
                }

                reparented = _engine.Reparent(target, host.SocketHwnd);
                if (reparented)
                {
                    host.AttachTarget(target);
                }
                else
                {
                    // Reparent failed (target may not tolerate WS_CHILD/SetParent, §8 step 6) —
                    // surface this instead of leaving an empty, non-functional frame open, and
                    // close the host rather than showing nothing with no explanation.
                    System.Windows.MessageBox.Show(
                        "This window could not be reparented. It may not support this operation (some apps are incompatible with window reparenting).",
                        "Window Reparenting Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    host.Close();
                }
            };

            host.RestoreRequested += (_, _) =>
            {
                // Only restore if Reparent() actually succeeded — SaveOriginalState alone doesn't
                // touch the target, so there's nothing to undo (and calling RestoreOriginalState
                // on a target that was never reparented risks needlessly re-applying its own
                // already-current style/placement).
                if (!reparented)
                {
                    return;
                }

                try
                {
                    _engine.RestoreOriginalState(state);
                }
                catch { }
            };

            host.Closed += (_, _) =>
            {
                if (ReferenceEquals(_activeHost, host))
                {
                    _activeHost = null;
                    _activeState = null;
                }
            };

            _activeHost = host;
            _activeState = state;
            host.Show();
        }

        /// <summary>
        /// Naive placeholder window picking (§14 Phase 1 Part 4): resolves the topmost window at
        /// the current cursor position via <c>WindowFromPoint</c>, then walks up to its root
        /// ancestor via <c>GetAncestor(hwnd, GA_ROOT)</c>. No ancestor-chain discovery, no
        /// filtering of invisible/helper windows beyond what <c>WindowFromPoint</c> itself
        /// already skips — deliberately minimal, per the plan's placeholder scope.
        /// </summary>
        private static IntPtr GetNaiveRootWindowUnderCursor()
        {
            if (!NativeMethods.GetCursorPos(out var pt))
            {
                return IntPtr.Zero;
            }

            IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root != IntPtr.Zero ? root : hwnd;
        }

        private static bool IsOwnProcessWindow(IntPtr hwnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                return pid == (uint)Environment.ProcessId;
            }
            catch
            {
                return false;
            }
        }

        private static class NativeMethods
        {
            public const uint GA_ROOT = 2;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern IntPtr WindowFromPoint(POINT Point);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        }
    }
}
