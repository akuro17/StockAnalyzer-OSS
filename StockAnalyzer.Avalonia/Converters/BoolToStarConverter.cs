using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converts boolean to star symbol: true → ★, false → ☆
/// </summary>
public class BoolToStarConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "★" : "☆";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() == "★";
    }
}
