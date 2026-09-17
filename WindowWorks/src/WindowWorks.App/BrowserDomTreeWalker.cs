using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowWorks.App
{
    /// <summary>
    /// One DOM element entry discovered by <see cref="BrowserDomTreeWalker"/> (docs/
    /// REPARENT_FEATURE_PLAN.md §Phase 6, item 2), nearest (deepest hovered element) first —
    /// mirrors <see cref="AncestorChainEntry"/>'s shape so the existing box-list/highlight UX
    /// (§6.3/§6.4) can drive either source uniformly.
    ///
    /// DOM elements always report <c>NativeWindowHandle == 0</c> (confirmed during the Phase 6
    /// spike) — there is no HWND to reparent per-element. <see cref="BrowserHwnd"/> is the real,
    /// whole top-level browser window that any eventual crop-and-reparent must target, with
    /// <see cref="ClippedScreenRect"/> (already intersected against the browser's client rect via
    /// <see cref="RectClipHelper"/>) supplying the crop region.
    /// </summary>
    public sealed class DomElementEntry
    {
        public IntPtr BrowserHwnd { get; }
        public string ControlTypeName { get; }
        public string Name { get; }
        public (int Left, int Top, int Right, int Bottom) ClippedScreenRect { get; }
        public bool IsDocumentRoot { get; }

        public DomElementEntry(
            IntPtr browserHwnd,
            string controlTypeName,
            string name,
            (int Left, int Top, int Right, int Bottom) clippedScreenRect,
            bool isDocumentRoot)
        {
            BrowserHwnd = browserHwnd;
            ControlTypeName = controlTypeName;
            Name = name;
            ClippedScreenRect = clippedScreenRect;
            IsDocumentRoot = isDocumentRoot;
        }
    }

    /// <summary>
    /// UI Automation DOM-tree walker for Chromium-family browser windows (docs/
    /// REPARENT_FEATURE_PLAN.md §Phase 6, item 2). Validated during the Phase 6 planning spike
    /// (live against Brave/Edge, no CDP/--remote-debugging-port needed):
    /// <c>AutomationElement.FromPoint</c> resolves the deepest DOM element under the cursor, then
    /// walking up parents via <see cref="TreeWalker.ControlViewWalker"/> collects the ancestor
    /// chain up to (and including) the page's <c>Document</c>-type root element. Mirrors the
    /// shape/ordering of <see cref="AncestorChainWalker.Discover"/> (nearest/deepest first) so it
    /// can be swapped in for the native ancestor-chain walk when the hovered top-level window is
    /// a browser (<see cref="BrowserClassifier"/>).
    /// </summary>
    public static class BrowserDomTreeWalker
    {
        private const int MaxLevels = 12;

        /// <summary>
        /// Discovers the DOM ancestor chain under the given screen point, up to the page's
        /// Document root. Returns an empty list if the point isn't over a recognizable DOM
        /// element (e.g. browser chrome/tab strip rather than page content) or UIA calls fail.
        /// </summary>
        public static List<DomElementEntry> Discover(IntPtr browserHwnd, int screenX, int screenY)
        {
            var result = new List<DomElementEntry>();

            if (!TryGetBrowserClientScreenRect(browserHwnd, out var browserClientScreenRect))
            {
                return result;
            }

            AutomationElement? element;
            try
            {
                element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
            }
            catch
            {
                return result;
            }

            if (element is null)
            {
                return result;
            }

            var walker = TreeWalker.ControlViewWalker;
            var seen = new HashSet<AutomationElement>();
            AutomationElement? current = element;
            int guard = 0;

            while (current is not null && guard++ < MaxLevels)
            {
                if (!seen.Add(current))
                {
                    break;
                }

                if (TryBuildEntry(current, browserHwnd, browserClientScreenRect, out var entry))
                {
                    result.Add(entry);
                    if (entry.IsDocumentRoot)
                    {
                        break;
                    }
                }

                AutomationElement? parent;
                try
                {
                    parent = walker.GetParent(current);
                }
                catch
                {
                    break;
                }

                current = parent;
            }

            return result;
        }

        private static bool TryBuildEntry(
            AutomationElement element,
            IntPtr browserHwnd,
            (int Left, int Top, int Right, int Bottom) browserClientScreenRect,
            out DomElementEntry entry)
        {
            entry = null!;
            try
            {
                var current = element.Current;
                if (!TryGetClippedScreenRect(current, browserClientScreenRect, out var clipped))
                {
                    return false;
                }

                bool isDocumentRoot = current.ControlType == ControlType.Document;

                entry = new DomElementEntry(
                    browserHwnd,
                    current.ControlType?.ProgrammaticName ?? string.Empty,
                    ResolveDisplayName(current, clipped),
                    clipped,
                    isDocumentRoot);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the browser window's client rect in screen pixels (shared by
        /// <see cref="Discover"/> and <see cref="DomElementTreeBuilder"/>, docs/
        /// REPARENT_FEATURE_PLAN.md §6.7 Piece B, so both entry points into the DOM picker use
        /// the exact same clip bounds).
        /// </summary>
        public static bool TryGetBrowserClientScreenRect(
            IntPtr browserHwnd,
            out (int Left, int Top, int Right, int Bottom) browserClientScreenRect)
        {
            browserClientScreenRect = default;
            if (!NativeMethods.GetClientRect(browserHwnd, out var clientRect))
            {
                return false;
            }
            var topLeft = new NativeMethods.POINT { X = 0, Y = 0 };
            NativeMethods.ClientToScreen(browserHwnd, ref topLeft);
            browserClientScreenRect = (
                Left: topLeft.X,
                Top: topLeft.Y,
                Right: topLeft.X + (clientRect.Right - clientRect.Left),
                Bottom: topLeft.Y + (clientRect.Bottom - clientRect.Top));
            return true;
        }

        /// <summary>
        /// Walks up from the deepest DOM element under the given screen point to the page's
        /// <c>Document</c>-type root, same walk as <see cref="Discover"/> but returning only the
        /// resolved root element itself (docs/REPARENT_FEATURE_PLAN.md §6.7, Piece B) — used by
        /// <see cref="DomElementTreeBuilder"/> so the tree view's root is defined identically to
        /// where the hover-based box list's chain (§6.6) stops. Returns <c>null</c> if the point
        /// isn't over a recognizable DOM element, if no Document-type ancestor is found within
        /// <see cref="MaxLevels"/>, or if UIA calls fail.
        /// </summary>
        public static AutomationElement? TryFindDocumentRoot(IntPtr browserHwnd, int screenX, int screenY)
        {
            AutomationElement? element;
            try
            {
                element = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
            }
            catch
            {
                return null;
            }

            if (element is null)
            {
                return null;
            }

            var walker = TreeWalker.ControlViewWalker;
            var seen = new HashSet<AutomationElement>();
            AutomationElement? current = element;
            int guard = 0;

            while (current is not null && guard++ < MaxLevels)
            {
                if (!seen.Add(current))
                {
                    return null;
                }

                try
                {
                    if (current.Current.ControlType == ControlType.Document)
                    {
                        return current;
                    }
                }
                catch
                {
                    return null;
                }

                try
                {
                    current = walker.GetParent(current);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Clips an already-fetched <see cref="AutomationElement.AutomationElementInformation"/>'s
        /// bounding rect against the browser's client rect, shared by <see cref="TryBuildEntry"/>
        /// and <see cref="DomElementTreeBuilder"/>. Returns false if the element has no usable
        /// bounds or the clipped result is empty.
        /// </summary>
        public static bool TryGetClippedScreenRect(
            AutomationElement.AutomationElementInformation current,
            (int Left, int Top, int Right, int Bottom) browserClientScreenRect,
            out (int Left, int Top, int Right, int Bottom) clipped)
        {
            clipped = default;
            var rect = current.BoundingRectangle;
            if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height))
            {
                return false;
            }

            var candidate = (
                Left: (int)Math.Round(rect.Left),
                Top: (int)Math.Round(rect.Top),
                Right: (int)Math.Round(rect.Right),
                Bottom: (int)Math.Round(rect.Bottom));

            return RectClipHelper.TryClipToWindowBounds(candidate, browserClientScreenRect, out clipped);
        }

        /// <summary>
        /// Public wrapper around <see cref="ResolveDisplayName"/> for callers outside this class
        /// (<see cref="DomElementTreeBuilder"/>, docs/REPARENT_FEATURE_PLAN.md §6.7 Piece B) that
        /// don't have a pre-clipped rect handy — falls back to the element's raw (unclipped) size
        /// for the size-hint tail case, which only affects the display label, never the actual
        /// crop/highlight rect.
        /// </summary>
        public static string ResolveDisplayNamePublic(AutomationElement.AutomationElementInformation current)
        {
            var rect = current.BoundingRectangle;
            (int Left, int Top, int Right, int Bottom) sizeHintRect = default;
            if (!rect.IsEmpty && !double.IsInfinity(rect.Width) && !double.IsInfinity(rect.Height))
            {
                sizeHintRect = (0, 0, (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
            }
            return ResolveDisplayName(current, sizeHintRect);
        }

        /// <summary>
        /// Resolves a human-readable display label for a DOM element, since UI Automation's
        /// <c>Name</c> is frequently empty for generic layout wrapper elements (Chromium reports
        /// these as an unnamed <c>ControlType.Group</c>) — confirmed during manual testing where
        /// several real page elements showed up in the picker as bare "ControlType.Group" with no
        /// other identifying text. Falls through several UIA properties Chromium is known to
        /// still populate even when <c>Name</c> is empty, roughly in order of how identifying
        /// they tend to be, before giving up and falling back to a size hint so at least visually
        /// distinct elements aren't fully indistinguishable in the pick list:
        /// <c>Name</c> -&gt; <c>ClassName</c> (Chromium reports the underlying HTML tag name here,
        /// e.g. "div"/"ytd-thumbnail") -&gt; <c>AutomationId</c> (often mirrors an HTML "id"
        /// attribute) -&gt; <c>HelpText</c> -&gt; control type + bounding-rect size.
        /// </summary>
        private static string ResolveDisplayName(
            AutomationElement.AutomationElementInformation current,
            (int Left, int Top, int Right, int Bottom) clippedRect)
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
            {
                return current.Name;
            }

            try
            {
                string? className = current.ClassName;
                if (!string.IsNullOrWhiteSpace(className))
                {
                    return className;
                }
            }
            catch { }

            try
            {
                string? automationId = current.AutomationId;
                if (!string.IsNullOrWhiteSpace(automationId))
                {
                    return automationId;
                }
            }
            catch { }

            try
            {
                string? helpText = current.HelpText;
                if (!string.IsNullOrWhiteSpace(helpText))
                {
                    return helpText;
                }
            }
            catch { }

            int width = clippedRect.Right - clippedRect.Left;
            int height = clippedRect.Bottom - clippedRect.Top;
            return $"{width}\u00D7{height}";
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        }
    }
}
