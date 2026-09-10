using System.Windows.Controls;

namespace WindowWorks.App.UI
{
    public partial class WindowReparentingSettingsControl : UserControl
    {
        public WindowReparentingSettingsControl()
        {
            InitializeComponent();
        }

        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> d)
        {
            if (d == null) return;
            if (d.TryGetValue("EnableWindowReparenting", out var ev))
            {
                if (ev.ValueKind == System.Text.Json.JsonValueKind.True) ChkEnableWindowReparenting.IsChecked = true;
                else if (ev.ValueKind == System.Text.Json.JsonValueKind.False) ChkEnableWindowReparenting.IsChecked = false;
                else if (ev.ValueKind == System.Text.Json.JsonValueKind.String) { if (bool.TryParse(ev.GetString(), out var bv)) ChkEnableWindowReparenting.IsChecked = bv; }
            }
            if (d.TryGetValue("EnablePopOutAndReparent", out var pv))
            {
                if (pv.ValueKind == System.Text.Json.JsonValueKind.True) ChkEnablePopOutAndReparent.IsChecked = true;
                else if (pv.ValueKind == System.Text.Json.JsonValueKind.False) ChkEnablePopOutAndReparent.IsChecked = false;
                else if (pv.ValueKind == System.Text.Json.JsonValueKind.String) { if (bool.TryParse(pv.GetString(), out var bv)) ChkEnablePopOutAndReparent.IsChecked = bv; }
            }
            if (d.TryGetValue("AllowResizingReparentedChildElements", out var v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.True) ChkAllowResizingReparentedChildElements.IsChecked = true;
                else if (v.ValueKind == System.Text.Json.JsonValueKind.False) ChkAllowResizingReparentedChildElements.IsChecked = false;
                else if (v.ValueKind == System.Text.Json.JsonValueKind.String) { if (bool.TryParse(v.GetString(), out var bv)) ChkAllowResizingReparentedChildElements.IsChecked = bv; }
            }
        }
    }
}