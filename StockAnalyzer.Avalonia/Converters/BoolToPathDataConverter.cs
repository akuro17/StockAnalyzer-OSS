using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Converters;

public class BoolToPathDataConverter : IValueConverter
{
    public string? ExpandedPath { get; set; }
    public string? CollapsedPath { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            var pathStr = b ? ExpandedPath : CollapsedPath;
            if (string.IsNullOrEmpty(pathStr)) return null;
            return Geometry.Parse(pathStr);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
