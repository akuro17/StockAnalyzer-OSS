using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converts a double value from the ViewModel into a GridLength for UI Column/Row Definitions,
/// and converts back a GridLength from UI changes (e.g. via GridSplitter) to a double.
/// </summary>
public class DoubleToGridLengthConverter : IValueConverter
{
    public static readonly DoubleToGridLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return new GridLength(Math.Max(0, d), GridUnitType.Pixel);
        }
        return new GridLength(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GridLength gridLength)
        {
            return gridLength.Value;
        }
        return 0d;
    }
}
