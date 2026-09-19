using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Re-acquires an inspector selection in a user-requested, newly launched browser process.
    /// This deliberately accepts only one exact DOM-fingerprint match and verifies it back through
    /// UIA; the normal picker remains the safe fallback for every uncertain outcome.
    /// </summary>
    internal static class CdpBrowserRelaunchHandoff
    {
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
        private const int MaxSearchResults = 512;
        private const int MaxUiaCandidateNodes = 512;
        private const int MaxUiaSearchDepth = 20;
        private const double MinUiaBoxOverlap = 0.5;
        private const double NormalizedPositionTolerance = 0.06;
        private const double NormalizedSizeTolerance = 0.08;

        public static bool TryCaptureIdentity(
            PropertyInspectorSelection selection,
            out RelaunchIdentity? identity)
        {
            identity = null;
            try
            {
                var current = selection.SelectedElement.Current;
                if (!TryGetScreenRect(current.BoundingRectangle, out var elementRect))
                {
                    Debug.WriteLine("[DevToolsRelaunch] Original UIA element has no usable bounds for relaunch handoff.");
                    return false;
                }

                var document = BrowserDomTreeWalker.TryFindDocumentRootFromElement(selection.SelectedElement);
                bool hasFingerprint = selection.CdpCache.TryGetFingerprint(out CdpNodeFingerprint fingerprint);
                (double X, double Y, double Width, double Height)? normalizedBounds = null;
                if (HasSpecificUiaIdentity(current)
                    && document is not null
                    && TryGetScreenRect(document.Current.BoundingRectangle, out var documentRect)
                    && TryGetNormalizedBounds(elementRect, documentRect, out var capturedBounds))
                {
                    normalizedBounds = capturedBounds;
                }

                if (!hasFingerprint && normalizedBounds is null)
                {
                    Debug.WriteLine("[DevToolsRelaunch] Cannot capture a strict UIA-only identity and no CDP fingerprint is available.");
                    return false;
                }

                identity = new RelaunchIdentity(
                    hasFingerprint ? fingerprint : null,
                    current.ControlType?.ProgrammaticName ?? string.Empty,
                    current.ClassName ?? string.Empty,
                    current.AutomationId ?? string.Empty,
                    current.Name ?? string.Empty,
                    normalizedBounds);
                Debug.WriteLine(hasFingerprint
                    ? "[DevToolsRelaunch] Captured CDP-fingerprint handoff identity."
                    : "[DevToolsRelaunch] CDP fingerprint unavailable; captured strict UIA-only handoff identity.");
                return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                Debug.WriteLine("[DevToolsRelaunch] Original UIA element became unavailable before relaunch handoff capture.");
                return false;
            }
        }

        /// <summary>
        /// Bounded, off-UI-thread wrapper around <see cref="TryCaptureIdentity"/>. The original
        /// element's UIA calls are synchronous COM calls into a potentially unresponsive
        /// cross-process provider; running them directly on the WPF UI thread (as the caller
        /// otherwise would, before its first await) risks freezing the whole inspector UI if that
        /// provider hangs. This races the capture against a short bounded timeout on a dedicated
        /// long-running background thread and gives up (identity = null) rather than blocking.
        /// </summary>
        public static async Task<RelaunchIdentity?> TryCaptureIdentityAsync(PropertyInspectorSelection selection)
        {
            var captureTask = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        return TryCaptureIdentity(selection, out var identity) ? identity : null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DevToolsRelaunch] Identity capture failed: {ex.Message}");
                        return null;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Task completed = await Task.WhenAny(captureTask, Task.Delay(ReadinessTimeout)).ConfigureAwait(false);
            if (ReferenceEquals(completed, captureTask))
            {
                return await captureTask.ConfigureAwait(false);
            }

            // The original element's UIA provider appears unresponsive. Do not wait on it any
            // further or touch it again; let the caller fall back to the picker.
            ObserveFault(captureTask);
            Debug.WriteLine("[DevToolsRelaunch] Identity capture timed out; skipping handoff.");
            return null;
        }

        public static async Task<PropertyInspectorSelection?> TryFindSelectionAsync(
            int newProcessId,
            RelaunchIdentity identity)
        {
            using var readinessCts = new CancellationTokenSource(ReadinessTimeout);
            CancellationToken cancellationToken = readinessCts.Token;
            DateTime deadline = DateTime.UtcNow + ReadinessTimeout;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    IntPtr browserHwnd = FindTopLevelBrowserWindow((uint)newProcessId);
                    if (browserHwnd != IntPtr.Zero)
                    {
                        PropertyInspectorSelection? selection = await TryFindSelectionOnceAsync(
                            browserHwnd, newProcessId, identity, deadline, readinessCts).ConfigureAwait(false);
                        if (selection is not null)
                        {
                            return selection;
                        }
                    }

                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The fixed readiness window elapsed; the caller opens the ordinary picker.
            }

            Debug.WriteLine("[DevToolsRelaunch] No unique safe handoff match was found before readiness timed out; using the picker.");
            return null;
        }

        private static async Task<PropertyInspectorSelection?> TryFindSelectionOnceAsync(
            IntPtr browserHwnd,
            int newProcessId,
            RelaunchIdentity identity,
            DateTime deadline,
            CancellationTokenSource readinessCts)
        {
            CancellationToken cancellationToken = readinessCts.Token;
            if (identity.Fingerprint is null)
            {
                return await RunBoundedUiaSearchAsync(
                    () => TryFindUiaOnlySelection(browserHwnd, newProcessId, identity, out var uiaSelection)
                        ? uiaSelection
                        : null,
                    deadline,
                    readinessCts).ConfigureAwait(false);
            }

            string? debuggerHttpBaseUrl = await CdpEndpointDiscovery
                .TryFindDebuggerHttpBaseUrlAsync((uint)newProcessId, cancellationToken)
                .ConfigureAwait(false);
            if (debuggerHttpBaseUrl is null)
            {
                return null;
            }

            MatchedDomNode? matchedNode;
            try
            {
                matchedNode = await FindUnambiguousMatchingNodeAsync(
                    debuggerHttpBaseUrl, identity.Fingerprint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedCdpFailure(ex))
            {
                return null;
            }

            if (matchedNode is null)
            {
                return null;
            }

            var selection = await RunBoundedUiaSearchAsync(
                () => TryCreateSelection(browserHwnd, newProcessId, identity, matchedNode.Value, out var candidate)
                    ? candidate
                    : null,
                deadline,
                readinessCts).ConfigureAwait(false);
            if (selection is not null)
            {
                Debug.WriteLine("[DevToolsRelaunch] Reacquired selection through the CDP-fingerprint handoff.");
            }

            return selection;
        }

        private static async Task<PropertyInspectorSelection?> RunBoundedUiaSearchAsync(
            Func<PropertyInspectorSelection?> search,
            DateTime deadline,
            CancellationTokenSource readinessCts)
        {
            Task<PropertyInspectorSelection?> searchTask = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        return search();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DevToolsRelaunch] New-browser UIA search failed: {ex.Message}");
                        return null;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                ObserveFault(searchTask);
                readinessCts.Cancel();
                return null;
            }

            Task completed = await Task.WhenAny(
                searchTask,
                Task.Delay(remaining, readinessCts.Token)).ConfigureAwait(false);
            if (ReferenceEquals(completed, searchTask))
            {
                return await searchTask.ConfigureAwait(false);
            }

            // Do not issue another UIA traversal while this provider call may still be blocked.
            // End the handoff immediately; the controller will open the regular picker.
            ObserveFault(searchTask);
            Debug.WriteLine("[DevToolsRelaunch] New-browser UIA search timed out; using the picker.");
            readinessCts.Cancel();
            return null;
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task<MatchedDomNode?> FindUnambiguousMatchingNodeAsync(
            string debuggerHttpBaseUrl,
            CdpNodeFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<CdpTarget> targets = await CdpClient
                .GetTargetsAsync(debuggerHttpBaseUrl, cancellationToken).ConfigureAwait(false);
            MatchedDomNode? match = null;

            foreach (var target in targets)
            {
                if (!string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                {
                    continue;
                }

                await using var client = new CdpClient();
                try
                {
                    await client.ConnectAsync(target.WebSocketDebuggerUrl!, cancellationToken).ConfigureAwait(false);
                    MatchedDomNode? targetMatch = await FindMatchingNodeInTargetAsync(
                        client, fingerprint, cancellationToken).ConfigureAwait(false);
                    if (targetMatch is null)
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        return null;
                    }

                    match = targetMatch;
                }
                catch (Exception ex) when (IsExpectedCdpFailure(ex))
                {
                    // Browser startup/tab navigation can race an individual target connection.
                    // The bounded outer poll retries it; this attempt simply has no usable match.
                }
            }

            return match;
        }

        private static async Task<MatchedDomNode?> FindMatchingNodeInTargetAsync(
            CdpClient client,
            CdpNodeFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            await client.SendCommandAsync("DOM.enable", cancellationToken: cancellationToken).ConfigureAwait(false);
            await client.SendCommandAsync("DOM.getDocument", new { depth = 1 }, cancellationToken).ConfigureAwait(false);

            JsonNode? search = await client.SendCommandAsync(
                "DOM.performSearch",
                new { query = fingerprint.BuildSearchQuery(), includeUserAgentShadowDOM = true }, cancellationToken).ConfigureAwait(false);
            string? searchId = search?["searchId"]?.GetValue<string>();
            int resultCount = search?["resultCount"]?.GetValue<int>() ?? 0;
            if (string.IsNullOrWhiteSpace(searchId) || resultCount <= 0 || resultCount > MaxSearchResults)
            {
                return null;
            }

            try
            {
                var results = await client.SendCommandAsync(
                    "DOM.getSearchResults",
                    new { searchId, fromIndex = 0, toIndex = resultCount }, cancellationToken).ConfigureAwait(false);
                if (results?["nodeIds"] is not JsonArray nodeIds)
                {
                    return null;
                }

                MatchedDomNode? match = null;
                foreach (var nodeIdNode in nodeIds)
                {
                    int? nodeId = nodeIdNode?.GetValue<int>();
                    if (nodeId is null)
                    {
                        continue;
                    }

                    var describe = await client.SendCommandAsync(
                        "DOM.describeNode", new { nodeId = nodeId.Value }, cancellationToken).ConfigureAwait(false);
                    var node = describe?["node"];
                    string? tagName = node?["nodeName"]?.GetValue<string>();
                    int? backendNodeId = node?["backendNodeId"]?.GetValue<int>();
                    if (backendNodeId is null
                        || !fingerprint.Matches(tagName, ExtractAttributes(node?["attributes"] as JsonArray)))
                    {
                        continue;
                    }

                    var box = await TryGetCssRectAsync(client, nodeId.Value, cancellationToken).ConfigureAwait(false);
                    var viewport = await TryGetViewportAsync(client, cancellationToken).ConfigureAwait(false);
                    if (box is null || viewport is null)
                    {
                        return null;
                    }

                    if (match is not null)
                    {
                        return null;
                    }

                    match = new MatchedDomNode(box.Value, viewport.Value);
                }

                return match;
            }
            finally
            {
                try
                {
                    await client.SendCommandAsync(
                        "DOM.discardSearchResults", new { searchId }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsExpectedCdpFailure(ex))
                {
                    // Cleanup has no effect on the already-computed match and is best-effort.
                }
            }
        }

        private static async Task<(double Left, double Top, double Width, double Height)?> TryGetCssRectAsync(
            CdpClient client,
            int nodeId,
            CancellationToken cancellationToken)
        {
            var boxResult = await client.SendCommandAsync(
                "DOM.getBoxModel", new { nodeId }, cancellationToken).ConfigureAwait(false);
            var border = boxResult?["model"]?["border"] as JsonArray;
            if (border is null || border.Count < 8)
            {
                return null;
            }

            double x1 = border[0]!.GetValue<double>(), y1 = border[1]!.GetValue<double>();
            double x2 = border[2]!.GetValue<double>(), y2 = border[3]!.GetValue<double>();
            double x3 = border[4]!.GetValue<double>(), y3 = border[5]!.GetValue<double>();
            double x4 = border[6]!.GetValue<double>(), y4 = border[7]!.GetValue<double>();
            double left = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
            double top = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
            double right = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
            double bottom = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
            return (left, top, right - left, bottom - top);
        }

        private static async Task<(double Width, double Height, double PageX, double PageY)?> TryGetViewportAsync(
            CdpClient client,
            CancellationToken cancellationToken)
        {
            var metrics = await client.SendCommandAsync("Page.getLayoutMetrics", cancellationToken: cancellationToken).ConfigureAwait(false);
            var viewport = metrics?["cssVisualViewport"];
            double? width = viewport?["clientWidth"]?.GetValue<double>();
            double? height = viewport?["clientHeight"]?.GetValue<double>();
            if (width is null || height is null || width <= 0 || height <= 0)
            {
                return null;
            }

            return (width.Value, height.Value, viewport?["pageX"]?.GetValue<double>() ?? 0, viewport?["pageY"]?.GetValue<double>() ?? 0);
        }

        private static bool TryCreateSelection(
            IntPtr browserHwnd,
            int newProcessId,
            RelaunchIdentity identity,
            MatchedDomNode matchedNode,
            out PropertyInspectorSelection? selection)
        {
            selection = null;
            try
            {
                if (!TryFindDocumentInBrowserWindow(
                        browserHwnd, newProcessId, out var document, out var documentRect))
                {
                    return false;
                }

                double scaleX = (documentRect.Right - documentRect.Left) / matchedNode.Viewport.Width;
                double scaleY = (documentRect.Bottom - documentRect.Top) / matchedNode.Viewport.Height;
                if (scaleX <= 0 || scaleY <= 0)
                {
                    return false;
                }

                var mappedRect = (
                    Left: (int)Math.Round((matchedNode.Box.Left - matchedNode.Viewport.PageX) * scaleX + documentRect.Left),
                    Top: (int)Math.Round((matchedNode.Box.Top - matchedNode.Viewport.PageY) * scaleY + documentRect.Top),
                    Right: (int)Math.Round((matchedNode.Box.Left + matchedNode.Box.Width - matchedNode.Viewport.PageX) * scaleX + documentRect.Left),
                    Bottom: (int)Math.Round((matchedNode.Box.Top + matchedNode.Box.Height - matchedNode.Viewport.PageY) * scaleY + documentRect.Top));
                if (mappedRect.Right <= mappedRect.Left || mappedRect.Bottom <= mappedRect.Top)
                {
                    return false;
                }

                int centerX = (mappedRect.Left + mappedRect.Right) / 2;
                int centerY = (mappedRect.Top + mappedRect.Bottom) / 2;
                var element = AutomationElement.FromPoint(new System.Windows.Point(centerX, centerY));
                if (element is null)
                {
                    return false;
                }

                var current = element.Current;
                if (!HasCompatibleUiaIdentity(current, identity)
                    || !TryGetScreenRect(current.BoundingRectangle, out var elementRect)
                    || ComputeIoU(elementRect, mappedRect) < MinUiaBoxOverlap
                    || !HasExpectedDocumentAncestor(element, document))
                {
                    return false;
                }

                return TryCaptureSelection(browserHwnd, newProcessId, element, out selection);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                return false;
            }
        }

        private static bool TryFindUiaOnlySelection(
                IntPtr browserHwnd,
                int newProcessId,
                RelaunchIdentity identity,
                out PropertyInspectorSelection? selection)
            {
                selection = null;
                if (identity.NormalizedBounds is not { } normalizedBounds)
                {
                    return false;
                }

                if (!TryFindDocumentInBrowserWindow(
                        browserHwnd, newProcessId, out var document, out var documentRect)
                    || !TryFindUniqueUiaCandidate(document, documentRect, identity, normalizedBounds, out var candidate)
                    || !HasExpectedDocumentAncestor(candidate, document))
                {
                    return false;
                }

                if (!TryCaptureSelection(browserHwnd, newProcessId, candidate, out selection))
                {
                    return false;
                }

                Debug.WriteLine("[DevToolsRelaunch] Reacquired selection through the strict UIA-only handoff.");
                return true;
            }

        private static bool TryFindUniqueUiaCandidate(
                AutomationElement document,
                (int Left, int Top, int Right, int Bottom) documentRect,
                RelaunchIdentity identity,
                (double X, double Y, double Width, double Height) expectedNormalizedBounds,
                out AutomationElement candidate)
            {
                candidate = null!;
                var walker = TreeWalker.ControlViewWalker;
                var pending = new Stack<(AutomationElement Element, int Depth)>();
                pending.Push((document, 0));
                int queued = 1;
                int visited = 0;

                try
                {
                    while (pending.Count > 0)
                    {
                        var (currentElement, depth) = pending.Pop();
                        if (++visited > MaxUiaCandidateNodes)
                        {
                            return false;
                        }

                        var current = currentElement.Current;
                        if (HasCompatibleUiaIdentity(current, identity)
                            && TryGetScreenRect(current.BoundingRectangle, out var currentRect)
                            && HasCompatibleNormalizedBounds(currentRect, documentRect, expectedNormalizedBounds))
                        {
                            if (candidate is not null)
                            {
                                return false;
                            }

                            candidate = currentElement;
                        }

                        if (depth >= MaxUiaSearchDepth)
                        {
                            continue;
                        }

                        AutomationElement? child = walker.GetFirstChild(currentElement);
                        while (child is not null)
                        {
                            if (++queued > MaxUiaCandidateNodes)
                            {
                                return false;
                            }

                            pending.Push((child, depth + 1));
                            child = walker.GetNextSibling(child);
                        }
                    }
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
                {
                    return false;
                }

                return candidate is not null;
            }

        private static bool TryCaptureSelection(
                IntPtr browserHwnd,
                int newProcessId,
                AutomationElement element,
                out PropertyInspectorSelection? selection)
            {
                selection = null;
                return TryCreateRootEntry(browserHwnd, newProcessId, out var rootEntry)
                    && PropertyInspectorSelection.TryCapture(rootEntry, element, out selection);
        }

        /// <summary>
        /// Resolves the document from the known new browser HWND's own UIA subtree, rather than
        /// hit-testing a screen coordinate that might be covered by another browser window.
        /// </summary>
        private static bool TryFindDocumentInBrowserWindow(
            IntPtr browserHwnd,
            int expectedProcessId,
            out AutomationElement document,
            out (int Left, int Top, int Right, int Bottom) documentRect)
        {
            document = null!;
            documentRect = default;
            try
            {
                if (GetWindowThreadProcessId(browserHwnd, out uint processId) == 0
                    || processId != (uint)expectedProcessId)
                {
                    return false;
                }

                var browserRoot = AutomationElement.FromHandle(browserHwnd);
                if (browserRoot is null || new IntPtr(browserRoot.Current.NativeWindowHandle) != browserHwnd)
                {
                    return false;
                }

                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
                document = browserRoot.FindFirst(TreeScope.Descendants, condition);
                return document is not null && TryGetScreenRect(document.Current.BoundingRectangle, out documentRect);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                return false;
            }
        }

        private static bool HasExpectedDocumentAncestor(AutomationElement element, AutomationElement expectedDocument)
        {
            AutomationElement? elementDocument = BrowserDomTreeWalker.TryFindDocumentRootFromElement(element);
            if (elementDocument is null)
            {
                return false;
            }

            try
            {
                int[]? elementRuntimeId = elementDocument.GetRuntimeId();
                int[]? expectedRuntimeId = expectedDocument.GetRuntimeId();
                return elementRuntimeId is { Length: > 0 }
                    && expectedRuntimeId is { Length: > 0 }
                    && elementRuntimeId.SequenceEqual(expectedRuntimeId);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                return false;
            }
        }

        private static bool TryCreateRootEntry(IntPtr browserHwnd, int expectedProcessId, out AncestorChainEntry rootEntry)
        {
            rootEntry = null!;
            if (!ReparentEngine.TryGetWindowIdentity(
                    browserHwnd,
                    out uint processId,
                    out DateTime processStartTimeUtc,
                    out string? className,
                    out string? runtimeId)
                || processId != (uint)expectedProcessId
                || !BrowserClassifier.IsChromiumFamily(className ?? string.Empty))
            {
                return false;
            }

            rootEntry = new AncestorChainEntry(
                browserHwnd,
                className ?? string.Empty,
                title: string.Empty,
                isTopLevel: true,
                processId,
                processStartTimeUtc,
                runtimeId,
                capturedIdentity: null);
            return true;
        }

        private static bool HasCompatibleUiaIdentity(
            AutomationElement.AutomationElementInformation current,
            RelaunchIdentity identity)
        {
            return string.Equals(current.ControlType?.ProgrammaticName ?? string.Empty, identity.ControlType, StringComparison.Ordinal)
                && MatchesIfCaptured(current.ClassName, identity.ClassName)
                && MatchesIfCaptured(current.AutomationId, identity.AutomationId)
                && MatchesIfCaptured(current.Name, identity.Name);
        }

        private static bool HasSpecificUiaIdentity(AutomationElement.AutomationElementInformation current)
        {
            return !string.IsNullOrWhiteSpace(current.ControlType?.ProgrammaticName)
                && (!string.IsNullOrWhiteSpace(current.ClassName)
                    || !string.IsNullOrWhiteSpace(current.AutomationId)
                    || !string.IsNullOrWhiteSpace(current.Name));
        }

        private static bool MatchesIfCaptured(string? candidate, string expected) =>
            string.IsNullOrEmpty(expected) || string.Equals(candidate ?? string.Empty, expected, StringComparison.Ordinal);

        private static bool TryGetNormalizedBounds(
            (int Left, int Top, int Right, int Bottom) elementRect,
            (int Left, int Top, int Right, int Bottom) documentRect,
            out (double X, double Y, double Width, double Height) normalized)
        {
            normalized = default;
            double documentWidth = documentRect.Right - documentRect.Left;
            double documentHeight = documentRect.Bottom - documentRect.Top;
            if (documentWidth <= 0 || documentHeight <= 0)
            {
                return false;
            }

            double x = (elementRect.Left - documentRect.Left) / documentWidth;
            double y = (elementRect.Top - documentRect.Top) / documentHeight;
            double width = (elementRect.Right - elementRect.Left) / documentWidth;
            double height = (elementRect.Bottom - elementRect.Top) / documentHeight;
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(width) || double.IsNaN(height)
                || double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(width) || double.IsInfinity(height)
                || width <= 0 || height <= 0
                || x < -NormalizedPositionTolerance || y < -NormalizedPositionTolerance
                || x + width > 1 + NormalizedPositionTolerance
                || y + height > 1 + NormalizedPositionTolerance)
            {
                return false;
            }

            normalized = (x, y, width, height);
            return true;
        }

        private static bool HasCompatibleNormalizedBounds(
            (int Left, int Top, int Right, int Bottom) candidateRect,
            (int Left, int Top, int Right, int Bottom) documentRect,
            (double X, double Y, double Width, double Height) expected)
        {
            return TryGetNormalizedBounds(candidateRect, documentRect, out var actual)
                && Math.Abs(actual.X - expected.X) <= NormalizedPositionTolerance
                && Math.Abs(actual.Y - expected.Y) <= NormalizedPositionTolerance
                && Math.Abs(actual.Width - expected.Width) <= NormalizedSizeTolerance
                && Math.Abs(actual.Height - expected.Height) <= NormalizedSizeTolerance;
        }

        private static IntPtr FindTopLevelBrowserWindow(uint processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)
                    || GetWindowThreadProcessId(hwnd, out uint pid) == 0
                    || pid != processId
                    || GetAncestor(hwnd, GaRoot) != hwnd)
                {
                    return true;
                }

                if (ReparentEngine.TryGetWindowIdentity(
                    hwnd,
                    out uint ignoredProcessId,
                    out DateTime ignoredProcessStartTimeUtc,
                    out string? className,
                    out string? ignoredRuntimeId,
                    includeAutomationRuntimeId: false)
                    && BrowserClassifier.IsChromiumFamily(className ?? string.Empty))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IReadOnlyDictionary<string, string> ExtractAttributes(JsonArray? flatAttributes)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (flatAttributes is null)
            {
                return attributes;
            }

            for (int i = 0; i + 1 < flatAttributes.Count; i += 2)
            {
                string? name = flatAttributes[i]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    attributes[name] = flatAttributes[i + 1]?.GetValue<string>() ?? string.Empty;
                }
            }

            return attributes;
        }

        private static bool TryGetScreenRect(System.Windows.Rect rect, out (int Left, int Top, int Right, int Bottom) result)
        {
            result = default;
            if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)
                || rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            result = (
                (int)Math.Round(rect.Left),
                (int)Math.Round(rect.Top),
                (int)Math.Round(rect.Right),
                (int)Math.Round(rect.Bottom));
            return result.Right > result.Left && result.Bottom > result.Top;
        }

        private static double ComputeIoU(
            (int Left, int Top, int Right, int Bottom) a,
            (int Left, int Top, int Right, int Bottom) b)
        {
            int left = Math.Max(a.Left, b.Left);
            int top = Math.Max(a.Top, b.Top);
            int right = Math.Min(a.Right, b.Right);
            int bottom = Math.Min(a.Bottom, b.Bottom);
            double intersection = right > left && bottom > top ? (right - left) * (double)(bottom - top) : 0;
            double areaA = Math.Max(0, a.Right - a.Left) * (double)Math.Max(0, a.Bottom - a.Top);
            double areaB = Math.Max(0, b.Right - b.Left) * (double)Math.Max(0, b.Bottom - b.Top);
            double union = areaA + areaB - intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private static bool IsExpectedCdpFailure(Exception ex) =>
            ex is InvalidOperationException or FormatException or TimeoutException
                or TaskCanceledException or OperationCanceledException or WebSocketException or HttpRequestException;

        internal sealed record RelaunchIdentity(
            CdpNodeFingerprint? Fingerprint,
            string ControlType,
            string ClassName,
            string AutomationId,
            string Name,
            (double X, double Y, double Width, double Height)? NormalizedBounds);

        private readonly record struct MatchedDomNode(
            (double Left, double Top, double Width, double Height) Box,
            (double Width, double Height, double PageX, double PageY) Viewport);

        private const uint GaRoot = 2;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
