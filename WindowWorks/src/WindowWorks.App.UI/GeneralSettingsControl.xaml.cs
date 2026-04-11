using System.Windows.Controls;

namespace WindowWorks.App.UI
{
    public partial class GeneralSettingsControl : UserControl
    {
        public GeneralSettingsControl()
        {
            InitializeComponent();
        }

        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> d)
        {
            if (d == null) return;
            if (d.TryGetValue("EnableHighlight", out var v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.True) ChkEnableHighlight.IsChecked = true;
                else if (v.ValueKind == System.Text.Json.JsonValueKind.False) ChkEnableHighlight.IsChecked = false;
                else if (v.ValueKind == System.Text.Json.JsonValueKind.String) { if (bool.TryParse(v.GetString(), out var bv)) ChkEnableHighlight.IsChecked = bv; }
            }
            if (d.TryGetValue("EnableConfirmations", out v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.True) ChkEnableConfirmations.IsChecked = true;
                else if (v.ValueKind == System.Text.Json.JsonValueKind.False) ChkEnableConfirmations.IsChecked = false;
                else if (v.ValueKind == System.Text.Json.JsonValueKind.String) { if (bool.TryParse(v.GetString(), out var bv2)) ChkEnableConfirmations.IsChecked = bv2; }
            }
        }
    }
}
