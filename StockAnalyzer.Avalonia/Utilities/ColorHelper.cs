using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Utilities;

/// <summary>
/// Utility class for color parsing and conversion.
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Parses a color string (hex or named) to SKColor with fallback to Gray.
    /// </summary>
    /// <param name="colorString">Color string in hex format (#RRGGBB) or named color.</param>
    /// <returns>Parsed SKColor or Gray on failure.</returns>
    public static SKColor ParseColorSafe(string? colorString)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(colorString)) return SKColors.Gray;
            
            // Use Avalonia's parser which supports names and hex
            var avaColor = Color.Parse(colorString);
            return new SKColor(avaColor.R, avaColor.G, avaColor.B, avaColor.A);
        }
        catch
        {
            // Fallback
            return SKColors.Gray;
        }
    }
    public static SKColor ParseColorSafe(StockAnalyzer.Core.Theme.HsvData hsv)
    {
        return hsv.ToIndicatorColor().ToSkColor();
    }

    /// <summary>
    /// Parses a color string to an Avalonia IBrush.
    /// </summary>
    public static IBrush ParseBrushSafe(string? colorString)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(colorString)) return new SolidColorBrush(Colors.Gray);
            var color = Color.Parse(colorString);
            return new SolidColorBrush(color);
        }
        catch
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }

    /// <summary>
    /// Converts SKColor to a hex string (#AARRGGBB).
    /// </summary>
    public static string ToHex(this SKColor color)
    {
        return $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
    }

    /// <summary>
    /// Converts IndicatorColor to a hex string (#AARRGGBB).
    /// </summary>
    public static string ToHex(this StockAnalyzer.Core.Models.IndicatorColor color)
    {
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
