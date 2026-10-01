using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AiCodeAgent.App.Converters;

public class BoolToErrorBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new SolidColorBrush(Colors.Red);
        return new SolidColorBrush(Colors.Green);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}