using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Correlates a UIA-picked browser DOM element to its corresponding CDP DOM node
    /// (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 2).
    ///
    /// UIA and CDP have entirely unrelated node identity schemes, so there is no direct lookup —
    /// this uses a two-step heuristic:
    ///   1. Calibrate a screen-pixel-to-CSS-pixel affine transform using the UIA "Document"
    ///      element's own bounding rect (physical screen pixels — the actual visible content
    ///      area) against <c>Page.getLayoutMetrics</c>'s CSS visual viewport size. (Two earlier,
    ///      wrong versions were tried: anchoring on &lt;html&gt;'s CDP box model instead of
    ///      Page.getLayoutMetrics — wrong because &lt;html&gt;'s box model reflects the full
    ///      scrollable content size, not the visible viewport; and anchoring on the browser
    ///      window's outer client rect instead of the UIA Document rect — wrong because the
    ///      client rect includes toolbar/tab-strip chrome above the actual page content, which
    ///      doesn't correspond to any CSS-pixel rect at all.)
    ///   2. Apply that transform to the picked element's UIA rect to get an approximate CSS-pixel
    ///      point, hit-test it via <c>DOM.getNodeForLocation</c>, then verify the match by
    ///      transforming the resulting node's own CDP box model back to screen pixels and
    ///      computing intersection-over-union (IoU) against the original UIA rect.
    ///
    /// This is explicitly a heuristic, not a guaranteed 1:1 mapping (browser zoom, scrollbars,
    /// and sub-pixel rounding can all introduce error) — <see cref="CdpDomCorrelationResult"/>
    /// reports a confidence score and diagnostics so real-world reliability can be judged.
    /// </summary>
    internal static class CdpDomCorrelator
    {
        /// <summary>
        /// Attempts to correlate <paramref name="pickedElementScreenRect"/> (the UIA-picked
        /// element's bounding rect, in physical screen pixels) to a CDP DOM node, using
        /// <paramref name="documentScreenRect"/> (the UIA Document-type ancestor's bounding rect,
        /// also physical screen pixels) as the calibration anchor.
        /// </summary>
        public static async Task<CdpDomCorrelationResult> CorrelateAsync(
            CdpClient client,
            (int Left, int Top, int Right, int Bottom) documentScreenRect,
            (int Left, int Top, int Right, int Bottom) pickedElementScreenRect)
        {
            try
            {
                await client.SendCommandAsync("DOM.enable").ConfigureAwait(false);

                var docResult = await client.SendCommandAsync("DOM.getDocument", new { depth = 1 })
                    .ConfigureAwait(false);
                int? rootNodeId = docResult?["root"]?["nodeId"]?.GetValue<int>();
                if (rootNodeId is null)
                {
                    return CdpDomCorrelationResult.Failed("DOM.getDocument returned no root node id.");
                }

                var layoutMetrics = await client.SendCommandAsync("Page.getLayoutMetrics").ConfigureAwait(false);
                var visualViewport = layoutMetrics?["cssVisualViewport"];
                double? viewportWidth = visualViewport?["clientWidth"]?.GetValue<double>();
                double? viewportHeight = visualViewport?["clientHeight"]?.GetValue<double>();
                double viewportPageX = visualViewport?["pageX"]?.GetValue<double>() ?? 0.0;
                double viewportPageY = visualViewport?["pageY"]?.GetValue<double>() ?? 0.0;
                if (viewportWidth is null || viewportHeight is null)
                {
                    return CdpDomCorrelationResult.Failed("Page.getLayoutMetrics returned no cssVisualViewport size.");
                }

                int docWidth = documentScreenRect.Right - documentScreenRect.Left;
                int docHeight = documentScreenRect.Bottom - documentScreenRect.Top;
                if (docWidth <= 0 || docHeight <= 0 || viewportWidth.Value <= 0 || viewportHeight.Value <= 0)
                {
                    return CdpDomCorrelationResult.Failed("Degenerate calibration rect (zero width/height).");
                }

                double scaleX = docWidth / viewportWidth.Value;
                double scaleY = docHeight / viewportHeight.Value;

                // Screen-px -> CSS-px (viewport-relative, i.e. what DOM.getNodeForLocation
                // expects) transform, anchored at the UIA Document rect's top-left mapping to
                // the visual viewport's own top-left (viewportPageX/Y is normally 0 unless the
                // page itself is pinch-zoomed, but not assumed to be 0).
                double ScreenXToCss(double screenX) => (screenX - documentScreenRect.Left) / scaleX + viewportPageX;
                double ScreenYToCss(double screenY) => (screenY - documentScreenRect.Top) / scaleY + viewportPageY;
                double CssXToScreen(double cssX) => (cssX - viewportPageX) * scaleX + documentScreenRect.Left;
                double CssYToScreen(double cssY) => (cssY - viewportPageY) * scaleY + documentScreenRect.Top;

                double centerCssX = ScreenXToCss((pickedElementScreenRect.Left + pickedElementScreenRect.Right) / 2.0);
                double centerCssY = ScreenYToCss((pickedElementScreenRect.Top + pickedElementScreenRect.Bottom) / 2.0);

                var locationResult = await client
                    .SendCommandAsync(
                        "DOM.getNodeForLocation",
                        new { x = (int)Math.Round(centerCssX), y = (int)Math.Round(centerCssY), includeUserAgentShadowDOM = true })
                    .ConfigureAwait(false);
                int? backendNodeId = locationResult?["backendNodeId"]?.GetValue<int>();
                if (backendNodeId is null)
                {
                    return CdpDomCorrelationResult.Failed(
                        $"DOM.getNodeForLocation found no node at CSS ({centerCssX:F1}, {centerCssY:F1}).");
                }

                var describeResult = await client
                    .SendCommandAsync("DOM.describeNode", new { backendNodeId = backendNodeId.Value })
                    .ConfigureAwait(false);
                var node = describeResult?["node"];
                string? tagName = node?["nodeName"]?.GetValue<string>()?.ToLowerInvariant();
                var (classAttr, idAttr) = ExtractClassAndId(node?["attributes"] as JsonArray);

                double confidence;
                string diagnostics;
                var nodeRectAttempt = await TryGetCssRectAsync(client, nodeId: null, backendNodeId: backendNodeId.Value)
                    .ConfigureAwait(false);
                if (!nodeRectAttempt.Success)
                {
                    // Node was found by location but has no box model (e.g. display:none or an
                    // inline text node) — still report the match but with low confidence since
                    // overlap cannot be verified.
                    confidence = 0.3;
                    diagnostics = $"Matched <{tagName}> via getNodeForLocation, but no box model available " +
                        "to verify overlap (element may be non-rendered).";
                }
                else
                {
                    var nodeCssRect = nodeRectAttempt.Rect;
                    var matchedScreenRect = (
                        Left: (int)Math.Round(CssXToScreen(nodeCssRect.Left)),
                        Top: (int)Math.Round(CssYToScreen(nodeCssRect.Top)),
                        Right: (int)Math.Round(CssXToScreen(nodeCssRect.Left + nodeCssRect.Width)),
                        Bottom: (int)Math.Round(CssYToScreen(nodeCssRect.Top + nodeCssRect.Height)));

                    double iou = ComputeIoU(pickedElementScreenRect, matchedScreenRect);
                    confidence = iou;
                    diagnostics = $"Matched <{tagName}> id='{idAttr}' class='{classAttr}'; " +
                        $"IoU(UIA rect, transformed CDP box)={iou:F2}; " +
                        $"UIA rect=({pickedElementScreenRect.Left},{pickedElementScreenRect.Top})-" +
                        $"({pickedElementScreenRect.Right},{pickedElementScreenRect.Bottom}); " +
                        $"CDP rect (screen-mapped)=({matchedScreenRect.Left},{matchedScreenRect.Top})-" +
                        $"({matchedScreenRect.Right},{matchedScreenRect.Bottom}); " +
                        $"calibration scale=({scaleX:F3},{scaleY:F3})";
                }

                return CdpDomCorrelationResult.Matched(
                    backendNodeId.Value, tagName, null, classAttr, idAttr, confidence, diagnostics);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or TimeoutException
                or TaskCanceledException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
            {
                // These are the "expected, heuristic-can-legitimately-fail" cases: a CDP field
                // present but of an unexpected JSON kind (InvalidOperationException/FormatException
                // from JsonNode.GetValue<T>()), or the WebSocket/command round-trip timing out or
                // being cancelled. Reported as an ordinary correlation failure.
                return CdpDomCorrelationResult.Failed($"Correlation failed: {ex.Message}");
            }
            // Any other exception (e.g. NullReferenceException, InvalidCastException) indicates an
            // unexpected CDP response shape or a genuine programming error, not a normal "no match
            // found" case - let it propagate so it is not silently reported as a 0-confidence
            // correlation failure indistinguishable from a real non-match.
        }

        private static async Task<(bool Success, (double Left, double Top, double Width, double Height) Rect)> TryGetCssRectAsync(
            CdpClient client,
            int? nodeId,
            int? backendNodeId)
        {
            object @params = nodeId is not null
                ? new { nodeId = nodeId.Value }
                : new { backendNodeId = backendNodeId!.Value };

            JsonNode? result;
            try
            {
                result = await client.SendCommandAsync("DOM.getBoxModel", @params).ConfigureAwait(false);
            }
            catch
            {
                return (false, default);
            }

            var border = result?["model"]?["border"] as JsonArray;
            if (border is null || border.Count < 8)
            {
                return (false, default);
            }

            double x1 = border[0]!.GetValue<double>(), y1 = border[1]!.GetValue<double>();
            double x2 = border[2]!.GetValue<double>(), y2 = border[3]!.GetValue<double>();
            double x3 = border[4]!.GetValue<double>(), y3 = border[5]!.GetValue<double>();
            double x4 = border[6]!.GetValue<double>(), y4 = border[7]!.GetValue<double>();

            double left = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
            double top = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
            double right = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
            double bottom = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));

            return (true, (left, top, right - left, bottom - top));
        }

        private static (string? ClassAttr, string? IdAttr) ExtractClassAndId(JsonArray? flatAttributes)
        {
            if (flatAttributes is null)
            {
                return (null, null);
            }

            string? className = null;
            string? id = null;
            for (int i = 0; i + 1 < flatAttributes.Count; i += 2)
            {
                string? name = flatAttributes[i]?.GetValue<string>();
                string? value = flatAttributes[i + 1]?.GetValue<string>();
                if (name == "class")
                {
                    className = value;
                }
                else if (name == "id")
                {
                    id = value;
                }
            }

            return (className, id);
        }

        private static double ComputeIoU(
            (int Left, int Top, int Right, int Bottom) a,
            (int Left, int Top, int Right, int Bottom) b)
        {
            int left = Math.Max(a.Left, b.Left);
            int top = Math.Max(a.Top, b.Top);
            int right = Math.Min(a.Right, b.Right);
            int bottom = Math.Min(a.Bottom, b.Bottom);

            double intersection = (right > left && bottom > top) ? (right - left) * (double)(bottom - top) : 0.0;
            double areaA = Math.Max(0, a.Right - a.Left) * (double)Math.Max(0, a.Bottom - a.Top);
            double areaB = Math.Max(0, b.Right - b.Left) * (double)Math.Max(0, b.Bottom - b.Top);
            double union = areaA + areaB - intersection;

            return union <= 0 ? 0.0 : intersection / union;
        }
    }
}
