using System;
using System.Globalization;
using System.Linq;
using System.Windows.Automation;

namespace WindowWorks.App
{
    /// <summary>
    /// Represents either a native HWND picker confirmation or an exact UIA element selected from
    /// the native element tree. The root window entry supplies the native identity anchor.
    /// </summary>
    public sealed class PropertyInspectorSelection
    {
        private PropertyInspectorSelection(
            AncestorChainEntry rootWindowEntry,
            AutomationElement selectedElement,
            string selectedElementRuntimeId,
            ReparentEngine.CapturedWindowIdentity rootWindowIdentity)
        {
            RootWindowEntry = rootWindowEntry ?? throw new ArgumentNullException(nameof(rootWindowEntry));
            SelectedElement = selectedElement;
            SelectedElementRuntimeId = selectedElementRuntimeId;
            RootWindowIdentity = rootWindowIdentity;
        }

        public AncestorChainEntry RootWindowEntry { get; }
        public AutomationElement SelectedElement { get; private set; }
        public string SelectedElementRuntimeId { get; private set; }
        internal ReparentEngine.CapturedWindowIdentity RootWindowIdentity { get; }

        /// <summary>
        /// Caches the last successful CDP correlation for this selection (see
        /// <see cref="Cdp.CdpCorrelationCache"/>) so a DevTools write can fall back to it when the
        /// UIA-rect-based re-correlation path fails (e.g. after style.display:none removes the
        /// element from the accessibility tree). One instance per selection/pick, shared across
        /// every read/write against this element.
        /// </summary>
        internal Cdp.CdpCorrelationCache CdpCache { get; } = new();

        /// <summary>
        /// Attempts to replace <see cref="SelectedElement"/>/<see cref="SelectedElementRuntimeId"/>
        /// with a freshly re-acquired UIA element found at the given screen point (docs/
        /// PROPERTY_INSPECTOR_FEATURE_PLAN.md §4 Phase E, sub-phase 4 follow-up). Needed because
        /// Chromium creates a brand-new accessibility node when a previously style.display:none
        /// element becomes visible again — the original <see cref="AutomationElement"/> reference
        /// captured at pick time never becomes valid again even after the underlying DOM node is
        /// visible, so the UIA section would otherwise stay permanently absent after a
        /// hide-then-show round trip. Returns <c>true</c> only if a live element was actually
        /// found at that point; the current selection is left unchanged on failure.
        /// </summary>
        internal bool TryRebindSelectedElementAtPoint(int screenX, int screenY)
        {
            try
            {
                var element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
                string? runtimeId = FormatRuntimeId(element?.GetRuntimeId());
                if (element is null || string.IsNullOrWhiteSpace(runtimeId))
                {
                    return false;
                }

                SelectedElement = element;
                SelectedElementRuntimeId = runtimeId;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Captures all identity fields on the picker UI thread at confirmation time. A later
        /// property read must compare its live observations to this immutable snapshot.
        /// </summary>
        public static bool TryCapture(
            AncestorChainEntry rootWindowEntry,
            AutomationElement? selectedElement,
            out PropertyInspectorSelection? selection)
        {
            selection = null;
            if (rootWindowEntry is null
                || !ReparentEngine.TryGetWindowIdentity(
                    rootWindowEntry.Hwnd,
                    out uint processId,
                    out DateTime processStartTimeUtc,
                    out string? className,
                    out string? rootRuntimeId)
                || string.IsNullOrWhiteSpace(rootRuntimeId))
            {
                return false;
            }

            try
            {
                var element = selectedElement ?? AutomationElement.FromHandle(rootWindowEntry.Hwnd);
                string? selectedRuntimeId = FormatRuntimeId(element?.GetRuntimeId());
                if (element is null || string.IsNullOrWhiteSpace(selectedRuntimeId))
                {
                    return false;
                }

                if (selectedElement is null
                    && !string.Equals(selectedRuntimeId, rootRuntimeId, StringComparison.Ordinal))
                {
                    return false;
                }

                var rootIdentity = ReparentEngine.CaptureIdentity(
                    rootWindowEntry.Hwnd,
                    processId,
                    processStartTimeUtc,
                    className,
                    rootRuntimeId);
                selection = new PropertyInspectorSelection(
                    rootWindowEntry,
                    element,
                    selectedRuntimeId,
                    rootIdentity);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? FormatRuntimeId(int[]? runtimeIdParts)
        {
            return runtimeIdParts is null || runtimeIdParts.Length == 0
                ? null
                : string.Join(
                    ",",
                    runtimeIdParts.Select(static value => value.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
