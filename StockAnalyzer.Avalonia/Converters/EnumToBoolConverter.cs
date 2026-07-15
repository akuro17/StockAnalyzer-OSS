using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace StockAnalyzer.Avalonia.Converters;

public class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        string checkValue = value.ToString()!;
        string targetValue = parameter.ToString()!;

        return checkValue.Equals(targetValue, StringComparison.InvariantCultureIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue)
        {
            return parameter;
        }

        return BindingOperations.DoNothing;
    }
}
