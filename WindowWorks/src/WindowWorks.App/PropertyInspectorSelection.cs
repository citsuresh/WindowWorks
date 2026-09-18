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
        public AutomationElement SelectedElement { get; }
        public string SelectedElementRuntimeId { get; }
        internal ReparentEngine.CapturedWindowIdentity RootWindowIdentity { get; }

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
