using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.Converters;

public class ComparisonOperatorConverter : IValueConverter
{
    public static readonly ComparisonOperatorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ComparisonOperator op)
        {
            return op.ToSymbolString();
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
