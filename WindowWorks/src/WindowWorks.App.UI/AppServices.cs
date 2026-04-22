using System;

namespace WindowWorks.App.UI
{
    // Lightweight static holder for the IServiceProvider until full DI lifetime is setup in WPF startup.
    public static class AppServices
    {
        public static IServiceProvider? Provider { get; set; }

        public static T? GetService<T>() where T : class
        {
            return Provider?.GetService(typeof(T)) as T;
        }
        // In-process hotkey apply service for UI to notify host of changes.
        // Provide a noop default so callers do not need to null-check.
        private static WindowWorks.App.UI.Services.IHotkeyApplyService s_hotkeyApplyService = new NoopHotkeyApplyService();

        public static WindowWorks.App.UI.Services.IHotkeyApplyService HotkeyApplyService
        {
            get => s_hotkeyApplyService;
            set => s_hotkeyApplyService = value ?? new NoopHotkeyApplyService();
        }

        private sealed class NoopHotkeyApplyService : WindowWorks.App.UI.Services.IHotkeyApplyService
        {
            public WindowWorks.App.UI.Services.HotkeyApplyResult ApplyHotkeys(string? commandPalette, string? emergencyReset, string? transparencyIncrease, string? transparencyDecrease, string? toggleTopmost)
            {
                // Return an empty result indicating no failures (no-op host)
                return new WindowWorks.App.UI.Services.HotkeyApplyResult();
            }
        }
    }
}
