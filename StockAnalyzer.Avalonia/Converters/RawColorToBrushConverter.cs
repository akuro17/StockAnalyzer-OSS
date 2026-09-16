using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converts raw color representations (uint, SKColor, IndicatorColor) to Avalonia IBrush.
/// Used for UI-agnostic ViewModels (ZeroAllocation and Thread-Safe).
/// </summary>
public class RawColorToBrushConverter : IValueConverter
{
    public static readonly RawColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is uint u)
        {
            return new ImmutableSolidColorBrush(global::Avalonia.Media.Color.FromUInt32(u));
        }
        else if (value is IndicatorColor ic)
        {
            return new ImmutableSolidColorBrush(global::Avalonia.Media.Color.FromArgb(ic.A, ic.R, ic.G, ic.B));
        }
        else if (value is SKColor skColor)
        {
            return new ImmutableSolidColorBrush(global::Avalonia.Media.Color.FromArgb(skColor.Alpha, skColor.Red, skColor.Green, skColor.Blue));
        }
        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
