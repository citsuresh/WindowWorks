using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowWorks.App
{
    /// <summary>
    /// One entry in a discovered ancestor chain (§6.2), nearest (deepest hovered child) first.
    /// </summary>
    public sealed class AncestorChainEntry
    {
        public IntPtr Hwnd { get; }
        public string ClassName { get; }
        public string Title { get; }
        public bool IsTopLevel { get; }
        public uint ProcessId { get; }
        public DateTime ProcessStartTimeUtc { get; }
        public string? AutomationRuntimeId { get; }
        /// <summary>
        /// Present for reparenting picks. Inspector-mode discovery defers UIA identity acquisition
        /// to its bounded worker so the picker UI never calls a target UIA provider.
        /// </summary>
        public ReparentEngine.CapturedWindowIdentity? CapturedIdentity { get; }

        public AncestorChainEntry(
            IntPtr hwnd,
            string className,
            string title,
            bool isTopLevel,
            uint processId,
            DateTime processStartTimeUtc,
            string? automationRuntimeId,
            ReparentEngine.CapturedWindowIdentity? capturedIdentity)
        {
            Hwnd = hwnd;
            ClassName = className;
            Title = title;
            IsTopLevel = isTopLevel;
            ProcessId = processId;
            ProcessStartTimeUtc = processStartTimeUtc;
            AutomationRuntimeId = automationRuntimeId;
            CapturedIdentity = capturedIdentity;
        }
    }

    /// <summary>
    /// Ancestor-chain discovery for the window picker (docs/REPARENT_FEATURE_PLAN.md §6.2):
    /// resolves the window under a screen point via <c>WindowFromPoint</c>, then walks up via
    /// repeated <c>GetAncestor(hwnd, GA_PARENT)</c> to <c>GA_ROOT</c>, collecting distinct HWNDs.
    /// Filters out pixel-identical-rect duplicates and invisible/0x0/off-screen helper windows,
    /// per §6.2. Sibling/z-order occlusion picking (EnumWindows + rect-intersection) is explicitly
    /// out of scope for v1 — deferred.
    /// </summary>
    public static class AncestorChainWalker
    {
        private const int MaxLevels = 8;

        /// <summary>
        /// Discovers the ancestor chain under the given screen point. <paramref name="excludeProcessId"/>
        /// filters out every window belonging to that process (used to exclude WindowWorks' own UI,
        /// including transient picker windows themselves, not just a single root HWND).
        ///
        /// Note: this walker no longer filters out specific window classes known to be
        /// non-reparentable (e.g. Chromium's <c>Chrome_RenderWidgetHostHWND</c> GPU-compositor
        /// surface) — per explicit user decision, that would require an ever-growing hardcoded
        /// denylist. Instead, non-reparentable child HWNDs are still offered as picks with no
        /// generic detection/rejection at pick time: an earlier attempt to detect these via the
        /// <c>WS_EX_NOREDIRECTIONBITMAP</c> extended style was tried and reverted (it did not
        /// reliably identify the problematic windows in testing; see
        /// docs/KNOWN_OPEN_FINDINGS.md for the current, accepted, unfixed limitation this leaves
        /// behind — picking such a window produces a blank reparented frame with no warning).
        /// </summary>
        public static List<AncestorChainEntry> Discover(
            int screenX,
            int screenY,
            uint excludeProcessId = 0,
            bool includeAutomationRuntimeId = true)
        {
            var result = new List<AncestorChainEntry>();
            var seen = new HashSet<IntPtr>();
            var seenRects = new HashSet<(int, int, int, int)>();

            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
            {
                return result;
            }

            IntPtr current = hwnd;
            int guard = 0;
            while (current != IntPtr.Zero && guard++ < MaxLevels)
            {
                if (!seen.Contains(current) && IsUsableWindow(current, seenRects, out var rectKey))
                {
                    seen.Add(current);
                    seenRects.Add(rectKey);

                    IntPtr root = NativeMethods.GetAncestor(current, NativeMethods.GA_ROOT);
                    bool isTopLevel = root == current;

                    if (!ReparentEngine.TryGetWindowIdentity(
                        current,
                        out uint pid,
                        out DateTime processStartTimeUtc,
                        out string? className,
                        out string? automationRuntimeId,
                        includeAutomationRuntimeId))
                    {
                        current = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
                        continue;
                    }
                    bool isOwnProcess = excludeProcessId != 0 && pid == excludeProcessId;
                    if (!isOwnProcess)
                    {
                        var capturedIdentity = includeAutomationRuntimeId
                            ? ReparentEngine.CaptureIdentity(
                                current,
                                pid,
                                processStartTimeUtc,
                                className,
                                automationRuntimeId)
                            : null;
                        result.Add(new AncestorChainEntry(
                            current,
                            className ?? string.Empty,
                            GetTitle(current),
                            isTopLevel,
                            pid,
                            processStartTimeUtc,
                            automationRuntimeId,
                            capturedIdentity));
                    }

                    if (isTopLevel)
                    {
                        break;
                    }
                }

                IntPtr parent = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
                if (parent == current)
                {
                    break;
                }
                current = parent;
            }

            return result;
        }

        private static bool IsUsableWindow(IntPtr hwnd, HashSet<(int, int, int, int)> seenRects, out (int, int, int, int) rectKey)
        {
            rectKey = default;
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return false;
            }
            if (!NativeMethods.GetWindowRect(hwnd, out var r))
            {
                return false;
            }
            int width = r.Right - r.Left;
            int height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            rectKey = (r.Left, r.Top, r.Right, r.Bottom);
            if (seenRects.Contains(rectKey))
            {
                // pixel-identical-rect duplicate (e.g. a transparent wrapper matching its child
                // exactly) — skip, per §6.2.
                return false;
            }
            return true;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            try
            {
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            }
            catch { }
            return sb.ToString();
        }

        private static string GetTitle(IntPtr hwnd)
        {
            try
            {
                int len = NativeMethods.GetWindowTextLength(hwnd);
                if (len <= 0)
                {
                    return string.Empty;
                }
                var sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static class NativeMethods
        {
            public const uint GA_PARENT = 1;
            public const uint GA_ROOT = 2;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll")]
            public static extern IntPtr WindowFromPoint(POINT Point);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int GetWindowTextLength(IntPtr hWnd);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        }
    }
}
