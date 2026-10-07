using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AiCodeAgent.App.Converters;

/// <summary>Turns SVG-style path data ("M 0,0 C …") from a view model into a <see cref="Geometry"/>.</summary>
public sealed class PathDataConverter : IValueConverter
{
    public static readonly PathDataConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string data || string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            return StreamGeometry.Parse(data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
