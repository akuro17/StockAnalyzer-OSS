using SkiaSharp;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Provides extension methods for SkiaSharp and domain color types.
/// </summary>
public static class SkiaExtensions
{
    /// <summary>
    /// Converts a domain IndicatorColor to a SkiaSharp SKColor.
    /// </summary>
    public static SKColor ToSkColor(this IndicatorColor color)
    {
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    /// <summary>
    /// Returns a new SKColor with a different Alpha component.
    /// </summary>
    public static SKColor WithAlpha(this SKColor color, byte alpha)
    {
        return new SKColor(color.Red, color.Green, color.Blue, alpha);
    }

    /// <summary>
    /// Converts a domain IndicatorColor to an Avalonia Media Color.
    /// </summary>
    public static global::Avalonia.Media.Color ToAvaloniaColor(this IndicatorColor ic)
    {
        return global::Avalonia.Media.Color.FromArgb(ic.A, ic.R, ic.G, ic.B);
    }
}
