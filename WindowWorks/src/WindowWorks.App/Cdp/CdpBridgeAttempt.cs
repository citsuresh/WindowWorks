using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Orchestrates the full DevTools read-only property attempt for a single Property Inspector
    /// selection (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 3): discover a
    /// live DevTools endpoint, connect, correlate the picked UIA element to a DOM node, then read
    /// its properties. Returns <c>null</c> at the first unavailable step — DevTools section
    /// absence is the normal/expected outcome whenever the browser wasn't launched with
    /// <c>--remote-debugging-port</c>, so no exception is surfaced to callers for that case, only
    /// for genuine programming errors.
    /// </summary>
    internal static class CdpBridgeAttempt
    {
        public static async Task<CdpNodeProperties?> TryReadAsync(
            AutomationElement selectedElement, IntPtr browserHwnd, CdpCorrelationCache cache)
        {
            return await TryReadOrWriteAsync(selectedElement, browserHwnd, cache, writeStep: null).ConfigureAwait(false);
        }

        /// <summary>
        /// Re-discovers the DevTools endpoint and re-correlates the element from scratch when
        /// possible (see <see cref="TryReadOrWriteAsync"/> for the UIA-anchor-unavailable fallback
        /// to <paramref name="cache"/>), performs the given CSS style write against the resolved
        /// node, then re-reads and returns the refreshed properties so the caller can apply the
        /// plan's "full re-fetch after any write" sync rule with a single round trip.
        /// Returns <c>null</c> if the DevTools connection/correlation is no longer available
        /// (distinct from a write failing after a successful correlation, which is reported via
        /// <paramref name="writeSucceeded"/>).
        /// </summary>
        public static async Task<(CdpNodeProperties? Properties, bool WriteSucceeded)> TryWriteStyleAsync(
            AutomationElement selectedElement,
            IntPtr browserHwnd,
            CdpCorrelationCache cache,
            string cssProperty,
            string value)
        {
            bool writeSucceeded = false;
            var properties = await TryReadOrWriteAsync(
                selectedElement,
                browserHwnd,
                cache,
                writeStep: async (client, backendNodeId) =>
                {
                    writeSucceeded = await CdpPropertyWriter.WriteStyleAsync(client, backendNodeId, cssProperty, value).ConfigureAwait(false);
                }).ConfigureAwait(false);
            return (properties, writeSucceeded);
        }

        private static async Task<CdpNodeProperties?> TryReadOrWriteAsync(
            AutomationElement selectedElement,
            IntPtr browserHwnd,
            CdpCorrelationCache cache,
            Func<CdpClient, int, Task>? writeStep)
        {
            if (!BrowserDomTreeWalker.TryGetBrowserClientScreenRect(browserHwnd, out var browserClientScreenRect))
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            NativeMethods.GetWindowThreadProcessId(browserHwnd, out uint browserProcessId);
            if (browserProcessId == 0)
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            AutomationElement.AutomationElementInformation selectedCurrent;
            try
            {
                selectedCurrent = selectedElement.Current;
            }
            catch (ElementNotAvailableException)
            {
                // The UIA anchor element is gone from the accessibility tree — this is the
                // expected outcome of a prior style.display:none/visibility:hidden write, not
                // necessarily a real failure. Fall back to the last known-good CDP identity for
                // this selection, if any, rather than giving up immediately.
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            if (!BrowserDomTreeWalker.TryGetClippedScreenRect(selectedCurrent, browserClientScreenRect, out var pickedScreenRect))
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            var documentElement = BrowserDomTreeWalker.TryFindDocumentRootFromElement(selectedElement);
            if (documentElement is null)
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            AutomationElement.AutomationElementInformation documentCurrent;
            try
            {
                documentCurrent = documentElement.Current;
            }
            catch (ElementNotAvailableException)
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            if (!BrowserDomTreeWalker.TryGetClippedScreenRect(documentCurrent, browserClientScreenRect, out var documentScreenRect))
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            string? debuggerHttpBaseUrl = await CdpEndpointDiscovery
                .TryFindDebuggerHttpBaseUrlAsync(browserProcessId)
                .ConfigureAwait(false);
            if (debuggerHttpBaseUrl is null)
            {
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }

            try
            {
                var targets = await CdpClient.GetTargetsAsync(debuggerHttpBaseUrl).ConfigureAwait(false);

                // A single browser process can have many open tabs/pages. The DevTools HTTP
                // endpoint has no notion of "which tab is focused/visible", so instead of using
                // whichever page target happens to be first in the list (which could easily
                // correspond to a different tab than the one the user picked from), try each page
                // target in turn and use the first one where correlation actually succeeds - the
                // correlator's own bounding-rect/IoU check (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md
                // §4 Phase E, sub-phase 2) is what makes this a reliable per-tab discriminator.
                foreach (var target in targets)
                {
                    if (!string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrEmpty(target.WebSocketDebuggerUrl))
                    {
                        continue;
                    }

                    CdpDomCorrelationResult correlation;
                    await using (var client = new CdpClient())
                    {
                        await client.ConnectAsync(target.WebSocketDebuggerUrl!).ConfigureAwait(false);
                        correlation = await CdpDomCorrelator.CorrelateAsync(client, documentScreenRect, pickedScreenRect).ConfigureAwait(false);
                        if (correlation.Success)
                        {
                            cache.Update(target.WebSocketDebuggerUrl!, correlation.BackendNodeId, pickedScreenRect);

                            if (writeStep is not null)
                            {
                                await writeStep(client, correlation.BackendNodeId).ConfigureAwait(false);
                            }

                            var readBack = await CdpPropertyReader.ReadAsync(client, correlation.BackendNodeId).ConfigureAwait(false);
                            return readBack;
                        }
                    }
                }

                // Fresh correlation found no match against any current page target (e.g. the
                // element is no longer in the UIA tree so its rect couldn't be used to hit-test,
                // or the hit-test simply didn't land on it this time) — fall back to the cache
                // before giving up entirely.
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or TimeoutException
                or TaskCanceledException or OperationCanceledException or System.Net.WebSockets.WebSocketException
                or System.Net.Http.HttpRequestException)
            {
                // Endpoint discovery said a port was reachable, but the WebSocket connect/command
                // round-trip subsequently failed (e.g. the browser closed the tab in between) —
                // treat this the same as "DevTools unavailable" rather than propagating.
                return await TryFallbackToCacheAsync(cache, writeStep).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Attempts the operation against the last known-good (WebSocket URL, backendNodeId) pair
        /// cached from a previous successful correlation for this selection. Used when live
        /// UIA-rect-based re-correlation is unavailable (most commonly: the element was just
        /// hidden via style.display:none/visibility:hidden, which removes it from the
        /// accessibility tree without removing it from the DOM). Returns <c>null</c> if there is
        /// no cached identity, or if reconnecting/operating against it also fails (e.g. the tab
        /// was closed, or the node was genuinely removed from the DOM/the page navigated).
        /// </summary>
        private static async Task<CdpNodeProperties?> TryFallbackToCacheAsync(
            CdpCorrelationCache cache,
            Func<CdpClient, int, Task>? writeStep)
        {
            if (!cache.TryGet(out var webSocketDebuggerUrl, out var backendNodeId))
            {
                return null;
            }

            try
            {
                await using var client = new CdpClient();
                await client.ConnectAsync(webSocketDebuggerUrl).ConfigureAwait(false);

                // DOM.enable (+ a getDocument call to actually prime the DOM agent) is required
                // before DOM.pushNodesByBackendIdsToFrontend/DOM.describeNode/etc. will resolve
                // anything - the normal correlation path always goes through DOM.enable +
                // DOM.getDocument as part of CdpDomCorrelator, but this fallback path skips
                // correlation entirely, so it must prime the DOM agent itself.
                await client.SendCommandAsync("DOM.enable").ConfigureAwait(false);
                await client.SendCommandAsync("DOM.getDocument", new { depth = 1 }).ConfigureAwait(false);

                if (writeStep is not null)
                {
                    await writeStep(client, backendNodeId).ConfigureAwait(false);
                }

                var readBack = await CdpPropertyReader.ReadAsync(client, backendNodeId).ConfigureAwait(false);
                return readBack;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or TimeoutException
                or TaskCanceledException or OperationCanceledException or System.Net.WebSockets.WebSocketException
                or System.Net.Http.HttpRequestException)
            {
                return null;
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        }
    }
}
