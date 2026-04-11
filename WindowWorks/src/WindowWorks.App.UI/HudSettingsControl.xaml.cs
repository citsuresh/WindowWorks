using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WindowWorks.App.UI
{
    public partial class HudSettingsControl : UserControl
    {
        private System.Windows.Shapes.Rectangle? _previewRect;
        private System.Windows.Controls.Slider? _transparencySlider;
        private System.Windows.Controls.TextBlock? _transparencyValueText;
        private System.Windows.Shapes.Rectangle? _alphaPreviewRect;

        public HudSettingsControl()
        {
            InitializeComponent();
            SetupColorPickerUi();
            SetupTransparencyUi();
        }

        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> d)
        {
            if (d == null) return;
            if (d.TryGetValue("HudBackgroundColor", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) TxtHudBackground.Text = v.GetString() ?? string.Empty;
            if (d.TryGetValue("HudFontSize", out v) && v.TryGetInt32(out var fs)) TxtHudFontSize.Text = fs.ToString();
            if (d.TryGetValue("HudCornerRadius", out v) && v.TryGetInt32(out var cr)) TxtHudCorner.Text = cr.ToString();
            if (d.TryGetValue("HudTransparencyPercent", out v) && v.TryGetInt32(out var tp)) TxtTransparency.Text = tp.ToString();
            if (d.TryGetValue("ShowHudOnOpacityChange", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkOpacityHud.IsChecked = true; else if (d.TryGetValue("ShowHudOnOpacityChange", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkOpacityHud.IsChecked = false;
            if (d.TryGetValue("ShowHudOnTopmostToggle", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkTopmostHud.IsChecked = true; else if (d.TryGetValue("ShowHudOnTopmostToggle", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkTopmostHud.IsChecked = false;
            if (d.TryGetValue("ShowHudOnPresetApplied", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkPresetHud.IsChecked = true; else if (d.TryGetValue("ShowHudOnPresetApplied", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkPresetHud.IsChecked = false;

            // Ensure UI previews reflect loaded values
            try
            {
                UpdatePreviewFromText();
                UpdateTransparencyDisplay();
                UpdateAlphaPreview();
            }
            catch { }
        }

        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new System.Windows.Forms.ColorDialog();
                // Try parse existing color text
                try
                {
                    var txt = TxtHudBackground.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        // Accept #AARRGGBB or #RRGGBB
                        var col = System.Drawing.ColorTranslator.FromHtml(txt);
                        dlg.Color = col;
                    }
                }
                catch { }

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dlg.Color;
                    // Convert to #AARRGGBB
                    string hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
                    TxtHudBackground.Text = hex;
                }
            }
            catch { }
        }

        private void TxtHudBackground_TextChanged(object? sender, TextChangedEventArgs e)
        {
            try
            {
                var rect = _previewRect ?? (this.FindName("RectHudPreview") as Rectangle);
                if (rect == null) return;
                var txt = TxtHudBackground.Text?.Trim();
                var col = ParseColorFromString(txt);
                if (col.HasValue)
                {
                    rect.Fill = new SolidColorBrush(col.Value);
                    string hex = col.Value.A == 255
                        ? $"#{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}"
                        : $"#{col.Value.A:X2}{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}";
                    rect.ToolTip = hex;
                    var lum = (0.299 * col.Value.R + 0.587 * col.Value.G + 0.114 * col.Value.B) / 255.0;
                    rect.Stroke = lum < 0.5 ? Brushes.White : Brushes.Gray;
                }
                else
                {
                    rect.Fill = Brushes.Transparent;
                    rect.ToolTip = null;
                    rect.Stroke = Brushes.Gray;
                }
                // Update alpha preview as background color changed
                UpdateAlphaPreview();
            }
            catch { }
        }

        private void SetupTransparencyUi()
        {
            try
            {
                // Find existing textbox if present and hide it (we'll provide slider UI)
                var existing = this.FindName("TxtTransparency") as System.Windows.Controls.TextBox;
                Panel parent = null;
                if (existing != null)
                {
                    parent = existing.Parent as Panel;
                    existing.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // try to find a reasonable panel near the background textbox
                    var bg = this.FindName("TxtHudBackground") as System.Windows.Controls.Control;
                    parent = bg?.Parent as Panel;
                }

                if (parent == null) return;

                // If slider already exists, keep reference
                var s = this.FindName("SldTransparency") as System.Windows.Controls.Slider;
                if (s != null)
                {
                    _transparencySlider = s;
                    _transparencySlider.ValueChanged += SldTransparency_ValueChanged;
                }
                else
                {
                    _transparencySlider = new System.Windows.Controls.Slider()
                    {
                        Name = "SldTransparency",
                        Width = 200,
                        Minimum = 0,
                        Maximum = 100,
                        Value = 50,
                        Margin = new Thickness(6,0,0,0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    _transparencySlider.ValueChanged += SldTransparency_ValueChanged;
                    parent.Children.Add(_transparencySlider);
                    // Register name so FindName works
                    try { this.RegisterName(_transparencySlider.Name, _transparencySlider); } catch { }
                }

                // Add numeric display
                var tv = this.FindName("TxtTransparencyValue") as System.Windows.Controls.TextBlock;
                if (tv != null) _transparencyValueText = tv;
                else
                {
                    _transparencyValueText = new System.Windows.Controls.TextBlock() { Name = "TxtTransparencyValue", Width = 40, TextAlignment = TextAlignment.Center, Margin = new Thickness(6,0,0,0), VerticalAlignment = VerticalAlignment.Center };
                    parent.Children.Add(_transparencyValueText);
                    try { this.RegisterName(_transparencyValueText.Name, _transparencyValueText); } catch { }
                }

                // Add alpha preview rectangle
                var ar = this.FindName("RectHudPreviewWithAlpha") as System.Windows.Shapes.Rectangle;
                if (ar != null) _alphaPreviewRect = ar;
                else
                {
                    _alphaPreviewRect = new System.Windows.Shapes.Rectangle() { Name = "RectHudPreviewWithAlpha", Width = 28, Height = 20, Stroke = Brushes.Gray, StrokeThickness = 1, Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center };
                    parent.Children.Add(_alphaPreviewRect);
                    try { this.RegisterName(_alphaPreviewRect.Name, _alphaPreviewRect); } catch { }
                }

                // Initialize display
                UpdateTransparencyDisplay();
                UpdateAlphaPreview();
            }
            catch { }
        }

        private void SldTransparency_ValueChanged(object? sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (_transparencyValueText != null) _transparencyValueText.Text = ((int)(_transparencySlider?.Value ?? 0)).ToString();
                // Keep legacy textbox in sync if present
                var legacy = this.FindName("TxtTransparency") as System.Windows.Controls.TextBox;
                if (legacy != null) legacy.Text = ((int)(_transparencySlider?.Value ?? 0)).ToString();
                UpdateAlphaPreview();
            }
            catch { }
        }

        private void UpdateTransparencyDisplay()
        {
            try
            {
                var legacy = this.FindName("TxtTransparency") as System.Windows.Controls.TextBox;
                int val = 50;
                if (legacy != null && int.TryParse(legacy.Text, out var v)) val = v;
                if (_transparencySlider != null) _transparencySlider.Value = val;
                if (_transparencyValueText != null) _transparencyValueText.Text = val.ToString();
            }
            catch { }
        }

        private void UpdateAlphaPreview()
        {
            try
            {
                if (_alphaPreviewRect == null) return;
                var txt = TxtHudBackground.Text?.Trim();
                var col = ParseColorFromString(txt) ?? Colors.Transparent;
                int alphaPercent = (int)(_transparencySlider?.Value ?? 0);
                byte alpha = (byte)(255 * (100 - alphaPercent) / 100.0);
                var colWithAlpha = Color.FromArgb(alpha, col.R, col.G, col.B);
                _alphaPreviewRect.Fill = new SolidColorBrush(colWithAlpha);
            }
            catch { }
        }

        private void SetupColorPickerUi()
        {
            try
            {
                if (TxtHudBackground == null) return;
                TxtHudBackground.TextChanged += TxtHudBackground_TextChanged;
                var parent = TxtHudBackground.Parent as Panel;
                if (parent == null) return;
                // If preview already exists, keep reference
                var existing = this.FindName("RectHudPreview") as Rectangle;
                if (existing != null)
                {
                    _previewRect = existing;
                    UpdatePreviewFromText();
                    return;
                }
                var rect = new Rectangle()
                {
                    Name = "RectHudPreview",
                    Width = 28,
                    Height = 20,
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                parent.Children.Add(rect);
                _previewRect = rect;
                UpdatePreviewFromText();
            }
            catch { }
        }

        private void UpdatePreviewFromText()
        {
            try
            {
                var txt = TxtHudBackground.Text?.Trim();
                var col = ParseColorFromString(txt);
                if (_previewRect == null) return;
                if (col.HasValue)
                {
                    _previewRect.Fill = new SolidColorBrush(col.Value);
                    string hex = col.Value.A == 255
                        ? $"#{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}"
                        : $"#{col.Value.A:X2}{col.Value.R:X2}{col.Value.G:X2}{col.Value.B:X2}";
                    _previewRect.ToolTip = hex;
                    var lum = (0.299 * col.Value.R + 0.587 * col.Value.G + 0.114 * col.Value.B) / 255.0;
                    _previewRect.Stroke = lum < 0.5 ? Brushes.White : Brushes.Gray;
                }
                else
                {
                    _previewRect.Fill = Brushes.Transparent;
                    _previewRect.ToolTip = null;
                    _previewRect.Stroke = Brushes.Gray;
                }
            }
            catch { }
        }

        private Color? ParseColorFromString(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var s = text.Trim();
                if (!s.StartsWith("#")) s = "#" + s;
                var conv = ColorConverter.ConvertFromString(s);
                if (conv is Color c) return c;
            }
            catch { }
            return null;
        }
    }
}
