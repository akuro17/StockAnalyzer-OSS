using Avalonia.Data.Converters;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Common;
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
    public static readonly IValueConverter UrlDisplay = new UrlDisplayConverter();
}

/// <summary>
/// Formats a Note's LinkUrl for display in the attachment row (fix request): the http(s)// scheme
/// adds no information for a user scanning a card and is stripped, and an overly long URL is capped
/// so a single link can't push a card's layout wide or force the card to grow tall. Only the
/// displayed text changes - the Button's CommandParameter stays bound to the untouched original URL,
/// so OpenUrlCommand always opens the exact link the Note recorded.
/// </summary>
public class UrlDisplayConverter : IValueConverter
{
    public const int MaxDisplayLength = UrlDisplayFormatter.MaxDisplayLength;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        FormatUrlForDisplay(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();

    /// <summary>Thin XAML-binding-friendly wrapper - the actual formatting logic lives in the
    /// platform-agnostic <see cref="UrlDisplayFormatter"/> so ViewModels can call it without depending
    /// on this Avalonia <see cref="IValueConverter"/>-implementing class.</summary>
    public static string FormatUrlForDisplay(string? url, int maxLength = MaxDisplayLength) =>
        UrlDisplayFormatter.Format(url, maxLength);
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
