using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converts the UI-agnostic <see cref="HsvData"/> to Avalonia's <see cref="HsvColor"/> and vice-versa
/// to decouple the ViewModel from the Avalonia framework.
/// </summary>
public class HsvDataToAvaloniaHsvColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HsvData hsv)
        {
            return new HsvColor(hsv.A, hsv.H, hsv.S, hsv.V);
        }
        return new HsvColor(1.0, 0.0, 0.0, 1.0); // Fallback Red
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HsvColor hsv)
        {
            return new HsvData(hsv.A, hsv.H, hsv.S, hsv.V);
        }
        return new HsvData(1.0, 0.0, 0.0, 1.0);
    }
}
