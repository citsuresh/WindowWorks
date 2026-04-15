using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
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
            // React to DataContext changes for MVVM
            this.DataContextChanged += HudSettingsControl_DataContextChanged;
            // Event handlers and mouse-wheel are wired in XAML now; no runtime hookup required.
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                var vm = sender as WindowWorks.App.UI.ViewModels.HudSettingsViewModel ?? this.DataContext as WindowWorks.App.UI.ViewModels.HudSettingsViewModel;
                if (vm == null) return;

                if (e.PropertyName == nameof(vm.BackgroundColor))
                {
                    // Use VM value directly to update preview
                    Dispatcher.Invoke(() =>
                    {
                        try { TxtHudBackground.Text = vm.BackgroundColor ?? string.Empty; } catch { }
                        try { UpdatePreviewFromText(); } catch { }
                    });
                }
                else if (e.PropertyName == nameof(vm.TransparencyPercent))
                {
                    Dispatcher.Invoke(() =>
                    {
                        try { if (SldTransparency != null) SldTransparency.Value = vm.TransparencyPercent; } catch { }
                        try { if (_transparencyValueText != null) _transparencyValueText.Text = vm.TransparencyPercent.ToString(); } catch { }
                        // Recompute final hex and update textbox/preview
                        try
                        {
                            var parsed = ParseColorFromString(vm.BackgroundColor) ?? Colors.Transparent;
                            byte alpha = (byte)(255 * (100 - Math.Clamp(vm.TransparencyPercent, 0, 100)) / 100.0);
                            var finalHex = $"#{alpha:X2}{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";
                            try { TxtHudBackground.Text = finalHex; } catch { }
                            try { UpdatePreviewFromText(); } catch { }
                        }
                        catch { }
                        try { UpdateAlphaPreview(); } catch { }
                    });
                }
                else if (e.PropertyName == nameof(vm.FontSize))
                {
                    Dispatcher.Invoke(() => { try { TxtHudFontSize.Text = vm.FontSize.ToString(); } catch { } UpdatePreviewFontAndCorner(); });
                }
                else if (e.PropertyName == nameof(vm.CornerRadius))
                {
                    Dispatcher.Invoke(() => { try { TxtHudCorner.Text = vm.CornerRadius.ToString(); } catch { } UpdatePreviewFontAndCorner(); });
                }
                else if (e.PropertyName == nameof(vm.DurationMs))
                {
                    Dispatcher.Invoke(() => { try { TxtHudDuration.Text = vm.DurationMs.ToString(); } catch { } });
                }
                else if (e.PropertyName == nameof(vm.ShowOnOpacityChange) || e.PropertyName == nameof(vm.ShowOnTopmostToggle) || e.PropertyName == nameof(vm.ShowOnPresetApplied))
                {
                    Dispatcher.Invoke(() =>
                    {
                        try { ChkOpacityHud.IsChecked = vm.ShowOnOpacityChange; } catch { }
                        try { ChkTopmostHud.IsChecked = vm.ShowOnTopmostToggle; } catch { }
                        try { ChkPresetHud.IsChecked = vm.ShowOnPresetApplied; } catch { }
                    });
                }
            }
            catch { }
        }

        private void HudSettingsControl_DataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                if (e.OldValue is System.ComponentModel.INotifyPropertyChanged oldNpc) oldNpc.PropertyChanged -= ViewModel_PropertyChanged;
                if (e.NewValue is System.ComponentModel.INotifyPropertyChanged npc) npc.PropertyChanged += ViewModel_PropertyChanged;

                // When DataContext changes, sync UI previews from VM
                var vm = this.DataContext as WindowWorks.App.UI.ViewModels.HudSettingsViewModel;
                if (vm != null)
                {
                    try { TxtHudBackground.Text = vm.BackgroundColor ?? string.Empty; } catch { }
                    try { if (SldTransparency != null) SldTransparency.Value = vm.TransparencyPercent; } catch { }
                    try { TxtHudFontSize.Text = vm.FontSize.ToString(); } catch { }
                    try { TxtHudCorner.Text = vm.CornerRadius.ToString(); } catch { }
                    try { TxtHudDuration.Text = vm.DurationMs.ToString(); } catch { }
                    try { ChkOpacityHud.IsChecked = vm.ShowOnOpacityChange; } catch { }
                    try { ChkTopmostHud.IsChecked = vm.ShowOnTopmostToggle; } catch { }
                    try { ChkPresetHud.IsChecked = vm.ShowOnPresetApplied; } catch { }

                    UpdatePreviewFromText();
                    UpdateTransparencyDisplay();
                    UpdateAlphaPreview();
                }
            }
            catch { }
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

        private void TxtHudDuration_TextChanged(object? sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // No immediate visual preview needed for duration; handled by HUD when shown
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

        // LoadFromDictionary removed: HUD control now uses HudSettingsViewModel for loading and state.

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
                // Also update the final color hex and update preview/textbox background together
                try
                {
                    int percent = (int)(_transparencySlider?.Value ?? 0);
                    // Parse existing color (may be #RRGGBB or #AARRGGBB or named)
                    var baseCol = ParseColorFromString(TxtHudBackground.Text) ?? Colors.Transparent;
                    byte alpha = (byte)(255 * (100 - Math.Clamp(percent, 0, 100)) / 100.0);
                    var finalHex = $"#{alpha:X2}{baseCol.R:X2}{baseCol.G:X2}{baseCol.B:X2}";
                    try
                    {
                        // Update ViewModel if present
                        if (this.DataContext is WindowWorks.App.UI.ViewModels.HudSettingsViewModel vm)
                        {
                            vm.BackgroundColor = finalHex;
                            vm.TransparencyPercent = percent;
                        }
                        else
                        {
                            TxtHudBackground.Text = finalHex;
                        }
                    }
                    catch { }
                    // Ensure the textbox background and small preview reflect the new hex immediately
                    try { UpdatePreviewFromText(); } catch { }
                }
                catch { }

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
                // If a legacy textbox exists and contains a value, prefer it (back-compat).
                if (legacy != null && int.TryParse(legacy.Text, out var legacyVal))
                {
                    if (_transparencySlider != null) _transparencySlider.Value = legacyVal;
                    if (_transparencyValueText != null) _transparencyValueText.Text = legacyVal.ToString();
                }
                else
                {
                    // Otherwise preserve current slider value (likely set via ViewModel binding) and reflect it in the display.
                    if (_transparencySlider != null && _transparencyValueText != null)
                    {
                        _transparencyValueText.Text = ((int)_transparencySlider.Value).ToString();
                    }
                    else if (_transparencyValueText != null)
                    {
                        _transparencyValueText.Text = "0";
                    }
                }
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
                    // Determine effective alpha: prefer explicit alpha in the color text (#AARRGGBB),
                    // otherwise use the current slider value to build the final color with alpha.
                    byte effectiveAlpha = col.Value.A;
                    if (string.IsNullOrWhiteSpace(txt) || !(txt.StartsWith("#") && txt.Length == 9))
                    {
                        // No explicit alpha in the string; use slider value if available
                        int alphaPercent = (int)(_transparencySlider?.Value ?? 0);
                        effectiveAlpha = (byte)(255 * (100 - Math.Clamp(alphaPercent, 0, 100)) / 100.0);
                    }

                    var colWithAlpha = Color.FromArgb(effectiveAlpha, col.Value.R, col.Value.G, col.Value.B);
                    _previewRect.Fill = new SolidColorBrush(colWithAlpha);
                    string hex = $"#{colWithAlpha.A:X2}{colWithAlpha.R:X2}{colWithAlpha.G:X2}{colWithAlpha.B:X2}";
                    _previewRect.ToolTip = hex;
                    var lum = (0.299 * colWithAlpha.R + 0.587 * colWithAlpha.G + 0.114 * colWithAlpha.B) / 255.0;
                    _previewRect.Stroke = lum < 0.5 ? Brushes.White : Brushes.Gray;
                    // apply color as background of the textbox for inline preview (respect alpha)
                    try
                    {
                        var tb = this.FindName("TxtHudBackground") as System.Windows.Controls.TextBox;
                        if (tb != null)
                        {
                            var brush = new SolidColorBrush(colWithAlpha);
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
                if (s.StartsWith("#")) s = s.Substring(1);
                // Normalize to 6 or 8 hex digits
                if (s.Length == 6)
                {
                    var r = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    var g = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    var b = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    return Color.FromArgb(255, r, g, b);
                }
                if (s.Length == 8)
                {
                    var a = byte.Parse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    var r = byte.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    var g = byte.Parse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    var b = byte.Parse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
                    return Color.FromArgb(a, r, g, b);
                }
                // Fallback to ColorConverter for named colors or other formats
                try
                {
                    var conv = ColorConverter.ConvertFromString(text);
                    if (conv is Color c) return c;
                }
                catch { }
            }
            catch { }
            return null;
        }
    }
}
