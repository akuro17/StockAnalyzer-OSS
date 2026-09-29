using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Converters;

public class EnumToLocalizedDisplayNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Enum enumValue)
        {
            var key = $"Enum_{value.GetType().Name}_{enumValue}";
            var localized = LocalizationManager.Instance[key];
            if (!string.IsNullOrEmpty(localized) && !localized.StartsWith("["))
            {
                return localized;
            }
            return enumValue.ToString();
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
