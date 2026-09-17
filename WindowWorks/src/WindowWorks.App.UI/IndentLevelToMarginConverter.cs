using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// Converts a <see cref="PickerAncestorBoxItem.IndentLevel"/> into a per-box left margin, so
    /// the flat yellow-box list (§6.3) hints at DOM/ancestor nesting depth without becoming a real
    /// tree view (docs/REPARENT_FEATURE_PLAN.md §Phase 6). Deliberately a small per-level step
    /// (see <see cref="StepDip"/>) per explicit user direction to keep indentation "as low as
    /// possible" — the box list already competes for horizontal space with the truncated DOM
    /// element labels (§6.6), so indentation must stay cheap rather than growing box width
    /// significantly per level.
    /// </summary>
    public sealed class IndentLevelToMarginConverter : IValueConverter
    {
        private const double StepDip = 3.0;
        private const double BaseMarginDip = 2.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int level = value is int i ? i : 0;
            if (level < 0)
            {
                level = 0;
            }
            double left = BaseMarginDip + (level * StepDip);
            return new Thickness(left, BaseMarginDip, BaseMarginDip, BaseMarginDip);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
