using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace AiCodeAgent.App.Converters;

/// <summary>Maps true to a 1* grid column and false to a zero-width column.</summary>
public class BoolToStarGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isStar = value is true;
        return isStar ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
