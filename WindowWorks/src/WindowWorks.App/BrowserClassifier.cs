namespace WindowWorks.App
{
    /// <summary>
    /// Detects whether a top-level window belongs to a Chromium-family browser
    /// (docs/REPARENT_FEATURE_PLAN.md §Phase 6, item 1). Chrome, Edge, Brave, Vivaldi, Opera,
    /// and WebView2 hosts all report the same top-level window class name,
    /// <c>Chrome_WidgetWin_1</c> — confirmed live against running Brave and Edge windows during
    /// the Phase 6 planning spike. Firefox (<c>MozillaWindowClass</c>) is a different rendering/
    /// accessibility backend and is explicitly out of scope for now (see plan doc).
    ///
    /// Deliberately a small, extensible allowlist (not a single hardcoded string check inline)
    /// so a future browser family can be added here without touching call sites.
    /// </summary>
    public static class BrowserClassifier
    {
        private static readonly string[] ChromiumFamilyClassNames =
        {
            "Chrome_WidgetWin_1",
        };

        /// <summary>
        /// True if <paramref name="className"/> matches a known Chromium-family top-level window
        /// class. Case-sensitive exact match — these class names are fixed, well-known Win32
        /// window class identifiers, not user-facing text.
        /// </summary>
        public static bool IsChromiumFamily(string? className)
        {
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }

            foreach (var candidate in ChromiumFamilyClassNames)
            {
                if (className == candidate)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
