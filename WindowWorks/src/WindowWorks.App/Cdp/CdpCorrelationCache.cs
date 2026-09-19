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
        private (int Left, int Top, int Right, int Bottom)? _lastKnownScreenRect;

        public void Update(string webSocketDebuggerUrl, int backendNodeId, (int Left, int Top, int Right, int Bottom) screenRect)
        {
            lock (_gate)
            {
                _webSocketDebuggerUrl = webSocketDebuggerUrl;
                _backendNodeId = backendNodeId;
                _lastKnownScreenRect = screenRect;
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
        /// The screen-space bounding rect of the last successfully-correlated UIA element, at the
        /// time of the last successful correlation. Used to re-acquire a fresh
        /// <see cref="System.Windows.Automation.AutomationElement"/> via
        /// <see cref="System.Windows.Automation.AutomationElement.FromPoint"/> (hit-testing its
        /// center) after a DevTools write makes a previously-hidden element visible again —
        /// Chromium creates a brand-new accessibility node in that case, so the original element
        /// reference can never be reused. The full rect (not just a point) is kept so the caller
        /// can verify the rebound element's own rect actually overlaps this one before accepting
        /// it as the same element — a plain "whatever is at this point now" hit-test is not
        /// reliable on its own since the page may have scrolled/reflowed since the rect was
        /// captured.
        /// </summary>
        public bool TryGetLastKnownScreenRect(out (int Left, int Top, int Right, int Bottom) rect)
        {
            lock (_gate)
            {
                if (_lastKnownScreenRect is { } value)
                {
                    rect = value;
                    return true;
                }

                rect = default;
                return false;
            }
        }
    }
}
