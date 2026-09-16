using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace StockAnalyzer.Avalonia.Converters;

public class IndicatorColorToAvaloniaColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IndicatorColor ic)
        {
            return Color.FromArgb(ic.A, ic.R, ic.G, ic.B);
        }
        return Color.FromRgb(0, 0, 0); // Default black
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c)
        {
            return new IndicatorColor(c.A, c.R, c.G, c.B);
        }
        return new IndicatorColor(255, 0, 0, 0);
    }
}
