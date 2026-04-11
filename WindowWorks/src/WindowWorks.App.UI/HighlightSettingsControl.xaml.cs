using System.Windows.Controls;
// Required for WinForms ColorDialog
using WinForms = System.Windows.Forms;

namespace WindowWorks.App.UI
{
    public partial class HighlightSettingsControl : UserControl
    {
        private System.Windows.Shapes.Rectangle? _previewRect;

        public HighlightSettingsControl()
        {
            InitializeComponent();
            SetupColorPickerUi();
        }

        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> d)
        {
            if (d == null) return;
            if (d.TryGetValue("HighlightBorderColor", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) TxtBorderColor.Text = v.GetString();
            if (d.TryGetValue("HighlightBorderThickness", out v) && v.TryGetInt32(out var bt)) TxtBorderThickness.Text = bt.ToString();
            if (d.TryGetValue("HighlightCornerRadius", out v) && v.TryGetInt32(out var cr)) TxtCornerRadius.Text = cr.ToString();
            if (d.TryGetValue("HighlightDurationMs", out v) && v.TryGetInt32(out var hm)) TxtHighlightMs.Text = hm.ToString();
            if (d.TryGetValue("HudDurationMs", out v) && v.TryGetInt32(out var um)) TxtHudMs.Text = um.ToString();
            if (d.TryGetValue("UseSystemColors", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkUseSystemColors.IsChecked = true;
        }

        private void SetupColorPickerUi()
        {
            try
            {
                // Ensure TxtBorderColor exists in XAML
                if (TxtBorderColor == null) return;

                // Hook text changed for live preview
                TxtBorderColor.TextChanged += TxtBorderColor_TextChanged;

                // If a preview already exists, skip adding controls
                var parent = TxtBorderColor.Parent as Panel;
                if (parent == null) return;

                // Add choose color button
                var btn = new System.Windows.Controls.Button()
                {
                    Name = "BtnChooseColor",
                    Content = "Choose...",
                    Width = 80,
                    Margin = new System.Windows.Thickness(8, 0, 0, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                btn.Click += BtnChooseColor_Click;

                // Add preview rectangle
                var rect = new System.Windows.Shapes.Rectangle()
                {
                    Name = "RectColorPreview",
                    Width = 28,
                    Height = 20,
                    Stroke = System.Windows.Media.Brushes.Gray,
                    StrokeThickness = 1,
                    Margin = new System.Windows.Thickness(8, 0, 0, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };

                // Add to parent panel after the textbox
                parent.Children.Add(btn);
                parent.Children.Add(rect);
                // keep a reference for quick updates
                _previewRect = rect;

                // If textbox already has a value, update preview
                UpdateColorPreview(TxtBorderColor.Text);
            }
            catch { }
        }

        private void TxtBorderColor_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateColorPreview(TxtBorderColor.Text);
        }

        private void BtnChooseColor_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                // Use WinForms color dialog for simplicity
                WinForms.ColorDialog dlg = new WinForms.ColorDialog();
                // Try to initialize with current color
                var cur = ParseColorFromString(TxtBorderColor.Text);
                if (cur.HasValue)
                {
                    var c = cur.Value;
                    dlg.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
                }
                var res = dlg.ShowDialog();
                if (res == WinForms.DialogResult.OK)
                {
                    var col = dlg.Color;
                    // write as hex #RRGGBB
                    TxtBorderColor.Text = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
                }
            }
            catch { }
        }

        private void UpdateColorPreview(string? text)
        {
            try
            {
                var rect = _previewRect ?? (this.FindName("RectColorPreview") as System.Windows.Shapes.Rectangle) ?? FindVisualChildByName<System.Windows.Shapes.Rectangle>(this, "RectColorPreview");
                if (rect == null) return;

                var col = ParseColorFromString(text);
                if (col.HasValue)
                {
                    var brush = new System.Windows.Media.SolidColorBrush(col.Value);
                    rect.Fill = brush;
                    // show hex tooltip for current color
                    string hex = col.Value.A == 255
                        ? $"#{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}"
                        : $"#{col.Value.A:X2}{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}";
                    rect.ToolTip = hex;
                    // choose a contrasting stroke for visibility
                    var lum = (0.299 * col.Value.R + 0.587 * col.Value.G + 0.114 * col.Value.B) / 255.0;
                    rect.Stroke = lum < 0.5 ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray;
                }
                else
                {
                    rect.Fill = System.Windows.Media.Brushes.Transparent;
                    rect.ToolTip = "";
                    rect.Stroke = System.Windows.Media.Brushes.Gray;
                }
            }
            catch { }
        }

        private System.Windows.Media.Color? ParseColorFromString(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                // Accept #RRGGBB or #AARRGGBB or RRGGBB
                var s = text.Trim();
                if (!s.StartsWith("#")) s = "#" + s;
                var conv = System.Windows.Media.ColorConverter.ConvertFromString(s);
                if (conv is System.Windows.Media.Color c) return c;
            }
            catch { }
            return null;
        }

        private static T? FindVisualChildByName<T>(System.Windows.DependencyObject parent, string name) where T : System.Windows.DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t && child is System.Windows.FrameworkElement fe && fe.Name == name) return t;
                var result = FindVisualChildByName<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }
    }
}
