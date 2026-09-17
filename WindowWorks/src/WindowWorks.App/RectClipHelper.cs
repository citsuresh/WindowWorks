using System;

namespace WindowWorks.App
{
    /// <summary>
    /// Screen-pixel rectangle intersection/clipping helper (docs/REPARENT_FEATURE_PLAN.md §Phase
    /// 6, item 3). UI Automation DOM element <c>BoundingRectangle</c> values were observed during
    /// the Phase 6 planning spike to extend outside a Chromium window's actual client bounds near
    /// corners/edges (e.g. a top-level content group reporting a negative X origin) — this is a
    /// UIA/Chromium quirk, not a real usable-content boundary. Any DOM-sourced rect must be
    /// clipped against the browser window's real client rect before being used for highlighting
    /// or crop-and-reparent, so the picker never offers (and crop never targets) pixels outside
    /// the window that's actually being reparented.
    /// </summary>
    public static class RectClipHelper
    {
        /// <summary>
        /// Intersects <paramref name="candidate"/> (screen pixels) against
        /// <paramref name="bounds"/> (screen pixels, typically the browser window's client rect).
        /// Returns false if the two rects do not overlap at all (nothing usable remains), true
        /// otherwise with <paramref name="clipped"/> set to the intersection.
        /// </summary>
        public static bool TryClipToWindowBounds(
            (int Left, int Top, int Right, int Bottom) candidate,
            (int Left, int Top, int Right, int Bottom) bounds,
            out (int Left, int Top, int Right, int Bottom) clipped)
        {
            int left = Math.Max(candidate.Left, bounds.Left);
            int top = Math.Max(candidate.Top, bounds.Top);
            int right = Math.Min(candidate.Right, bounds.Right);
            int bottom = Math.Min(candidate.Bottom, bounds.Bottom);

            if (right <= left || bottom <= top)
            {
                clipped = default;
                return false;
            }

            clipped = (left, top, right, bottom);
            return true;
        }
    }
}
