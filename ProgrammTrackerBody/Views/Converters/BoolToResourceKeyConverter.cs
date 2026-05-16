using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProgrammTrackerBody.Views.Converters;

// Looks up one of two resource keys passed via ConverterParameter as
// "trueKey|falseKey" and returns the resolved string from app resources.
public sealed class BoolToResourceKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string spec)
        {
            return value;
        }

        var parts = spec.Split('|');
        if (parts.Length != 2)
        {
            return value;
        }

        var key = value is true ? parts[0] : parts[1];
        return Application.Current?.TryFindResource(key) ?? key;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
