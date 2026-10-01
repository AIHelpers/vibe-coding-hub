using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AiCodeAgent.App.Converters;

public class BoolToStatusIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? "✅" : "⏳";
        return "⏳";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
