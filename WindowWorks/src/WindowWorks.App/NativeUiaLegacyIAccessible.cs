using System;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowWorks.App
{
    /// <summary>
    /// Thin wrapper over the official Microsoft-generated <c>Interop.UIAutomationClient</c> COM interop
    /// (NuGet package "Interop.UIAutomationClient") to reach <see cref="IUIAutomationLegacyIAccessiblePattern"/>.
    /// The managed System.Windows.Automation API (UIAutomationClient.dll referenced via
    /// UseWPF/System.Windows.Automation) does not expose this MSAA/IAccessible bridge pattern at all, so
    /// native UIA3 COM is used instead - via the official interop assembly rather than hand-written COM
    /// interfaces, to avoid the risk of an incorrect hand-authored vtable layout.
    /// Every lookup re-resolves the element from its RuntimeId at call time - nothing is cached across
    /// calls, consistent with the identity-guarded pattern used elsewhere in PropertyInspectorController.
    /// </summary>
    internal static class NativeUiaLegacyIAccessible
    {
        internal readonly struct LegacyState
        {
            public LegacyState(string value, string role, string defaultAction)
            {
                Value = value;
                Role = role;
                DefaultAction = defaultAction;
            }

            public string Value { get; }
            public string Role { get; }
            public string DefaultAction { get; }
        }

        internal static bool TryGetState(int[]? runtimeId, IntPtr containingWindowHandle, out LegacyState state)
        {
            state = default;
            if (!TryGetLegacyPattern(runtimeId, containingWindowHandle, out var legacyPattern, out var release))
            {
                return false;
            }

            try
            {
                string value = legacyPattern.CurrentValue ?? string.Empty;
                uint role = (uint)legacyPattern.CurrentRole;
                string defaultAction = legacyPattern.CurrentDefaultAction ?? string.Empty;
                state = new LegacyState(value, role.ToString(), defaultAction);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                release();
            }
        }

        internal static bool TrySetValue(int[]? runtimeId, IntPtr containingWindowHandle, string value)
        {
            if (!TryGetLegacyPattern(runtimeId, containingWindowHandle, out var legacyPattern, out var release))
            {
                return false;
            }

            try
            {
                legacyPattern.SetValue(value);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                release();
            }
        }

        internal static bool TryDoDefaultAction(int[]? runtimeId, IntPtr containingWindowHandle)
        {
            if (!TryGetLegacyPattern(runtimeId, containingWindowHandle, out var legacyPattern, out var release))
            {
                return false;
            }

            try
            {
                legacyPattern.DoDefaultAction();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                release();
            }
        }

        internal static bool HasPattern(int[]? runtimeId, IntPtr containingWindowHandle)
        {
            bool found = TryGetLegacyPattern(runtimeId, containingWindowHandle, out _, out var release);
            release();
            return found;
        }

        private static bool TryGetLegacyPattern(
            int[]? runtimeId,
            IntPtr containingWindowHandle,
            out IUIAutomationLegacyIAccessiblePattern legacyPattern,
            out Action release)
        {
            legacyPattern = null!;
            release = static () => { };

            if (runtimeId is null || runtimeId.Length == 0)
            {
                return false;
            }

            CUIAutomation? automation = null;
            IUIAutomationElement? rootElement = null;
            IUIAutomationElement? matchedElement = null;
            try
            {
                automation = new CUIAutomation();

                // Scope the search to the containing top-level window instead of the entire desktop
                // subtree: walking every window's full UIA tree on every property read/write is
                // prohibitively slow (multi-second hangs) against complex UIs such as browser DOM trees.
                rootElement = containingWindowHandle != IntPtr.Zero
                    ? automation.ElementFromHandle(containingWindowHandle)
                    : automation.GetRootElement();
                if (rootElement is null)
                {
                    return false;
                }

                // Fast path: the containing window itself might be the target element.
                var rootRuntimeId = (int[])rootElement.GetRuntimeId();
                if (RuntimeIdEquals(rootRuntimeId, runtimeId))
                {
                    matchedElement = rootElement;
                }
                else
                {
                    matchedElement = FindByRuntimeId(automation, rootElement, runtimeId);
                }

                if (matchedElement is null)
                {
                    return false;
                }

                object? patternObject = matchedElement.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId);
                if (patternObject is not IUIAutomationLegacyIAccessiblePattern resolvedPattern)
                {
                    return false;
                }

                legacyPattern = resolvedPattern;
                var elementToRelease = matchedElement;
                var rootToRelease = ReferenceEquals(matchedElement, rootElement) ? null : rootElement;
                var automationToRelease = automation;
                release = () =>
                {
                    ReleaseComObject(elementToRelease);
                    if (rootToRelease is not null)
                    {
                        ReleaseComObject(rootToRelease);
                    }

                    ReleaseComObject(automationToRelease);
                };
                return true;
            }
            catch
            {
                if (!ReferenceEquals(matchedElement, rootElement))
                {
                    ReleaseComObject(matchedElement);
                }

                ReleaseComObject(rootElement);
                ReleaseComObject(automation);
                return false;
            }
        }

        private static IUIAutomationElement? FindByRuntimeId(CUIAutomation automation, IUIAutomationElement searchRoot, int[] runtimeId)
        {
            IUIAutomationCondition? trueCondition = null;
            IUIAutomationElementArray? found = null;
            IUIAutomationElement? matchedElement = null;
            try
            {
                trueCondition = automation.CreateTrueCondition();
                found = searchRoot.FindAll(TreeScope.TreeScope_Subtree, trueCondition);
                if (found is null)
                {
                    return null;
                }

                for (int i = 0; i < found.Length; i++)
                {
                    var candidate = found.GetElement(i);
                    if (candidate is null)
                    {
                        continue;
                    }

                    var candidateRuntimeId = (int[])candidate.GetRuntimeId();
                    if (RuntimeIdEquals(candidateRuntimeId, runtimeId))
                    {
                        matchedElement = candidate;
                        continue;
                    }

                    ReleaseComObject(candidate);
                }

                return matchedElement;
            }
            finally
            {
                ReleaseComObject(found);
                ReleaseComObject(trueCondition);
            }
        }

        private static bool RuntimeIdEquals(int[] a, int[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void ReleaseComObject(object? comObject)
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
    }
}
