using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// A process-independent DOM identity for the relaunch handoff. Backend node ids are deliberately
    /// excluded because a newly launched page has a new DOM document and new backend ids.
    /// </summary>
    internal sealed class CdpNodeFingerprint
    {
        private static readonly HashSet<string> GenericClassTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "active", "disabled", "enabled", "hidden", "selected", "visible"
        };

        private CdpNodeFingerprint(string tagName, IReadOnlyDictionary<string, string> attributes)
        {
            TagName = tagName;
            Attributes = attributes;
        }

        public string TagName { get; }
        public IReadOnlyDictionary<string, string> Attributes { get; }

        public static CdpNodeFingerprint? TryCreate(
            CdpDomCorrelationResult correlation,
            IReadOnlyList<(string Name, string Value)> attributes)
        {
            if (!correlation.Success
                || correlation.Confidence < 0.8
                || string.IsNullOrWhiteSpace(correlation.TagName))
            {
                return null;
            }

            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in attributes)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    snapshot[name] = value;
                }
            }

            return HasStrongIdentity(correlation.TagName, snapshot)
                ? new CdpNodeFingerprint(correlation.TagName, snapshot)
                : null;
        }

        public bool Matches(string? tagName, IReadOnlyDictionary<string, string> attributes)
        {
            if (!string.Equals(TagName, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var (name, value) in Attributes)
            {
                if (!attributes.TryGetValue(name, out string? candidateValue)
                    || !string.Equals(value, candidateValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public string BuildSearchQuery()
        {
            return Attributes.TryGetValue("id", out string? id) && !string.IsNullOrWhiteSpace(id)
                ? $"{TagName}[id=\"{EscapeCssString(id)}\"]"
                : TagName;
        }

        private static bool HasStrongIdentity(string tagName, IReadOnlyDictionary<string, string> attributes)
        {
            if (attributes.TryGetValue("id", out string? id) && !string.IsNullOrWhiteSpace(id))
            {
                return true;
            }

            int signals = 0;
            if (attributes.TryGetValue("class", out string? classValue)
                && classValue.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => !GenericClassTokens.Contains(token)))
            {
                signals++;
            }

            foreach (string name in new[] { "name", "aria-label", "role", "href", "src", "title", "alt", "type" })
            {
                if (attributes.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    signals++;
                }
            }

            signals += attributes.Count(pair =>
                pair.Key.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value));

            return !string.IsNullOrWhiteSpace(tagName) && signals >= 2;
        }

        private static string EscapeCssString(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\D ", StringComparison.Ordinal)
                .Replace("\n", "\\A ", StringComparison.Ordinal);
        }
    }
}
