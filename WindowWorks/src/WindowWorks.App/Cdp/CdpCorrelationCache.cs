namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Caches the last successful CDP correlation (target WebSocket URL + backend node id) for a
    /// single Property Inspector selection (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E,
    /// sub-phase 4 follow-up).
    ///
    /// Per the CDP spec, a <c>backendNodeId</c> remains valid for as long as the underlying DOM
    /// node exists at all — it is NOT invalidated by style changes (including
    /// <c>display: none</c>/<c>visibility: hidden</c>, which remove the node from the
    /// accessibility/UIA tree but not from the DOM itself) and does NOT require staying connected
    /// to the same WebSocket (reconnecting to the same target and reusing the id is valid). It
    /// only becomes stale if the node is actually removed from the DOM or the page
    /// navigates/reloads (a new document invalidates all previously-known backend node ids).
    ///
    /// This exists specifically to support the "hide element, then show it again" round trip:
    /// once <c>style.display: none</c> is applied, the UIA-rect-based correlation path can no
    /// longer find the element (it's gone from the accessibility tree), so a follow-up write
    /// falls back to this cached identity instead of failing outright.
    /// </summary>
    internal sealed class CdpCorrelationCache
    {
        private readonly object _gate = new();
        private string? _webSocketDebuggerUrl;
        private int? _backendNodeId;
        private (int X, int Y)? _lastKnownScreenPoint;

        public void Update(string webSocketDebuggerUrl, int backendNodeId, (int X, int Y) screenPoint)
        {
            lock (_gate)
            {
                _webSocketDebuggerUrl = webSocketDebuggerUrl;
                _backendNodeId = backendNodeId;
                _lastKnownScreenPoint = screenPoint;
            }
        }

        public bool TryGet(out string webSocketDebuggerUrl, out int backendNodeId)
        {
            lock (_gate)
            {
                if (_webSocketDebuggerUrl is not null && _backendNodeId is not null)
                {
                    webSocketDebuggerUrl = _webSocketDebuggerUrl;
                    backendNodeId = _backendNodeId.Value;
                    return true;
                }

                webSocketDebuggerUrl = string.Empty;
                backendNodeId = 0;
                return false;
            }
        }

        /// <summary>
        /// The screen-space point (center of the last successfully-correlated UIA bounding rect)
        /// at the time of the last successful correlation. Used to re-acquire a fresh
        /// <see cref="System.Windows.Automation.AutomationElement"/> via
        /// <see cref="System.Windows.Automation.AutomationElement.FromPoint"/> after a DevTools
        /// write makes a previously-hidden element visible again — Chromium creates a brand-new
        /// accessibility node in that case, so the original element reference can never be reused.
        /// </summary>
        public bool TryGetLastKnownScreenPoint(out int x, out int y)
        {
            lock (_gate)
            {
                if (_lastKnownScreenPoint is { } point)
                {
                    x = point.X;
                    y = point.Y;
                    return true;
                }

                x = 0;
                y = 0;
                return false;
            }
        }
    }
}
