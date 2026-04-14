using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WindowWorks.App.UI
{
    public partial class HudSettingsControl : UserControl
    {
        private System.Windows.Shapes.Rectangle? _previewRect;
        private System.Windows.Controls.Slider? _transparencySlider;
        private System.Windows.Controls.TextBlock? _transparencyValueText;
        private System.Windows.Shapes.Rectangle? _alphaPreviewRect;
        // Live preview controls created at runtime
        private System.Windows.Controls.Border? _previewBorder;
        private System.Windows.Controls.TextBlock? _previewMessage;
        private System.Windows.Controls.ProgressBar? _previewProgress;
        private System.Windows.Threading.DispatcherTimer? _previewTimer;
        private readonly TimeSpan _previewFadeMs = TimeSpan.FromMilliseconds(300);
        private readonly TimeSpan _previewVisibleDefault = TimeSpan.FromMilliseconds(1000);

        public HudSettingsControl()
        {
            InitializeComponent();
            SetupColorPickerUi();
            // Defer preview wiring until control is loaded so FindName lookups succeed
            this.Loaded += HudSettingsControl_Loaded;
            // Event handlers and mouse-wheel are wired in XAML now; no runtime hookup required.
        }

        private void HudSettingsControl_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                this.Loaded -= HudSettingsControl_Loaded;
                // Initialize transparency/preview now that visual tree is ready
                try { SetupTransparencyUi(); } catch { }
                try { SetupPreviewUi(); } catch { }
                try { UpdatePreviewFromText(); } catch { }
                try { UpdateTransparencyDisplay(); } catch { }
                try { UpdateAlphaPreview(); } catch { }
                try { PlayPreview(); } catch { }
            }
            catch { }
        }

        private void TxtHudFontSize_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdatePreviewFontAndCorner();
        }

        private void TxtHudCorner_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdatePreviewFontAndCorner();
        }

        private void BtnHudFontUp_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try { if (int.TryParse(TxtHudFontSize.Text, out var v)) TxtHudFontSize.Text = (v + 1).ToString(); else TxtHudFontSize.Text = "12"; UpdatePreviewFontAndCorner(); } catch { }
        }

        private void BtnHudFontDown_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try { if (int.TryParse(TxtHudFontSize.Text, out var v)) TxtHudFontSize.Text = Math.Max(1, v - 1).ToString(); else TxtHudFontSize.Text = "12"; UpdatePreviewFontAndCorner(); } catch { }
        }

        private void BtnHudCornerUp_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try { if (int.TryParse(TxtHudCorner.Text, out var v)) TxtHudCorner.Text = (v + 1).ToString(); else TxtHudCorner.Text = "6"; UpdatePreviewFontAndCorner(); } catch { }
        }

        private void BtnHudCornerDown_Click(object? sender, System.Windows.RoutedEventArgs e)
        {
            try { if (int.TryParse(TxtHudCorner.Text, out var v)) TxtHudCorner.Text = Math.Max(0, v - 1).ToString(); else TxtHudCorner.Text = "6"; UpdatePreviewFontAndCorner(); } catch { }
        }

        // Public wrapper so external controls (e.g., HighlightSettingsControl) can trigger the embedded preview
        public void PlayEmbeddedPreview()
        {
            try
            {
                Dispatcher.Invoke(() => PlayPreview());
            }
            catch { }
        }

        // Designer contains numeric up/down controls and RepeatButtons. Mouse-wheel wiring is handled in the constructor.

        private void NumericBox_PreviewMouseWheel(object? sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.TextBox tb)
                {
                    int step = (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift)) ? 5 : 1;
                    if (int.TryParse(tb.Text, out var v))
                    {
                        v += e.Delta > 0 ? step : -step;
                    }
                    else
                    {
                        v = step > 0 ? step : 0;
                    }
                    // clamp
                    if (tb.Name == "TxtHudCorner") v = Math.Max(0, v);
                    if (tb.Name == "TxtHudFontSize") v = Math.Max(1, v);
                    tb.Text = v.ToString();
                    UpdatePreviewFontAndCorner();
                    e.Handled = true;
                }
            }
            catch { }
        }

        public void LoadFromDictionary(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> d)
        {
            if (d == null) return;
            // Background color may include alpha as #AARRGGBB. If so, extract alpha into the transparency slider.
            if (d.TryGetValue("HudBackgroundColor", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var colorText = v.GetString() ?? string.Empty;
                TxtHudBackground.Text = colorText;
                try
                {
                    if (!string.IsNullOrWhiteSpace(colorText) && colorText.Trim().StartsWith("#") && colorText.Trim().Length == 9)
                    {
                        // #AARRGGBB
                        var aHex = colorText.Trim().Substring(1, 2);
                        if (byte.TryParse(aHex, System.Globalization.NumberStyles.HexNumber, null, out var a))
                        {
                            int percent = (int)Math.Round(100.0 - (a / 255.0 * 100.0));
                            var sld = this.FindName("SldTransparency") as System.Windows.Controls.Slider;
                            var legacy = this.FindName("TxtTransparency") as System.Windows.Controls.TextBox;
                            var txt = this.FindName("TxtTransparencyValue") as System.Windows.Controls.TextBlock;
                            if (sld != null) sld.Value = percent;
                            if (legacy != null) legacy.Text = percent.ToString();
                            if (txt != null) txt.Text = percent.ToString();
                        }
                    }
                }
                catch { }
            }
            if (d.TryGetValue("HudFontSize", out v) && v.TryGetInt32(out var fs)) TxtHudFontSize.Text = fs.ToString();
            if (d.TryGetValue("HudCornerRadius", out v) && v.TryGetInt32(out var cr)) TxtHudCorner.Text = cr.ToString();
            // Back-compat: if separate HudTransparencyPercent exists, use it only when color did not include alpha
            if (d.TryGetValue("HudTransparencyPercent", out v) && v.TryGetInt32(out var tp))
            {
                var sld = this.FindName("SldTransparency") as System.Windows.Controls.Slider;
                var legacy = this.FindName("TxtTransparency") as System.Windows.Controls.TextBox;
                var txt = this.FindName("TxtTransparencyValue") as System.Windows.Controls.TextBlock;
                if (sld != null && (sld.Value == 0)) sld.Value = tp; // only set if not already set from color alpha
                if (legacy != null && string.IsNullOrWhiteSpace(legacy.Text)) legacy.Text = tp.ToString();
                if (txt != null && string.IsNullOrWhiteSpace(txt.Text)) txt.Text = tp.ToString();
            }
            if (d.TryGetValue("ShowHudOnOpacityChange", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkOpacityHud.IsChecked = true; else if (d.TryGetValue("ShowHudOnOpacityChange", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkOpacityHud.IsChecked = false;
            if (d.TryGetValue("ShowHudOnTopmostToggle", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkTopmostHud.IsChecked = true; else if (d.TryGetValue("ShowHudOnTopmostToggle", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkTopmostHud.IsChecked = false;
            if (d.TryGetValue("ShowHudOnPresetApplied", out v) && v.ValueKind == System.Text.Json.JsonValueKind.True) ChkPresetHud.IsChecked = true; else if (d.TryGetValue("ShowHudOnPresetApplied", out v) && v.ValueKind == System.Text.Json.JsonValueKind.False) ChkPresetHud.IsChecked = false;

            // Ensure UI previews reflect loaded values. Defer playing the preview until
            // the control is loaded/attached to the visual tree so the animation is visible.
            try
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    try
                    {
                        UpdatePreviewFromText();
                        UpdateTransparencyDisplay();
                        UpdateAlphaPreview();
                        // Show the embedded preview once when settings are loaded
                        PlayPreview();
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
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

        private void PlayPreview()
        {
            try
            {
                if (_previewBorder == null) return;

                // Ensure preview is visible and fully opaque (always-on preview)
                try
                {
                    _previewTimer?.Stop();
                    _previewTimer = null;
                }
                catch { }

                _previewBorder.Visibility = Visibility.Visible;
                _previewBorder.Opacity = 1.0;
            }
            catch { }
        }

        private void UpdatePreviewFontAndCorner()
        {
            try
            {
                if (_previewMessage != null)
                {
                    if (int.TryParse(TxtHudFontSize?.Text, out var fs) && fs > 0)
                    {
                        _previewMessage.FontSize = fs;
                    }
                }
                if (_previewBorder != null)
                {
                    if (int.TryParse(TxtHudCorner?.Text, out var cr) && cr >= 0)
                    {
                        _previewBorder.CornerRadius = new CornerRadius(cr);
                    }
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
                // Play sample preview when the color changes
                PlayPreview();
            }
            catch { }
        }

        private void SetupTransparencyUi()
        {
            try
            {
                // Wire designer-provided slider, numeric display and alpha-preview rectangle
                try
                {
                    _transparencySlider = this.FindName("SldTransparency") as System.Windows.Controls.Slider;
                    // ValueChanged is wired in XAML (SldTransparency_ValueChanged)
                }
                catch { }

                try
                {
                    _transparencyValueText = this.FindName("TxtTransparencyValue") as System.Windows.Controls.TextBlock;
                }
                catch { }

                try
                {
                    _alphaPreviewRect = this.FindName("RectHudPreviewWithAlpha") as System.Windows.Shapes.Rectangle;
                }
                catch { }

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
                // Play preview when transparency changes
                PlayPreview();
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
                // Also update full preview if present
                try
                {
                    if (_previewBorder != null)
                    {
                        var brush = new SolidColorBrush(colWithAlpha);
                        _previewBorder.Background = brush;
                    }
                    if (_previewMessage != null)
                    {
                        // keep contrast
                        var lum = (0.299 * col.R + 0.587 * col.G + 0.114 * col.B) / 255.0;
                        _previewMessage.Foreground = lum < 0.5 ? Brushes.White : Brushes.Black;
                    }
                    if (_previewProgress != null)
                    {
                        _previewProgress.Value = _transparencySlider?.Value is double v ? (100 - v) : 75;
                    }
                }
                catch { }
            }
            catch { }
        }

        private void SetupPreviewUi()
        {
            try
            {
                // Use designer-provided PreviewBorder if present
                var existing = this.FindName("PreviewBorder") as System.Windows.Controls.Border;
                if (existing != null)
                {
                    _previewBorder = existing;
                    _previewMessage = this.FindName("PreviewMessage") as System.Windows.Controls.TextBlock;
                    _previewProgress = this.FindName("PreviewProgress") as System.Windows.Controls.ProgressBar;
                    UpdatePreviewFromText();
                    UpdateAlphaPreview();
                }
            }
            catch { }
        }

        private void SetupColorPickerUi()
        {
            try
            {
                if (TxtHudBackground == null) return;
                // TextChanged wired in XAML
                // Wire designer-provided rectangle preview
                try
                {
                    var rect = this.FindName("RectHudPreview") as Rectangle;
                    if (rect != null) _previewRect = rect;
                }
                catch { }
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
                    // apply color as background of the textbox for inline preview
                    try
                    {
                        var tb = this.FindName("TxtHudBackground") as System.Windows.Controls.TextBox;
                        if (tb != null)
                        {
                            var brush = new SolidColorBrush(col.Value);
                            tb.Background = brush;
                            tb.Foreground = lum < 0.5 ? Brushes.White : Brushes.Black;
                            tb.ToolTip = hex;
                        }
                    }
                    catch { }
                }
                else
                {
                    _previewRect.Fill = Brushes.Transparent;
                    _previewRect.ToolTip = null;
                    _previewRect.Stroke = Brushes.Gray;
                    try
                    {
                        var tb = this.FindName("TxtHudBackground") as System.Windows.Controls.TextBox;
                        if (tb != null)
                        {
                            tb.Background = Brushes.Transparent;
                            tb.Foreground = Brushes.Black;
                            tb.ToolTip = null;
                        }
                    }
                    catch { }
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
