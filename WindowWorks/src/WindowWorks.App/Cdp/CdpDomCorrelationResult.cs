using System.Text.Json.Nodes;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// Result of attempting to correlate a UIA-picked DOM element to a CDP DOM node
    /// (docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 2). This is explicitly a
    /// heuristic match, not a guaranteed 1:1 identity — <see cref="Confidence"/> and the
    /// diagnostic fields below exist so real-world reliability can be judged and reported, not
    /// just a bare pass/fail.
    /// </summary>
    internal sealed class CdpDomCorrelationResult
    {
        public bool Success { get; }

        /// <summary>CDP backend node id of the matched node, valid only while the current DOM
        /// document generation is unchanged (per CDP semantics) — treat as short-lived.</summary>
        public int BackendNodeId { get; }

        public string? TagName { get; }
        public string? NodeId { get; }
        public string? ClassAttribute { get; }
        public string? IdAttribute { get; }

        /// <summary>
        /// 0.0-1.0 heuristic confidence: 1.0 = box model overlaps essentially exactly with the
        /// UIA rect (within a small pixel tolerance) and, if available, tag/attribute signals do
        /// not contradict; lower values indicate a distance-based fallback match or a
        /// low-overlap result the caller should treat with more suspicion.
        /// </summary>
        public double Confidence { get; }

        public string Diagnostics { get; }

        private CdpDomCorrelationResult(
            bool success,
            int backendNodeId,
            string? tagName,
            string? nodeId,
            string? classAttribute,
            string? idAttribute,
            double confidence,
            string diagnostics)
        {
            Success = success;
            BackendNodeId = backendNodeId;
            TagName = tagName;
            NodeId = nodeId;
            ClassAttribute = classAttribute;
            IdAttribute = idAttribute;
            Confidence = confidence;
            Diagnostics = diagnostics;
        }

        public static CdpDomCorrelationResult Failed(string diagnostics) =>
            new(false, 0, null, null, null, null, 0.0, diagnostics);

        public static CdpDomCorrelationResult Matched(
            int backendNodeId,
            string? tagName,
            string? nodeId,
            string? classAttribute,
            string? idAttribute,
            double confidence,
            string diagnostics) =>
            new(true, backendNodeId, tagName, nodeId, classAttribute, idAttribute, confidence, diagnostics);
    }
}
