using System;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

public static class NumericalConverters
{
    public static IValueConverter IsGreaterThanZero { get; } =
        new FuncValueConverter<int, bool>(x => x > 0);

    public static IValueConverter IsGreaterThanOne { get; } =
        new FuncValueConverter<int, bool>(x => x > 1);

    public static IValueConverter IsEqualToZero { get; } =
        new FuncValueConverter<int, bool>(x => x == 0);

    public static IValueConverter IsEqualTo { get; } = new EqualityConverter();
}

public class EqualityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        if (int.TryParse(value.ToString(), out int val) && int.TryParse(parameter.ToString(), out int target))
        {
            return val == target;
        }
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return global::Avalonia.Data.BindingOperations.DoNothing;
    }
}
