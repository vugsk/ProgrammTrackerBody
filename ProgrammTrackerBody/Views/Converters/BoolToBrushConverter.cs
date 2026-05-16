using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ProgrammTrackerBody.Views.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = new SolidColorBrush(Color.FromRgb(56, 142, 60));

    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(117, 117, 117));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
