using System.Collections.Generic;
using System.Text.Json;

namespace WindowWorks.App.UI.ViewModels
{
    // Common interface for all settings section viewmodels so SettingsWindow can enumerate them generically.
    public interface ISettingsSectionViewModel
    {
        void LoadFromDictionary(System.Collections.Generic.Dictionary<string, JsonElement>? d);
        System.Collections.Generic.Dictionary<string, object?> ToDictionary();
    }
}
