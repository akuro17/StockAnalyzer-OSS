using Avalonia.Data.Converters;
using Avalonia.Media;
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

public class SignalStatusColorConverter : IValueConverter
{
    public static readonly SignalStatusColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString();
        IBrush? successBrush = null;
        IBrush? errorBrush = null;
        IBrush? neutralBrush = null;

        if (global::Avalonia.Application.Current != null)
        {
            if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Success", null, out var successRes) && successRes is IBrush sb)
            {
                successBrush = sb;
            }
            if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Error", null, out var errorRes) && errorRes is IBrush eb)
            {
                errorBrush = eb;
            }
            if (global::Avalonia.Application.Current.TryGetResource("Brush.Semantic.Neutral", null, out var neutralRes) && neutralRes is IBrush nb)
            {
                neutralBrush = nb;
            }
        }

        successBrush ??= global::Avalonia.Media.Brushes.LimeGreen;
        errorBrush ??= global::Avalonia.Media.Brushes.Crimson;
        neutralBrush ??= global::Avalonia.Media.Brushes.Gray;

        if (status == "PASS" || status == "True") return successBrush;
        if (status == "FAIL" || status == "False") return errorBrush;
        return neutralBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SignalStatusBgConverter : IValueConverter
{
    public static readonly SignalStatusBgConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString();
        if (status == "PASS" || status == "True") return global::Avalonia.Media.Brush.Parse("#2510B981");
        if (status == "FAIL" || status == "False") return global::Avalonia.Media.Brush.Parse("#25EF4444");
        return global::Avalonia.Media.Brush.Parse("#20888888");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
