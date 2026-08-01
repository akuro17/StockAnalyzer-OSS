using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace StockAnalyzer.Avalonia.Converters;

public class StringAppendConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var baseStr = value?.ToString() ?? string.Empty;
        var suffix = parameter?.ToString() ?? string.Empty;
        return baseStr + suffix;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public static class StringConverters
{
    public static readonly IValueConverter Append = new StringAppendConverter();
}
