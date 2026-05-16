using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProgrammTrackerBody.Views.Converters;

// Converts an enum value to a localized string using the convention
// "<prefix>.<EnumValue>" where <prefix> is supplied via ConverterParameter.
public sealed class EnumResourceLookupConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var prefix = parameter as string;
        var key = string.IsNullOrEmpty(prefix)
            ? value.ToString()
            : $"{prefix}.{value}";

        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return Application.Current?.TryFindResource(key) ?? key;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
