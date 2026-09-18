using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Temporary, manual-only verification harness for docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md
    /// §4 Phase E sub-phases 1-2 (CDP bridge proof of concept + element correlation). Not wired
    /// into any UI or hotkey — call <see cref="RunAsync"/>/<see cref="RunCorrelationAsync"/>
    /// directly (e.g. via a temporary call site in Program.cs guarded by a command-line switch)
    /// against a real Chromium-family browser launched with <c>--remote-debugging-port=9222</c>.
    ///
    /// This class is intentionally throwaway scaffolding, kept around per explicit user request
    /// (as of sub-phase 1 completion) to support manual verification of sub-phase 2's element
    /// correlation as well, rather than being removed after sub-phase 1.
    /// </summary>
    internal static class CdpBridgePoc
    {
        /// <summary>
        /// Connects to the given DevTools HTTP endpoint, lists targets, connects to the first
        /// "page" target found, evaluates a trivial expression, and returns a human-readable
        /// summary string for manual inspection (e.g. via a MessageBox or console/log output).
        /// </summary>
        public static async Task<string> RunAsync(string debuggerHttpBaseUrl = "http://localhost:9222")
        {
            var report = new System.Text.StringBuilder();

            var targets = await CdpClient.GetTargetsAsync(debuggerHttpBaseUrl).ConfigureAwait(false);
            report.AppendLine($"Found {targets.Count} target(s) at {debuggerHttpBaseUrl}:");
            foreach (var t in targets)
            {
                report.AppendLine($"  [{t.Type}] {t.Title} — {t.Url}");
            }

            CdpTarget? pageTarget = null;
            foreach (var t in targets)
            {
                if (string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(t.WebSocketDebuggerUrl))
                {
                    pageTarget = t;
                    break;
                }
            }

            if (pageTarget is null)
            {
                report.AppendLine("No connectable 'page' target found. Is a tab open in the browser?");
                return report.ToString();
            }

            await using var client = new CdpClient();
            await client.ConnectAsync(pageTarget.WebSocketDebuggerUrl!).ConfigureAwait(false);
            report.AppendLine($"Connected to target: {pageTarget.Title}");

            var result = await client.SendCommandAsync("Runtime.evaluate", new
            {
                expression = "1 + 1",
                returnByValue = true
            }).ConfigureAwait(false);

            report.AppendLine($"Runtime.evaluate(\"1 + 1\") result: {result?.ToJsonString() ?? "(null)"}");

            return report.ToString();
        }

        /// <summary>
        /// Sub-phase 2 manual verification: finds whatever DOM element is currently under the
        /// mouse cursor (must be hovering a Chromium-family browser window's page content when
        /// this is invoked — give yourself a few seconds after launching to position the cursor),
        /// picks the deepest UIA element there via the same <see cref="BrowserDomTreeWalker"/>
        /// used by the real picker, then attempts to correlate it to a CDP DOM node and reports
        /// the match (or failure) with full diagnostics for manual eyeballing.
        /// </summary>
        public static async Task<string> RunCorrelationAsync(string debuggerHttpBaseUrl = "http://localhost:9222")
        {
            var report = new System.Text.StringBuilder();

            // WindowWorks.App has no DPI-awareness manifest, so by default this thread's Win32/UIA
            // calls (GetCursorPos, AutomationElement.FromPoint bounding rects, GetClientRect) run
            // in a virtualized 96-DPI coordinate space and get silently scaled by Windows, while
            // CDP/Chromium (itself DPI-aware) reports true physical-pixel coordinates. On a
            // 150%-scaled display this showed up as a consistent ~1.5x mismatch between UIA rects
            // and CDP rects that looked like a calibration bug but wasn't one. Rather than guess
            // at a scale factor, switch this thread to PER_MONITOR_AWARE_V2 for the duration of
            // the correlation call so every Win32/UIA call below returns real physical pixels,
            // matching CDP's coordinate space directly. Scoped to this thread only (not the whole
            // app) since sub-phase 2 is the first feature sensitive to this gap; app-wide
            // DPI-awareness is a separate, bigger change deferred per user decision.
            IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previousDpiContext == IntPtr.Zero)
            {
                // Setting PER_MONITOR_AWARE_V2 failed (e.g. an OS version predating Windows 10
                // 1703's PMv2 support). Proceeding anyway would silently run the rest of this
                // method in whatever ambient DPI-awareness the thread already had, reproducing
                // the exact virtualized-96-DPI mismatch this fix exists to avoid — but without
                // this note, a resulting low-confidence/failed correlation would look identical
                // to a genuine no-match rather than a DPI setup failure.
                report.AppendLine("WARNING: SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2) failed; " +
                    "proceeding with ambient DPI awareness. Screen-pixel rects below may be virtualized " +
                    "(non-physical) coordinates, which will skew calibration against CDP's physical-pixel rects.");
            }

            try
            {
                return await RunCorrelationCoreAsync(debuggerHttpBaseUrl, report).ConfigureAwait(false);
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                {
                    NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext);
                }
            }
        }

        private static async Task<string> RunCorrelationCoreAsync(string debuggerHttpBaseUrl, System.Text.StringBuilder report)
        {
            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                return "GetCursorPos failed.";
            }
            report.AppendLine($"Cursor pos: ({cursor.X},{cursor.Y})");

            IntPtr topLevelHwnd = NativeMethods.WindowFromPoint(cursor);
            topLevelHwnd = NativeMethods.GetAncestor(topLevelHwnd, NativeMethods.GA_ROOT);

            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(topLevelHwnd, className, className.Capacity);
            if (!BrowserClassifier.IsChromiumFamily(className.ToString()))
            {
                report.AppendLine($"Window under cursor (class '{className}') is not a recognized Chromium-family browser. " +
                    "Hover over a browser tab's page content and retry.");
                return report.ToString();
            }

            var chain = BrowserDomTreeWalker.Discover(topLevelHwnd, cursor.X, cursor.Y);
            if (chain.Count == 0)
            {
                report.AppendLine("BrowserDomTreeWalker found no DOM element under the cursor.");
                return report.ToString();
            }

            var pickedEntry = chain[0];
            report.AppendLine($"Chain length: {chain.Count}");
            foreach (var e in chain)
            {
                report.AppendLine($"  [{e.ControlTypeName}] '{e.Name}' rect=({e.ClippedScreenRect.Left},{e.ClippedScreenRect.Top})-" +
                    $"({e.ClippedScreenRect.Right},{e.ClippedScreenRect.Bottom})");
            }
            report.AppendLine($"Picked (raw) UIA element: [{pickedEntry.ControlTypeName}] '{pickedEntry.Name}' " +
                $"rect=({pickedEntry.ClippedScreenRect.Left},{pickedEntry.ClippedScreenRect.Top})-" +
                $"({pickedEntry.ClippedScreenRect.Right},{pickedEntry.ClippedScreenRect.Bottom})");

            // The Discover() chain only includes entries whose bounding rect survived clipping
            // (TryBuildEntry can skip the Document node itself if its own rect is degenerate at
            // the edges) — use the dedicated TryFindDocumentRoot + manual clip instead of relying
            // on chain membership, so a missing chain entry doesn't block correlation entirely.
            var documentElement = BrowserDomTreeWalker.TryFindDocumentRoot(topLevelHwnd, cursor.X, cursor.Y);
            if (documentElement is null)
            {
                report.AppendLine("BrowserDomTreeWalker.TryFindDocumentRoot found no Document-type ancestor to use as the calibration anchor.");
                return report.ToString();
            }

            if (!BrowserDomTreeWalker.TryGetBrowserClientScreenRect(topLevelHwnd, out var browserClientScreenRect))
            {
                report.AppendLine("Could not resolve the browser window's client rect.");
                return report.ToString();
            }

            if (!BrowserDomTreeWalker.TryGetClippedScreenRect(documentElement.Current, browserClientScreenRect, out var documentScreenRect))
            {
                report.AppendLine("Document root element has no usable (non-degenerate) bounding rect after clipping.");
                return report.ToString();
            }

            report.AppendLine($"Picked UIA element: [{pickedEntry.ControlTypeName}] '{pickedEntry.Name}' " +
                $"rect=({pickedEntry.ClippedScreenRect.Left},{pickedEntry.ClippedScreenRect.Top})-" +
                $"({pickedEntry.ClippedScreenRect.Right},{pickedEntry.ClippedScreenRect.Bottom})");
            report.AppendLine($"Document anchor rect=({documentScreenRect.Left},{documentScreenRect.Top})-" +
                $"({documentScreenRect.Right},{documentScreenRect.Bottom})");


            var targets = await CdpClient.GetTargetsAsync(debuggerHttpBaseUrl).ConfigureAwait(false);
            CdpTarget? pageTarget = null;
            foreach (var t in targets)
            {
                if (string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(t.WebSocketDebuggerUrl))
                {
                    pageTarget = t;
                    break;
                }
            }

            if (pageTarget is null)
            {
                report.AppendLine("No connectable 'page' target found via CDP.");
                return report.ToString();
            }

            report.AppendLine($"CDP target: {pageTarget.Title} — {pageTarget.Url}");

            await using var client = new CdpClient();
            await client.ConnectAsync(pageTarget.WebSocketDebuggerUrl!).ConfigureAwait(false);

            var correlation = await CdpDomCorrelator.CorrelateAsync(
                client,
                documentScreenRect,
                pickedEntry.ClippedScreenRect).ConfigureAwait(false);

            report.AppendLine($"Correlation success={correlation.Success}, confidence={correlation.Confidence:F2}");
            report.AppendLine($"Diagnostics: {correlation.Diagnostics}");

            return report.ToString();
        }

        private static class NativeMethods
        {
            public const uint GA_ROOT = 2;

            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, as a predefined pointer value per
            // Win32 conventions (winuser.h defines this as (DPI_AWARENESS_CONTEXT)-4).
            public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern IntPtr WindowFromPoint(POINT point);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }
        }
    }
}
