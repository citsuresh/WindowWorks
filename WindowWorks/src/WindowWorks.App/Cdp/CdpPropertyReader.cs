using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Read-only DevTools property values for a correlated DOM node
    /// (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 3): <c>style.display</c>,
    /// <c>style.visibility</c>, <c>class</c>, <c>id</c>, <c>innerText</c>, and the computed
    /// bounding box in page (CSS) coordinates. Write path (style.display/style.visibility) is in
    /// <see cref="CdpPropertyWriter"/>, sub-phase 4.
    /// </summary>
    internal sealed class CdpNodeProperties
    {
        public string? Display { get; init; }
        public string? Visibility { get; init; }
        public string? InnerText { get; init; }
        public (double Left, double Top, double Width, double Height)? PageBoundingBox { get; init; }

        /// <summary>
        /// Every HTML attribute present on the element (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md
        /// §4 Phase E, sub-phase 5), in document order as reported by
        /// <c>DOM.describeNode</c>. Includes <c>class</c>/<c>id</c> alongside every other
        /// attribute (e.g. <c>href</c>, <c>src</c>, <c>data-*</c>) rather than special-casing
        /// just those two, since the whole point of sub-phase 5 is generic attribute read/write.
        /// </summary>
        public IReadOnlyList<(string Name, string Value)> Attributes { get; init; } = Array.Empty<(string, string)>();
    }

    /// <summary>
    /// Fetches <see cref="CdpNodeProperties"/> for a backend node id already resolved by
    /// <see cref="CdpDomCorrelator"/>. Kept separate from the correlator itself since correlation
    /// (finding the right node) and property reading (reading values off a known node) are
    /// distinct concerns with independent failure modes.
    /// </summary>
    internal static class CdpPropertyReader
    {
        public static async Task<CdpNodeProperties?> ReadAsync(CdpClient client, int backendNodeId)
        {
            try
            {
                await client.SendCommandAsync("CSS.enable").ConfigureAwait(false);

                var describeResult = await client
                    .SendCommandAsync("DOM.describeNode", new { backendNodeId })
                    .ConfigureAwait(false);
                var node = describeResult?["node"];
                var attributes = ExtractAttributes(node?["attributes"] as JsonArray);

                string? display = null;
                string? visibility = null;
                try
                {
                    var computedStyleResult = await client
                        .SendCommandAsync("CSS.getComputedStyleForNode", new { nodeId = await ResolveNodeIdAsync(client, backendNodeId).ConfigureAwait(false) })
                        .ConfigureAwait(false);
                    if (computedStyleResult?["computedStyle"] is JsonArray computedStyle)
                    {
                        foreach (var entry in computedStyle)
                        {
                            string? name = entry?["name"]?.GetValue<string>();
                            if (name == "display")
                            {
                                display = entry?["value"]?.GetValue<string>();
                            }
                            else if (name == "visibility")
                            {
                                visibility = entry?["value"]?.GetValue<string>();
                            }
                        }
                    }
                }
                catch (Exception ex) when (IsExpectedCdpFailure(ex))
                {
                    // Computed style unavailable (e.g. node detached) — leave display/visibility
                    // null; the rest of the properties can still be reported.
                }

                string? innerText = null;
                try
                {
                    var resolveResult = await client
                        .SendCommandAsync("DOM.resolveNode", new { backendNodeId })
                        .ConfigureAwait(false);
                    string? objectId = resolveResult?["object"]?["objectId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(objectId))
                    {
                        var callResult = await client
                            .SendCommandAsync(
                                "Runtime.callFunctionOn",
                                new
                                {
                                    objectId,
                                    functionDeclaration = "function() { return this.innerText; }",
                                    returnByValue = true
                                })
                            .ConfigureAwait(false);
                        innerText = callResult?["result"]?["value"]?.GetValue<string>();
                    }
                }
                catch (Exception ex) when (IsExpectedCdpFailure(ex))
                {
                    // innerText unavailable (e.g. non-Element node) — leave null.
                }

                (double Left, double Top, double Width, double Height)? boxModel = null;
                try
                {
                    var boxModelResult = await client
                        .SendCommandAsync("DOM.getBoxModel", new { backendNodeId })
                        .ConfigureAwait(false);
                    var border = boxModelResult?["model"]?["border"] as JsonArray;
                    if (border is not null && border.Count >= 8)
                    {
                        double x1 = border[0]!.GetValue<double>(), y1 = border[1]!.GetValue<double>();
                        double x3 = border[4]!.GetValue<double>(), y3 = border[5]!.GetValue<double>();
                        double left = Math.Min(x1, x3);
                        double top = Math.Min(y1, y3);
                        double right = Math.Max(x1, x3);
                        double bottom = Math.Max(y1, y3);
                        boxModel = (left, top, right - left, bottom - top);
                    }
                }
                catch (Exception ex) when (IsExpectedCdpFailure(ex))
                {
                    // No box model (e.g. display:none) — leave null.
                }

                return new CdpNodeProperties
                {
                    Display = display,
                    Visibility = visibility,
                    InnerText = innerText,
                    PageBoundingBox = boxModel,
                    Attributes = attributes
                };
            }
            catch (Exception ex) when (IsExpectedCdpFailure(ex))
            {
                return null;
            }
        }

        private static async Task<int?> ResolveNodeIdAsync(CdpClient client, int backendNodeId)
        {
            // CSS.getComputedStyleForNode requires a nodeId (not backendNodeId) - DOM.pushNodeByBackendIdToFrontend
            // resolves one without needing to re-run DOM.getDocument.
            var pushResult = await client
                .SendCommandAsync("DOM.pushNodesByBackendIdsToFrontend", new { backendNodeIds = new[] { backendNodeId } })
                .ConfigureAwait(false);
            var nodeIds = pushResult?["nodeIds"] as JsonArray;
            return nodeIds is { Count: > 0 } ? nodeIds[0]?.GetValue<int>() : null;
        }

        private static IReadOnlyList<(string Name, string Value)> ExtractAttributes(JsonArray? flatAttributes)
        {
            if (flatAttributes is null)
            {
                return Array.Empty<(string, string)>();
            }

            var attributes = new List<(string Name, string Value)>();
            for (int i = 0; i + 1 < flatAttributes.Count; i += 2)
            {
                string? name = flatAttributes[i]?.GetValue<string>();
                string? value = flatAttributes[i + 1]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    attributes.Add((name, value ?? string.Empty));
                }
            }

            return attributes;
        }

        private static bool IsExpectedCdpFailure(Exception ex) =>
            ex is InvalidOperationException or FormatException or TimeoutException
                or TaskCanceledException or OperationCanceledException or System.Net.WebSockets.WebSocketException;
    }
}
