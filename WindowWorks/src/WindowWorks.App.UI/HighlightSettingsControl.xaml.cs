using System.Windows.Controls;
// Required for WinForms ColorDialog
using WinForms = System.Windows.Forms;

namespace WindowWorks.App.UI
{
    public partial class HighlightSettingsControl : UserControl
    {
        private System.Windows.Shapes.Rectangle? _previewRect;
        // Embedded preview for the whole highlight sample
        private System.Windows.Controls.Border? _highlightPreviewBorder;
        private System.Windows.Controls.TextBlock? _highlightPreviewLabel;

        public HighlightSettingsControl()
        {
            InitializeComponent();
            SetupColorPickerUi();
            try { SetupPreviewUi(); } catch { }
            // Numeric controls and event handlers are defined in XAML; no runtime wiring required here.
        }

        private void SldBorderTransparency_ValueChanged(object? sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                var txt = this.FindName("TxtBorderTransparencyValue") as System.Windows.Controls.TextBlock;
                if (txt != null) txt.Text = ((int)e.NewValue).ToString();
                UpdateHighlightTransparency();
            }
            catch { }
        }

        private void TxtHighlightMs_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Value tracked for Save; no live preview required currently
        }

        private void TxtHudMs_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Value tracked for Save; no live preview required currently
        }

        private void NumericBox_PreviewMouseWheel(object? sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.TextBox tb)
                {
                    int step = (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift)) ? 5 : 1;
                    int v;
                    if (int.TryParse(tb.Text, out var cur)) v = cur;
                    else v = step;
                    v += e.Delta > 0 ? step : -step;
                    if (tb.Name == "TxtCornerRadius") v = Math.Max(0, v);
                    if (tb.Name == "TxtBorderThickness") v = Math.Max(0, v);
                    tb.Text = v.ToString();
                    UpdateHighlightPreviewSettings();
                    e.Handled = true;
                }
            }
            catch { }
        }

        private void UpdateHighlightTransparency()
        {
            try
            {
                var preview = this.FindName("HighlightPreviewBorder") as System.Windows.Controls.Border ?? _highlightPreviewBorder;
                if (preview == null) return;
                var sld = this.FindName("SldBorderTransparency") as System.Windows.Controls.Slider;
                int alphaPercent = sld != null ? (int)sld.Value : 0;
                var col = ParseColorFromString(TxtBorderColor.Text);
                if (col.HasValue)
                {
                    byte alpha = (byte)(255 * (100 - alphaPercent) / 100.0);
                    var colWithAlpha = System.Windows.Media.Color.FromArgb(alpha, col.Value.R, col.Value.G, col.Value.B);
                    preview.BorderBrush = new System.Windows.Media.SolidColorBrush(colWithAlpha);
                }
            }
            catch { }
        }

        private void BtnBorderThicknessUp_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                var tb = this.FindName("TxtBorderThickness") as System.Windows.Controls.TextBox;
                if (tb != null)
                {
                    if (int.TryParse(tb.Text, out var v)) tb.Text = (v + 1).ToString(); else tb.Text = "2";
                    UpdateHighlightPreviewSettings();
                }
            }
            catch { }
        }

        private void BtnBorderThicknessDown_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                var tb = this.FindName("TxtBorderThickness") as System.Windows.Controls.TextBox;
                if (tb != null)
                {
                    if (int.TryParse(tb.Text, out var v)) tb.Text = Math.Max(0, v - 1).ToString(); else tb.Text = "2";
                    UpdateHighlightPreviewSettings();
                }
            }
            catch { }
        }

        private void BtnCornerRadiusUp_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                var tb = this.FindName("TxtCornerRadius") as System.Windows.Controls.TextBox;
                if (tb != null)
                {
                    if (int.TryParse(tb.Text, out var v)) tb.Text = (v + 1).ToString(); else tb.Text = "6";
                    UpdateHighlightPreviewSettings();
                }
            }
            catch { }
        }

        private void BtnCornerRadiusDown_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                var tb = this.FindName("TxtCornerRadius") as System.Windows.Controls.TextBox;
                if (tb != null)
                {
                    if (int.TryParse(tb.Text, out var v)) tb.Text = Math.Max(0, v - 1).ToString(); else tb.Text = "6";
                    UpdateHighlightPreviewSettings();
                }
            }
            catch { }
        }

        private void TxtBorderThickness_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateHighlightPreviewSettings();
        }

        private void TxtCornerRadius_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateHighlightPreviewSettings();
        }

        private void SetupPreviewUi()
        {
            try
            {
                var existing = this.FindName("HighlightPreviewBorder") as System.Windows.Controls.Border;
                if (existing != null)
                {
                    _highlightPreviewBorder = existing;
                    _highlightPreviewLabel = this.FindName("HighlightPreviewLabel") as System.Windows.Controls.TextBlock;
                    return;
                }
                // nothing to create at runtime because XAML contains the preview; keep fields null-safe
            }
            catch { }
        }

        // Numeric controls are declared in XAML; no dynamic creation required.

        private void UpdateHighlightPreviewSettings()
        {
            try
            {
                var tbThickness = this.FindName("TxtBorderThickness") as System.Windows.Controls.TextBox;
                var tbCorner = this.FindName("TxtCornerRadius") as System.Windows.Controls.TextBox;
                var preview = this.FindName("HighlightPreviewBorder") as System.Windows.Controls.Border ?? _highlightPreviewBorder;
                if (preview != null)
                {
                    if (int.TryParse(tbCorner?.Text, out var cr)) preview.CornerRadius = new System.Windows.CornerRadius(cr);
                    if (int.TryParse(tbThickness?.Text, out var bt)) preview.BorderThickness = new System.Windows.Thickness(bt);
                    // Update border transparency if slider present
                    try
                    {
                        var sld = this.FindName("SldBorderTransparency") as System.Windows.Controls.Slider;
                        if (sld != null)
                        {
                            UpdateHighlightTransparency();
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> d)
        {
            if (d == null) return;
            int? loadedTransparencyPercent = null;
            if (d.TryGetValue("HighlightBorderColor", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var colorText = v.GetString();
                TxtBorderColor.Text = colorText;
                // If color contains alpha as #AARRGGBB, derive transparency percent from it
                try
                {
                    if (!string.IsNullOrWhiteSpace(colorText) && colorText.Trim().StartsWith("#") && colorText.Trim().Length == 9)
                    {
                        var aHex = colorText.Trim().Substring(1, 2);
                        if (byte.TryParse(aHex, System.Globalization.NumberStyles.HexNumber, null, out var a))
                        {
                            loadedTransparencyPercent = (int)Math.Round(100.0 - (a / 255.0 * 100.0));
                        }
                    }
                }
                catch { }
            }
            if (d.TryGetValue("HighlightBorderThickness", out v) && v.TryGetInt32(out var bt)) TxtBorderThickness.Text = bt.ToString();
            if (d.TryGetValue("HighlightCornerRadius", out v) && v.TryGetInt32(out var cr)) TxtCornerRadius.Text = cr.ToString();
            if (d.TryGetValue("HighlightDurationMs", out v) && v.TryGetInt32(out var hm)) TxtHighlightMs.Text = hm.ToString();
            if (d.TryGetValue("HudDurationMs", out v) && v.TryGetInt32(out var um)) TxtHudMs.Text = um.ToString();
            if (d.TryGetValue("HighlightBorderTransparencyPercent", out v) && v.TryGetInt32(out var tp))
            {
                // explicit percent overrides alpha derived from color
                loadedTransparencyPercent = tp;
            }

            // Ensure preview reflects loaded values once control is loaded
            try
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    try
                    {
                        // If we have a loaded transparency percent (from color alpha or explicit setting), apply it now when visual tree is ready
                        try
                        {
                            if (loadedTransparencyPercent.HasValue)
                            {
                                var sld = this.FindName("SldBorderTransparency") as System.Windows.Controls.Slider;
                                var txt = this.FindName("TxtBorderTransparencyValue") as System.Windows.Controls.TextBlock;
                                if (sld != null) sld.Value = loadedTransparencyPercent.Value;
                                if (txt != null) txt.Text = loadedTransparencyPercent.Value.ToString();
                            }
                        }
                        catch { }

                        UpdateColorPreview(TxtBorderColor.Text);
                        UpdateHighlightPreviewSettings();
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        private void SetupColorPickerUi()
        {
            try
            {
                // Ensure TxtBorderColor exists in XAML
                if (TxtBorderColor == null) return;

                // Wire designer-provided preview rectangle (event handlers wired in XAML)
                try
                {
                    var rect = this.FindName("RectColorPreview") as System.Windows.Shapes.Rectangle;
                    if (rect != null) _previewRect = rect;
                }
                catch { }

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
                var col = ParseColorFromString(text);
                if (col.HasValue)
                {
                    var brush = new System.Windows.Media.SolidColorBrush(col.Value);
                    // update rectangle if present (backwards compatible)
                    if (rect != null)
                    {
                        rect.Fill = brush;
                        // show hex tooltip for current color
                        string hex = col.Value.A == 255
                            ? $"#{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}"
                            : $"#{col.Value.A:X2}{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}";
                        rect.ToolTip = hex;
                        // choose a contrasting stroke for visibility
                        var lumRect = (0.299 * col.Value.R + 0.587 * col.Value.G + 0.114 * col.Value.B) / 255.0;
                        rect.Stroke = lumRect < 0.5 ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray;
                    }

                    // Apply the color as the background of the textbox itself so users see the preview inline
                    try
                    {
                        var tb = this.FindName("TxtBorderColor") as System.Windows.Controls.TextBox;
                        if (tb != null)
                        {
                            tb.Background = brush;
                            var lum = (0.299 * col.Value.R + 0.587 * col.Value.G + 0.114 * col.Value.B) / 255.0;
                            tb.Foreground = lum < 0.5 ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
                            tb.ToolTip = tb.Text;
                        }
                    }
                    catch { }
                    // Update embedded highlight preview border if available
                    try
                    {
                        var preview = this.FindName("HighlightPreviewBorder") as System.Windows.Controls.Border ?? _highlightPreviewBorder;
                        if (preview != null)
                        {
                            // For highlights we show only the border (no fill). Set transparent background and set BorderBrush.
                            preview.Background = System.Windows.Media.Brushes.Transparent;
                            // Apply transparency to border brush if slider present
                            var sld = this.FindName("SldBorderTransparency") as System.Windows.Controls.Slider;
                            if (sld != null)
                            {
                                int alphaPercent = (int)sld.Value;
                                byte alpha = (byte)(255 * (100 - alphaPercent) / 100.0);
                                var colWithAlpha = System.Windows.Media.Color.FromArgb(alpha, col.Value.R, col.Value.G, col.Value.B);
                                preview.BorderBrush = new System.Windows.Media.SolidColorBrush(colWithAlpha);
                                // reflect transparency on the preview border stroke thickness visual (optional)
                                try { preview.BorderThickness = preview.BorderThickness; } catch { }
                            }
                            else
                            {
                                preview.BorderBrush = brush;
                            }
                            // ensure border thickness reflects current setting
                            if (int.TryParse(this.FindName("TxtBorderThickness") is System.Windows.Controls.TextBox tbth ? tbth.Text : null, out var bt))
                            {
                                preview.BorderThickness = new System.Windows.Thickness(bt);
                            }
                            // choose contrasting foreground for label
                            var lbl = this.FindName("HighlightPreviewLabel") as System.Windows.Controls.TextBlock ?? _highlightPreviewLabel;
                            if (lbl != null)
                            {
                                var lum = (0.299 * col.Value.R + 0.587 * col.Value.G + 0.114 * col.Value.B) / 255.0;
                                lbl.Foreground = lum < 0.5 ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    if (rect != null)
                    {
                        rect.Fill = System.Windows.Media.Brushes.Transparent;
                        rect.ToolTip = "";
                        rect.Stroke = System.Windows.Media.Brushes.Gray;
                    }
                    try
                    {
                        var tb = this.FindName("TxtBorderColor") as System.Windows.Controls.TextBox;
                        if (tb != null)
                        {
                            tb.Background = System.Windows.Media.Brushes.Transparent;
                            tb.Foreground = System.Windows.Media.Brushes.Black;
                        }
                    }
                    catch { }
                    try
                    {
                        var preview = this.FindName("HighlightPreviewBorder") as System.Windows.Controls.Border ?? _highlightPreviewBorder;
                        if (preview != null)
                        {
                            preview.Background = System.Windows.Media.Brushes.Transparent;
                            preview.BorderBrush = System.Windows.Media.Brushes.Gray;
                            var lbl = this.FindName("HighlightPreviewLabel") as System.Windows.Controls.TextBlock ?? _highlightPreviewLabel;
                            if (lbl != null) lbl.Foreground = System.Windows.Media.Brushes.Black;
                        }
                    }
                    catch { }
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
