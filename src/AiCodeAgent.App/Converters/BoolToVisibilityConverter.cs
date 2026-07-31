using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AiCodeAgent.App.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isVisible = value is bool b && b;
        if (parameter is string param && param == "invert")
            isVisible = !isVisible;
        
        if (targetType == typeof(bool))
            return isVisible;
        
        return isVisible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b;
    }
}