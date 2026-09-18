using System.Text.Json.Serialization;

namespace WindowWorks.App.Cdp
{
    /// <summary>
    /// One entry from a Chromium DevTools <c>/json/list</c> (or <c>/json</c>) response — a
    /// debuggable target (typically a browser tab/page). Property names match the JSON keys
    /// verbatim via <see cref="JsonPropertyNameAttribute"/> since the CDP HTTP endpoint uses
    /// lowercase-first field names, not .NET PascalCase conventions.
    /// docs/PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 1.
    /// </summary>
    internal sealed class CdpTarget
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("webSocketDebuggerUrl")]
        public string? WebSocketDebuggerUrl { get; set; }
    }
}
