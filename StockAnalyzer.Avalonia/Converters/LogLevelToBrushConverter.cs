using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.Converters;

public class LogLevelToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogLevel level)
        {
            return level switch
            {
                LogLevel.Fatal => Brushes.DarkRed,
                LogLevel.Error => Brushes.Red,
                LogLevel.Warning => Brushes.Orange,
                LogLevel.Information => Brushes.DeepSkyBlue,
                LogLevel.Debug => Brushes.Gray,
                LogLevel.Verbose => Brushes.LightGray,
                _ => Brushes.Transparent
            };
        }
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
