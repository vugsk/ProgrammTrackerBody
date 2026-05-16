using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProgrammTrackerBody.Views.Converters;

public sealed class ResourceLookupConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
        {
            return value;
        }

        var resource = Application.Current?.TryFindResource(key);
        return resource ?? key;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
