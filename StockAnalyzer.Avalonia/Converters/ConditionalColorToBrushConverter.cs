using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Converter that takes (SKColor | IndicatorColor, bool UseCustomColor, IBrush Fallback) and returns the appropriate brush.
/// </summary>
public class ConditionalColorToBrushConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[1] is bool useCustom)
        {
            if (useCustom)
            {
                if (values[0] is IndicatorColor ic)
                {
                    return new ImmutableSolidColorBrush(global::Avalonia.Media.Color.FromArgb(ic.A, ic.R, ic.G, ic.B));
                }
                else if (values[0] is SKColor skColor)
                {
                    return new ImmutableSolidColorBrush(global::Avalonia.Media.Color.FromArgb(skColor.Alpha, skColor.Red, skColor.Green, skColor.Blue));
                }
            }
        }
        
        if (values.Count >= 3 && values[2] is IBrush fallback)
        {
            return fallback;
        }

        // Final fallback if everything fails
        return AvaloniaProperty.UnsetValue;
    }
}
