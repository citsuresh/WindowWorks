using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WindowWorks.App.UI
{
    // SettingsWindow is UI-only and does not reference application models to avoid circular project refs.
    public partial class SettingsWindow : Window
    {
        private TaskCompletionSource<string?>? _tcs;
        private System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? _initialDict;
        // Ensure only one settings window instance is active at a time.
        private static SettingsWindow? s_activeWindow;
        private static TaskCompletionSource<string?>? s_activeTcs;
        private static readonly object s_lock = new();

        public SettingsWindow()
        {
            InitializeComponent();
            // Ensure Shortcuts nav item exists in case XAML was modified or running older binaries
            try
            {
                bool hasShortcuts = false;
                foreach (var it in NavList.Items)
                {
                    if (it is System.Windows.Controls.ListBoxItem lbi && (lbi.Content as string) == "Shortcuts") { hasShortcuts = true; break; }
                }
                if (!hasShortcuts)
                {
                    NavList.Items.Add(new System.Windows.Controls.ListBoxItem() { Content = "Shortcuts" });
                }
                if (NavList.SelectedIndex < 0) NavList.SelectedIndex = 0;
            }
            catch { }
        }

        public SettingsWindow(string currentJson, TaskCompletionSource<string?> tcs) : this()
        {
            _tcs = tcs;
            // Optionally initialize controls from provided JSON (lightweight parsing)
            try
            {
                if (!string.IsNullOrWhiteSpace(currentJson))
                {
                    _initialDict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(currentJson);
                    // Defer applying values until pages are shown; consumer may expand later
                }
            }
            catch { }
            // Now that initial dictionary is available, select first page to ensure values are applied.
            // NavList.SelectedIndex may already be 0 (set in parameterless ctor) and won't raise SelectionChanged again,
            // so force a reset to trigger loading with the initial dictionary.
            try { NavList.SelectedIndex = -1; NavList.SelectedIndex = 0; } catch { }
            try { RegisterActiveInstance(); } catch { }
        }

        private void RegisterActiveInstance()
        {
            // Called on the window's STA thread during creation
            lock (s_lock)
            {
                s_activeWindow = this;
            }
        }

        private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var item = NavList.SelectedItem as System.Windows.Controls.ListBoxItem;
            if (item == null) return;
            switch (item.Content as string)
            {
                case "General":
                    var g = new GeneralSettingsControl();
                    // If we have initial JSON, try to load it
                    if (_initialDict != null) g.LoadFromDictionary(_initialDict);
                    ContentArea.Content = g;
                    break;
                case "Highlight & HUD":
                    var h = new HighlightSettingsControl();
                    if (_initialDict != null) h.LoadFromDictionary(_initialDict);
                    ContentArea.Content = h;
                    break;
                case "Shortcuts":
                    var sControl = new ShortcutsSettingsControl();
                    if (_initialDict != null) sControl.LoadFromSettings(_initialDict);
                    sControl.HotkeysChanged += (ss, ee) => {
                        // when hotkeys edited, update settings preview in memory so Save will persist
                        try
                        {
                            var tb1 = sControl.FindName("TxtCommandPalette") as System.Windows.Controls.TextBox;
                            var tb2 = sControl.FindName("TxtEmergencyReset") as System.Windows.Controls.TextBox;
                            if (tb1 != null) _initialDict["HotkeyCommandPalette"] = System.Text.Json.JsonDocument.Parse("\"" + System.Text.Json.JsonEncodedText.Encode(tb1.Text).ToString() + "\"").RootElement;
                            if (tb2 != null) _initialDict["HotkeyEmergencyReset"] = System.Text.Json.JsonDocument.Parse("\"" + System.Text.Json.JsonEncodedText.Encode(tb2.Text).ToString() + "\"").RootElement;
                        }
                        catch { }
                    };
                    ContentArea.Content = sControl;
                    break;

                default:
                    ContentArea.Content = null;
                    break;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Collect settings from current content control(s).
            // For now, collect from known controls if present.
            try
            {
                // Build a lightweight dictionary and serialize to JSON for the host to consume
                var settingsDict = new Dictionary<string, object?>();
                // Attempt to populate fields from HighlightSettingsControl if visible
                if (ContentArea.Content is HighlightSettingsControl h)
                {
                    // Read values by finding named elements (lightweight approach)
                    var tbColor = h.FindName("TxtBorderColor") as System.Windows.Controls.TextBox;
                    var tbBorder = h.FindName("TxtBorderThickness") as System.Windows.Controls.TextBox;
                    var tbCorner = h.FindName("TxtCornerRadius") as System.Windows.Controls.TextBox;
                    var tbHighlight = h.FindName("TxtHighlightMs") as System.Windows.Controls.TextBox;
                    var tbHud = h.FindName("TxtHudMs") as System.Windows.Controls.TextBox;
                    var chkSys = h.FindName("ChkUseSystemColors") as System.Windows.Controls.CheckBox;
                    if (tbColor != null) settingsDict["HighlightBorderColor"] = tbColor.Text?.Trim();
                    if (tbBorder != null && int.TryParse(tbBorder.Text, out var bt)) settingsDict["HighlightBorderThickness"] = bt;
                    if (tbCorner != null && int.TryParse(tbCorner.Text, out var cr)) settingsDict["HighlightCornerRadius"] = cr;
                    if (tbHighlight != null && int.TryParse(tbHighlight.Text, out var hm)) settingsDict["HighlightDurationMs"] = hm;
                    if (tbHud != null && int.TryParse(tbHud.Text, out var um)) settingsDict["HudDurationMs"] = um;
                    if (chkSys != null) settingsDict["UseSystemColors"] = chkSys.IsChecked == true;
                }

                // For General page, try to read those as well
                if (ContentArea.Content is GeneralSettingsControl g)
                {
                    var chk1 = g.FindName("ChkEnableHighlight") as System.Windows.Controls.CheckBox;
                    var chk2 = g.FindName("ChkEnableConfirmations") as System.Windows.Controls.CheckBox;
                    if (chk1 != null) settingsDict["EnableHighlight"] = chk1.IsChecked == true;
                    if (chk2 != null) settingsDict["EnableConfirmations"] = chk2.IsChecked == true;
                }
                // For Shortcuts page, capture hotkey strings
                if (ContentArea.Content is ShortcutsSettingsControl s)
                {
                    var picker1 = s.FindName("PickerCommandPalette") as ShortcutPicker;
                    var picker2 = s.FindName("PickerEmergencyReset") as ShortcutPicker;
                    if (picker1 != null) settingsDict["HotkeyCommandPalette"] = picker1.Shortcut;
                    if (picker2 != null) settingsDict["HotkeyEmergencyReset"] = picker2.Shortcut;
                    // Opacity nudge and Toggle Topmost are mouse gestures and are not saved as keyboard shortcuts.
                }
                // Signal result as JSON and close
                var json = JsonSerializer.Serialize(settingsDict);
                _tcs?.TrySetResult(json);
            }
            catch
            {
                _tcs?.TrySetResult(null);
            }
            finally
            {
                try { UnregisterActiveInstance(); } catch { }
                this.Close();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _tcs?.TrySetResult(null);
            try { UnregisterActiveInstance(); } catch { }
            this.Close();
        }

        // Helper to show the settings window on an STA thread and return updated settings JSON or null
        // Non-blocking async variant so callers need not block the main UI thread.
        public static Task<string?> ShowDialogModalAsync(string currentJson)
        {
            lock (s_lock)
            {
                if (s_activeWindow != null)
                {
                    // If an instance is already active, return its TCS so caller can await the existing window result.
                    return s_activeTcs?.Task ?? Task.FromResult<string?>(null);
                }
                var tcs = new TaskCompletionSource<string?>();
                s_activeTcs = tcs;

                var thread = new Thread(() =>
                {
                    var win = new SettingsWindow(currentJson, tcs);
                    try
                    {
                        win.ShowDialog();
                    }
                    catch
                    {
                        // Ensure TCS completes in case of unexpected failure
                        tcs.TrySetResult(null);
                    }
                    finally
                    {
                        // If window closed without producing a result, ensure TCS has a value
                        if (!tcs.Task.IsCompleted)
                        {
                            tcs.TrySetResult(null);
                        }
                        lock (s_lock)
                        {
                            s_activeTcs = null;
                            s_activeWindow = null;
                        }
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();

                // Return the task; caller should await it. Do not block the caller thread.
                return tcs.Task;
            }
        }

        // Backwards-compatible synchronous wrapper (keeps previous behavior).
        public static string? ShowDialogModal(string currentJson)
        {
            return ShowDialogModalAsync(currentJson).GetAwaiter().GetResult();
        }

        private void UnregisterActiveInstance()
        {
            lock (s_lock)
            {
                if (s_activeWindow == this)
                {
                    s_activeWindow = null;
                    // ensure TCS cleared by caller path in ShowDialogModalAsync finalizer
                }
            }
        }
    }
}
