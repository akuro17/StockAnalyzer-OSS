using SkiaSharp;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Supported blend modes for chart drawing objects.
/// Maps directly to safe SkiaSharp SKBlendMode values.
/// </summary>
public enum DrawingBlendMode : byte
{
    Normal = 0,      // SKBlendMode.SrcOver (Default alpha blending)
    Multiply = 1,    // SKBlendMode.Multiply
    Screen = 2,      // SKBlendMode.Screen
    Overlay = 3,     // SKBlendMode.Overlay
    Darken = 4,      // SKBlendMode.Darken
    Lighten = 5,     // SKBlendMode.Lighten
    ColorDodge = 6,  // SKBlendMode.ColorDodge
    ColorBurn = 7,   // SKBlendMode.ColorBurn
    SoftLight = 8,   // SKBlendMode.SoftLight
    HardLight = 9,   // SKBlendMode.HardLight
    Difference = 10, // SKBlendMode.Difference
    Exclusion = 11   // SKBlendMode.Exclusion
}

/// <summary>
/// Extension methods for DrawingBlendMode mapping to SkiaSharp.
/// </summary>
public static class DrawingBlendModeExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SKBlendMode ToSkBlendMode(this DrawingBlendMode mode) => mode switch
    {
        DrawingBlendMode.Normal => SKBlendMode.SrcOver,
        DrawingBlendMode.Multiply => SKBlendMode.Multiply,
        DrawingBlendMode.Screen => SKBlendMode.Screen,
        DrawingBlendMode.Overlay => SKBlendMode.Overlay,
        DrawingBlendMode.Darken => SKBlendMode.Darken,
        DrawingBlendMode.Lighten => SKBlendMode.Lighten,
        DrawingBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        DrawingBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        DrawingBlendMode.SoftLight => SKBlendMode.SoftLight,
        DrawingBlendMode.HardLight => SKBlendMode.HardLight,
        DrawingBlendMode.Difference => SKBlendMode.Difference,
        DrawingBlendMode.Exclusion => SKBlendMode.Exclusion,
        _ => SKBlendMode.SrcOver
    };
}
