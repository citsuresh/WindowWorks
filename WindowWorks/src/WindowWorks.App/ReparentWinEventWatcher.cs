using System;
using System.Runtime.InteropServices;

namespace WindowWorks.App
{
    /// <summary>
    /// Wraps a native <c>SetWinEventHook(EVENT_OBJECT_DESTROY, ...)</c> registration
    /// (docs/REPARENT_FEATURE_PLAN.md §12/§14 Phase 1 item 8) so <see cref="ReparentController"/>
    /// can detect a reparented target window being destroyed externally (e.g. the source app
    /// crashes or is force-closed while embedded) instead of polling for it.
    ///
    /// Implementation details called out explicitly in the plan, all honored here:
    /// - <c>SetWinEventHook</c> is not itself HWND-filtered — it fires for every window in the
    ///   system, so <see cref="WinEventCallback"/> must and does check <c>idObject == OBJID_WINDOW</c>
    ///   and <c>idChild == 0</c> to ignore destroy events for unrelated child objects/controls,
    ///   and callers are responsible for checking the reported HWND against whatever they're
    ///   actually tracking (this class does not know about the tracking list itself).
    /// - Uses <c>WINEVENT_OUTOFCONTEXT</c> so the callback runs on this process's own thread via
    ///   its normal message loop, avoiding cross-process DLL-injection complexity.
    /// - The callback delegate is rooted as a field for the lifetime of the hook registration —
    ///   a delegate that gets garbage collected while the native hook still references it would
    ///   crash or silently stop firing.
    /// - <see cref="Stop"/> explicitly calls <c>UnhookWinEvent</c>; also invoked from
    ///   <see cref="Dispose"/> so this class is safe to use in a <c>using</c>/field-disposal
    ///   pattern.
    /// </summary>
    public sealed class ReparentWinEventWatcher : IDisposable
    {
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;

        // Rooted for the hook's lifetime — see class remarks.
        private readonly NativeMethods.WinEventProc _procDelegate;
        private IntPtr _hook = IntPtr.Zero;

        public ReparentWinEventWatcher()
        {
            _procDelegate = WinEventCallback;
        }

        /// <summary>
        /// Raised when any top-level window in the system is destroyed. Callers must check the
        /// reported HWND against whatever they're tracking themselves — this class deliberately
        /// has no knowledge of the reparent tracking list, to keep it independently testable/
        /// reusable.
        /// </summary>
        public event Action<IntPtr>? WindowDestroyed;

        /// <summary>
        /// Registers the WinEvent hook if not already registered. Safe to call multiple times
        /// (no-ops if already started).
        /// </summary>
        public void Start()
        {
            if (_hook != IntPtr.Zero)
            {
                return;
            }

            _hook = NativeMethods.SetWinEventHook(
                EVENT_OBJECT_DESTROY,
                EVENT_OBJECT_DESTROY,
                IntPtr.Zero,
                _procDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);
        }

        /// <summary>
        /// Unregisters the WinEvent hook if currently registered. Safe to call multiple times.
        /// </summary>
        public void Stop()
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        {
            // SetWinEventHook is not HWND-filtered — filter here to only the specific
            // (window, not child-object) destroy notifications the plan calls out.
            if (eventType != EVENT_OBJECT_DESTROY || idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero)
            {
                return;
            }

            WindowDestroyed?.Invoke(hwnd);
        }

        public void Dispose()
        {
            Stop();
        }

        private static class NativeMethods
        {
            public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWinEventHook(
                uint eventMin,
                uint eventMax,
                IntPtr hmodWinEventProc,
                WinEventProc lpfnWinEventProc,
                uint idProcess,
                uint idThread,
                uint dwFlags);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        }
    }
}
