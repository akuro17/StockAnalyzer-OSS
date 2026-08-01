using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converts <see cref="HsvData"/> to an Avalonia <see cref="IBrush"/>.
/// </summary>
public class HsvDataToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HsvData hsv)
        {
            var hsvColor = new HsvColor(hsv.A, hsv.H, hsv.S, hsv.V);
            return new SolidColorBrush(hsvColor.ToRgb());
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
