using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Converters;

public class SelectionToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isSelected = value is bool b && b;
        if (Application.Current == null) return null;

        if (isSelected)
        {
            return Application.Current.TryGetResource("Brush.Accent.Primary", null, out var accentRes) ? accentRes : null;
        }
        return Application.Current.TryGetResource("Brush.Text.Secondary", null, out var secondaryRes) ? secondaryRes : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class SelectionToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isSelected = value is bool b && b;
        return isSelected ? FontWeight.Bold : FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool val = value is bool b && b;
        return val ? 1.0 : 0.4;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class IconKeyToDataConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? key = value as string;
        if (Application.Current == null) return null;
        return Application.Current.TryGetResource(key ?? "SettingsIconData", null, out var res) ? res : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LocalizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? key = value as string;
        if (string.IsNullOrEmpty(key)) return value;
        return LocalizationManager.Instance[key] ?? key;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class EnumToLocalizedNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return null;
        string key = $"{value.GetType().Name}_{value}";
        return LocalizationManager.Instance[key] ?? value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
