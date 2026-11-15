using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Data.Converters;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Converters;

public class EnumToLocalizedDisplayNameConverter : IValueConverter
{
    public static readonly EnumToLocalizedDisplayNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Enum enumValue ? ResolveDisplayText(enumValue) : value?.ToString();

    public static string ResolveDisplayText(Enum enumValue)
    {
        var key = $"Enum_{enumValue.GetType().Name}_{enumValue}";
        var localized = LocalizationManager.Instance.Get(key);
        if (!string.IsNullOrEmpty(localized) && !(localized.StartsWith("[") && localized.EndsWith("]")))
        {
            return localized;
        }

        var field = enumValue.GetType().GetField(enumValue.ToString());
        if (field != null)
        {
            var descAttr = (DescriptionAttribute?)Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute));
            if (!string.IsNullOrWhiteSpace(descAttr?.Description))
            {
                return descAttr.Description;
            }
        }

        return enumValue.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
