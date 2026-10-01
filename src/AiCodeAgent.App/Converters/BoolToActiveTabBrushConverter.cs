using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AiCodeAgent.App.Converters;

/// <summary>
/// Highlights the active editor tab in the tab strip. A light overlay for
/// the active tab, transparent (so the dark tab-strip background shows
/// through) for inactive ones.
/// </summary>
public class BoolToActiveTabBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush InactiveBrush = new(Colors.Transparent);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b ? ActiveBrush : InactiveBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
