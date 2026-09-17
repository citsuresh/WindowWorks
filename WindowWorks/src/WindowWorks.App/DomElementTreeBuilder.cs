using System;
using System.Collections.Generic;
using System.Windows.Automation;
using WindowWorks.App.UI;

namespace WindowWorks.App
{
    /// <summary>
    /// Builds the lazy-loaded <see cref="ElementTreeNodeItem"/> tree shown by
    /// <see cref="PickerElementTreeWindow"/> (docs/REPARENT_FEATURE_PLAN.md §6.7, Piece B) from a
    /// live UI Automation subtree, rooted at the same page <c>Document</c> element that
    /// <see cref="BrowserDomTreeWalker"/> already walks up to for the hover-based box list (§6.6)
    /// — kept as a single canonical "what counts as the page root" definition rather than
    /// duplicating that logic.
    ///
    /// Populates children lazily, one level at a time, only when a node is actually expanded in
    /// the tree (via the <c>childrenLoader</c> delegate captured per node) — never an eager
    /// up-front full-subtree walk, since real modern pages can have very large/deep DOM trees and
    /// eagerly walking the whole thing via UIA would be slow and wasted work for branches the
    /// user never expands. Mirrors how Inspect.exe and browser DevTools Elements panels
    /// themselves lazily expand.
    /// </summary>
    public static class DomElementTreeBuilder
    {
        /// <summary>
        /// Locates the page's <c>Document</c>-type root above the given screen point (reusing
        /// <see cref="BrowserDomTreeWalker"/>'s own walk-up-to-Document logic so both entry
        /// points into the DOM picker agree on what "the page root" means) and returns a single
        /// root <see cref="ElementTreeNodeItem"/> for it, or <c>null</c> if no Document root
        /// could be resolved under that point.
        /// </summary>
        public static ElementTreeNodeItem? TryBuildRoot(IntPtr browserHwnd, int screenX, int screenY)
        {
            AutomationElement? documentElement = BrowserDomTreeWalker.TryFindDocumentRoot(browserHwnd, screenX, screenY);
            if (documentElement is null)
            {
                return null;
            }

            if (!BrowserDomTreeWalker.TryGetBrowserClientScreenRect(browserHwnd, out var browserClientScreenRect))
            {
                return null;
            }

            return BuildNode(documentElement, browserHwnd, browserClientScreenRect);
        }

        /// <summary>
        /// Builds a root <see cref="ElementTreeNodeItem"/> for a native (non-browser) top-level
        /// window's own UI Automation subtree (docs/REPARENT_FEATURE_PLAN.md §6.7 Piece D), rooted
        /// at the window's own <see cref="AutomationElement"/> (via <c>FromHandle</c>) rather than
        /// walking up to a browser page's <c>Document</c> element. Reuses the exact same
        /// <see cref="BuildNode"/>/<see cref="LoadChildren"/> lazy-loading logic as the browser
        /// DOM path — <see cref="ElementTreeNodeItem"/> and the UIA calls involved are already
        /// control-type-agnostic; only the choice of root element and clip rect differ between the
        /// two entry points. Node rects are clipped against the window's own client rect (in place
        /// of a browser's client rect), so descendants that overflow the window bounds are clipped
        /// the same way the browser case already clips against the browser viewport.
        /// </summary>
        public static ElementTreeNodeItem? TryBuildRootForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            AutomationElement rootElement;
            try
            {
                rootElement = AutomationElement.FromHandle(hwnd);
            }
            catch
            {
                return null;
            }

            if (rootElement is null)
            {
                return null;
            }

            if (!BrowserDomTreeWalker.TryGetBrowserClientScreenRect(hwnd, out var windowClientScreenRect))
            {
                return null;
            }

            return BuildNode(rootElement, hwnd, windowClientScreenRect);
        }

        private static ElementTreeNodeItem BuildNode(
            AutomationElement element,
            IntPtr browserHwnd,
            (int Left, int Top, int Right, int Bottom) browserClientScreenRect)
        {
            var current = element.Current;
            string controlTypeName = current.ControlType?.ProgrammaticName ?? string.Empty;
            string label = BrowserDomTreeWalker.ResolveDisplayNamePublic(current);
            string fullLabel = string.IsNullOrWhiteSpace(label) ? controlTypeName : $"{label} ({controlTypeName})";

            (int Left, int Top, int Right, int Bottom)? screenRect = null;
            if (BrowserDomTreeWalker.TryGetClippedScreenRect(current, browserClientScreenRect, out var clipped))
            {
                screenRect = clipped;
            }

            bool hasChildren = TryHasAnyChild(element);

            return new ElementTreeNodeItem(
                fullLabel,
                screenRect,
                hasChildren,
                childrenLoader: hasChildren
                    ? node => LoadChildren(element, browserHwnd, browserClientScreenRect)
                    : null,
                tag: element,
                fullLabel: fullLabel);
        }

        private static IReadOnlyList<ElementTreeNodeItem> LoadChildren(
            AutomationElement parent,
            IntPtr browserHwnd,
            (int Left, int Top, int Right, int Bottom) browserClientScreenRect)
        {
            var result = new List<ElementTreeNodeItem>();
            var walker = TreeWalker.ControlViewWalker;

            AutomationElement? child;
            try
            {
                child = walker.GetFirstChild(parent);
            }
            catch
            {
                return result;
            }

            const int MaxChildrenPerLevel = 500;
            int guard = 0;
            while (child is not null && guard++ < MaxChildrenPerLevel)
            {
                try
                {
                    result.Add(BuildNode(child, browserHwnd, browserClientScreenRect));
                }
                catch
                {
                    // Best-effort: a single unreadable child (e.g. it went away mid-enumeration)
                    // should not abort the rest of the sibling list.
                }

                try
                {
                    child = walker.GetNextSibling(child);
                }
                catch
                {
                    break;
                }
            }

            return result;
        }

        private static bool TryHasAnyChild(AutomationElement element)
        {
            try
            {
                return TreeWalker.ControlViewWalker.GetFirstChild(element) is not null;
            }
            catch
            {
                return false;
            }
        }
    }
}
