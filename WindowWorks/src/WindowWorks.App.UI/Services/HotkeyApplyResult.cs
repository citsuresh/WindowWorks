using System.Collections.Generic;

namespace WindowWorks.App.UI.Services
{
    // Result of attempting to apply hotkey changes. Contains per-key success/failure messages.
    public class HotkeyApplyResult
    {
        public Dictionary<string, bool> Success { get; } = new Dictionary<string, bool>();
        public Dictionary<string, string?> ErrorMessage { get; } = new Dictionary<string, string?>();

        public void SetSuccess(string keyName, bool ok, string? message = null)
        {
            Success[keyName] = ok;
            ErrorMessage[keyName] = message;
        }
    }
}
