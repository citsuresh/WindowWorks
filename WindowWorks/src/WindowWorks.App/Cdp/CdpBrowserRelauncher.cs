using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Implements the "Open a DevTools-enabled copy of this page" assist (docs/
    /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 6). WindowWorks otherwise never
    /// launches/relaunches a browser (see <see cref="CdpEndpointDiscovery"/>'s doc comment) — this
    /// is an explicit, user-confirmed exception to that rule, only triggered by the user directly
    /// clicking the button after confirming a popup.
    ///
    /// Originally this closed (<c>WM_CLOSE</c>) and relaunched the *same* browser process, but
    /// live testing showed two problems: (1) Chromium-family browsers often keep a background
    /// process alive after all windows close (for notifications/sync), so a plain
    /// <c>Process.Start</c> with the same exe/profile silently hands off to that still-running
    /// background instance via its single-instance mutex/lock, discarding the new
    /// <c>--remote-debugging-port</c> flag entirely; and (2) closing the user's real window is
    /// destructive/risky to automate reliably. Instead, this launches a brand-new, independent
    /// browser process pointed at a separate temporary <c>--user-data-dir</c> — Chromium allows
    /// multiple concurrent processes as long as each uses a distinct profile directory, so this
    /// reliably starts a fresh instance with debugging enabled, at the same URL, without touching
    /// the user's original window/tab/profile at all. Does not attempt to auto-restore the
    /// previously-picked element's selection — the new instance is a different process with
    /// different UIA/CDP node identities. The controller may nevertheless perform a separate,
    /// strict best-effort handoff using a cached process-independent DOM fingerprint; it always
    /// falls back to the normal picker when that fingerprint is unavailable or ambiguous.
    /// </summary>
    internal static class CdpBrowserRelauncher
    {
        private const string RemoteDebuggingArgument = "--remote-debugging-port=9222";
        private const string BlankUrl = "about:blank";
        private static readonly TimeSpan DevToolsReadinessTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DevToolsReadinessPollInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// <paramref name="NewProcessId"/> is populated only when a new process was actually
        /// started (i.e. <paramref name="Succeeded"/> is <c>true</c>) — callers use it to attempt
        /// re-finding the same element in the new window (docs/
        /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 6 follow-up).
        /// </summary>
        public readonly record struct RelaunchResult(bool Succeeded, string Message, int? NewProcessId = null);

        /// <summary>
        /// Resolves the owning process/executable of <paramref name="browserHwnd"/> and the
        /// current page URL (best-effort, via the browser's own address bar), then launches a new
        /// instance of that same executable with <c>--remote-debugging-port=9222</c> and a fresh
        /// temporary <c>--user-data-dir</c>, navigated to that URL. The original window is never
        /// touched.
        /// </summary>
        public static Task<RelaunchResult> RelaunchWithDevToolsAsync(IntPtr browserHwnd)
        {
            return Task.Run(() => RelaunchWithDevToolsCoreAsync(browserHwnd));
        }

        private static async Task<RelaunchResult> RelaunchWithDevToolsCoreAsync(IntPtr browserHwnd)
        {
            GetWindowThreadProcessId(browserHwnd, out uint processId);
            if (processId == 0)
            {
                return new RelaunchResult(false, "Could not identify the browser process to launch a copy of.");
            }

            string? exePath = CdpEndpointDiscovery.TryGetProcessExecutablePath(processId);
            if (string.IsNullOrEmpty(exePath))
            {
                return new RelaunchResult(false, "Could not resolve the browser's executable path.");
            }

            string url = TryReadCurrentUrl(browserHwnd) ?? BlankUrl;
            string profileDir = Path.Combine(Path.GetTempPath(), "WindowWorksDevToolsProfile");
            string windowArgs = TryBuildWindowGeometryArguments(browserHwnd);

            Process? process;
            try
            {
                Directory.CreateDirectory(profileDir);

                process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{RemoteDebuggingArgument} --user-data-dir=\"{profileDir}\" --no-first-run --new-window{windowArgs} \"{url}\"",
                    UseShellExecute = false
                });

                if (process is null)
                {
                    return new RelaunchResult(false, "The browser process could not be started.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return new RelaunchResult(false, $"Opening a DevTools-enabled copy failed: {ex.Message}");
            }

            int newProcessId = process.Id;
            process.Dispose();

            if (!await WaitForDevToolsEndpointAsync(newProcessId).ConfigureAwait(false))
            {
                return new RelaunchResult(
                    false,
                    "The browser copy opened, but it did not establish a DevTools endpoint on port 9222. " +
                    "The current inspector was left open.");
            }

            string urlNote = string.Equals(url, BlankUrl, StringComparison.Ordinal)
                ? " The page's URL could not be read automatically, so the new window opened blank — please navigate to the page yourself."
                : string.Empty;

            return new RelaunchResult(
                true,
                "A separate DevTools-enabled browser window was opened (temporary profile) at the same page. " +
                "Your original window was not affected." + urlNote,
                newProcessId);
        }

        private static async Task<bool> WaitForDevToolsEndpointAsync(int processId)
        {
            using var cts = new CancellationTokenSource(DevToolsReadinessTimeout);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    string? endpoint = await CdpEndpointDiscovery
                        .TryFindDebuggerHttpBaseUrlAsync((uint)processId, cts.Token)
                        .ConfigureAwait(false);
                    if (endpoint is not null)
                    {
                        return true;
                    }

                    await Task.Delay(DevToolsReadinessPollInterval, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The browser launched, but its process never became the responsive owner of 9222.
            }

            return false;
        }

        /// <summary>
        /// Best-effort read of the current tab's URL via the browser's own address bar (an
        /// <c>Edit</c> control exposing <c>ValuePattern</c>, present in the toolbar of every
        /// Chromium-family browser tested). Returns <c>null</c> (not an exception) if the address
        /// bar can't be found/read — callers fall back to opening a blank tab in that case.
        /// </summary>
        private static string? TryReadCurrentUrl(IntPtr browserHwnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(browserHwnd);
                if (root is null)
                {
                    return null;
                }

                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var editControls = root.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement candidate in editControls)
                {
                    if (!candidate.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
                    {
                        continue;
                    }

                    if (patternObj is not ValuePattern valuePattern)
                    {
                        continue;
                    }

                    string? value = valuePattern.Current.Value;
                    if (LooksLikeUrl(value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                // Best-effort only - any UIA failure (COM exception, element gone, etc.) falls
                // back to the caller's blank-tab handling rather than surfacing an error here.
            }

            return null;
        }

        private static bool LooksLikeUrl(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && (value.Contains("://", StringComparison.Ordinal)
                    || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    || value.Contains('.', StringComparison.Ordinal));
        }

        /// <summary>
        /// Best-effort read of the original window's screen position/size, translated into
        /// Chromium's <c>--window-position</c>/<c>--window-size</c> launch arguments so the new
        /// DevTools-enabled copy opens at roughly the same place/size as the window the user was
        /// just looking at. Returns an empty string (not an exception) if the rect can't be read —
        /// the new window then opens at Chromium's own default position/size.
        ///
        /// Switches this thread to PER_MONITOR_AWARE_V2 for the duration of the
        /// <c>GetWindowRect</c> call, same fix/rationale as <c>CdpBridgePoc</c>'s DPI-calibration
        /// code: without it, <c>GetWindowRect</c> returns virtualized (DPI-scaled, not real
        /// physical-pixel) coordinates on this thread. However, Chromium's own
        /// <c>--window-size</c>/<c>--window-position</c> flags are documented/confirmed live to
        /// take *logical* (DIP, 96-DPI-relative) pixels, not physical ones — Chromium re-scales
        /// them itself by the target monitor's own scale factor. Passing the raw physical-pixel
        /// rect (correct for everything else in this codebase, which talks to Win32/UIA/CDP,
        /// all physical-pixel APIs) produced a new window ~1.5x too large on this 150%-scaled
        /// monitor. So the physical-pixel rect is divided by the window's own DPI scale factor
        /// (<c>GetDpiForWindow</c> / 96) before being passed to Chromium.
        /// </summary>
        private static string TryBuildWindowGeometryArguments(IntPtr browserHwnd)
        {
            IntPtr previousDpiContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
            try
            {
                if (!GetWindowRect(browserHwnd, out var rect))
                {
                    return string.Empty;
                }

                int physicalWidth = rect.Right - rect.Left;
                int physicalHeight = rect.Bottom - rect.Top;
                if (physicalWidth <= 0 || physicalHeight <= 0)
                {
                    return string.Empty;
                }

                double scale = GetWindowDpiScale(browserHwnd);

                int left = (int)Math.Round(rect.Left / scale);
                int top = (int)Math.Round(rect.Top / scale);
                int width = (int)Math.Round(physicalWidth / scale);
                int height = (int)Math.Round(physicalHeight / scale);

                return $" --window-position={left},{top} --window-size={width},{height}";
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or ExternalException)
            {
                return string.Empty;
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                {
                    SetThreadDpiAwarenessContext(previousDpiContext);
                }
            }
        }

        /// <summary>
        /// Returns the target window's DPI scale factor (e.g. 1.5 for 150% scaling), falling back
        /// to 1.0 (no scaling) if <c>GetDpiForWindow</c> is unavailable/fails/returns 0 — this
        /// requires Windows 10 1607+; on older systems the geometry is simply left un-rescaled
        /// rather than throwing.
        /// </summary>
        private static double GetWindowDpiScale(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            catch (EntryPointNotFoundException)
            {
                return 1.0;
            }
        }

        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
