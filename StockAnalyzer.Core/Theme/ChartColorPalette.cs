using StockAnalyzer.Core.Models;
using System;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Provides a fixed, perceptually distinct 12-color palette for chart comparison lines.
/// Supports both Dark and Light themes with high contrast and accessibility.
/// </summary>
public static class ChartColorPalette
{
    public const int PaletteSize = 12;
    // Dark theme colors (high visibility against dark backgrounds)
    public static readonly IndicatorColor[] DarkColors = new IndicatorColor[12]
    {
        new IndicatorColor(255, 0x5B, 0x9C, 0xF6), // 00 Blue
        new IndicatorColor(255, 0x4E, 0xCB, 0xA1), // 01 Teal
        new IndicatorColor(255, 0xF4, 0xA7, 0x42), // 02 Amber
        new IndicatorColor(255, 0xE0, 0x70, 0x70), // 03 Coral
        new IndicatorColor(255, 0xA7, 0x8B, 0xF5), // 04 Violet
        new IndicatorColor(255, 0x6E, 0xC9, 0x7A), // 05 Green
        new IndicatorColor(255, 0xF4, 0x7D, 0xB0), // 06 Pink
        new IndicatorColor(255, 0x60, 0xC8, 0xD8), // 07 Cyan
        new IndicatorColor(255, 0xE8, 0xC2, 0x4A), // 08 Yellow
        new IndicatorColor(255, 0xB0, 0x6A, 0xD4), // 09 Purple
        new IndicatorColor(255, 0x5B, 0xC4, 0xA0), // 10 Mint
        new IndicatorColor(255, 0xF0, 0x8C, 0x5A), // 11 Orange
    };

    // Light theme colors (high visibility against light backgrounds)
    public static readonly IndicatorColor[] LightColors = new IndicatorColor[12]
    {
        new IndicatorColor(255, 0x18, 0x5F, 0xA5), // 00 Blue
        new IndicatorColor(255, 0x0F, 0x6E, 0x56), // 01 Teal
        new IndicatorColor(255, 0x85, 0x4F, 0x0B), // 02 Amber
        new IndicatorColor(255, 0x99, 0x3C, 0x1D), // 03 Coral
        new IndicatorColor(255, 0x53, 0x4A, 0xB7), // 04 Violet
        new IndicatorColor(255, 0x3B, 0x6D, 0x11), // 05 Green
        new IndicatorColor(255, 0x99, 0x35, 0x56), // 06 Pink
        new IndicatorColor(255, 0x18, 0x5F, 0x6E), // 07 Cyan
        new IndicatorColor(255, 0x7A, 0x5E, 0x00), // 08 Yellow
        new IndicatorColor(255, 0x6B, 0x2D, 0x9A), // 09 Purple
        new IndicatorColor(255, 0x1A, 0x6B, 0x52), // 10 Mint
        new IndicatorColor(255, 0x8A, 0x3E, 0x12), // 11 Orange
    };

    /// <summary>
    /// Gets the color for a specific index and theme.
    /// Wraps around the 12-color palette if the index exceeds it.
    /// </summary>
    /// <param name="index">The color index (0-based).</param>
    /// <param name="isDark">True for Dark theme, False for Light theme.</param>
    /// <returns>The corresponding IndicatorColor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is negative.</exception>
    public static IndicatorColor Get(int index, bool isDark)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), index, "Color index must be non-negative.");
        
        var palette = isDark ? DarkColors : LightColors;
        return palette[index % 12];
    }
}
