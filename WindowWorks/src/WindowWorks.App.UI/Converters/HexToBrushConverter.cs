using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WindowWorks.App.UI.Converters
{
    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    var txt = s.Trim();
                    // Accept #RRGGBB or #AARRGGBB
                    if (!txt.StartsWith("#")) txt = "#" + txt;
                    var conv = (Color)ColorConverter.ConvertFromString(txt);
                    return new SolidColorBrush(conv);
                }
            }
            catch { }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
