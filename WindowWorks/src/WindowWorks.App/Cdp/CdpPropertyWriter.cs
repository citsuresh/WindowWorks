using System;
using System.Threading.Tasks;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Write path for a correlated DOM node (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E,
    /// sub-phase 4): sets a single inline CSS style property (<c>style.display</c> /
    /// <c>style.visibility</c>) via <c>Runtime.callFunctionOn</c> against the resolved JS object,
    /// rather than <c>DOM.setAttributeValue</c> on the raw <c>style</c> attribute string — this
    /// avoids clobbering any other inline styles already present on the element.
    /// </summary>
    internal static class CdpPropertyWriter
    {
        public static async Task<bool> WriteStyleAsync(CdpClient client, int backendNodeId, string cssProperty, string value)
        {
            try
            {
                var resolveResult = await client
                    .SendCommandAsync("DOM.resolveNode", new { backendNodeId })
                    .ConfigureAwait(false);
                string? objectId = resolveResult?["object"]?["objectId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(objectId))
                {
                    return false;
                }

                var callResult = await client
                    .SendCommandAsync(
                        "Runtime.callFunctionOn",
                        new
                        {
                            objectId,
                            functionDeclaration = "function(property, value) { this.style.setProperty(property, value); }",
                            arguments = new object[]
                            {
                                new { value = cssProperty },
                                new { value }
                            },
                            returnByValue = true
                        })
                    .ConfigureAwait(false);

                // Runtime.callFunctionOn does not throw a protocol-level error when the JS
                // function itself throws (e.g. an invalid CSS property/value) — it returns a
                // normal response containing "exceptionDetails" instead. Treat that as a write
                // failure rather than reporting success for a style that was never actually set.
                if (callResult?["exceptionDetails"] is not null)
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (IsExpectedCdpFailure(ex))
            {
                return false;
            }
        }

        /// <summary>
        /// Sets an arbitrary HTML attribute's value via <c>DOM.setAttributeValue</c> (docs/
        /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 5). Unlike
        /// <see cref="WriteStyleAsync"/>, this uses a purpose-built CDP DOM command rather than
        /// executing JS, since setting a plain attribute value doesn't need the JS-object
        /// resolution/"this.style" indirection a CSS property write does. Only edits an attribute
        /// that already exists on the element — adding a brand-new attribute name is out of scope
        /// for this pass (per explicit user decision) but would use the same command.
        /// </summary>
        public static async Task<bool> WriteAttributeAsync(CdpClient client, int backendNodeId, string attributeName, string value)
        {
            try
            {
                // DOM.setAttributeValue requires a "nodeId" (frontend-side, pushed-node
                // identifier), not the "backendNodeId" this class otherwise works with
                // everywhere else -- confirmed live via a real CDP protocol error ("Failed to
                // deserialize params.nodeId - BINDINGS: mandatory field missing") when
                // backendNodeId was passed directly as nodeId. DOM.pushNodesByBackendIdsToFrontend
                // converts a batch of backendNodeIds into frontend nodeIds (pushing the node into
                // CDP's frontend-tracked set if it wasn't already), returning them in the same
                // order as the input array.
                var pushResult = await client
                    .SendCommandAsync("DOM.pushNodesByBackendIdsToFrontend", new { backendNodeIds = new[] { backendNodeId } })
                    .ConfigureAwait(false);
                int? nodeId = pushResult?["nodeIds"]?[0]?.GetValue<int>();
                if (nodeId is null)
                {
                    return false;
                }

                var result = await client
                    .SendCommandAsync("DOM.setAttributeValue", new { nodeId = nodeId.Value, name = attributeName, value })
                    .ConfigureAwait(false);

                // DOM.setAttributeValue returns an empty success object with no result payload to
                // check; a protocol-level failure (e.g. invalid attribute name, node not an
                // Element) throws as an exception from SendCommandAsync rather than returning a
                // normal response with an error field, so reaching this point means it succeeded.
                _ = result;
                return true;
            }
            catch (Exception ex) when (IsExpectedCdpFailure(ex))
            {
                return false;
            }
        }

        private static bool IsExpectedCdpFailure(Exception ex) =>
            ex is InvalidOperationException or FormatException or TimeoutException
                or TaskCanceledException or OperationCanceledException or System.Net.WebSockets.WebSocketException;
    }
}
