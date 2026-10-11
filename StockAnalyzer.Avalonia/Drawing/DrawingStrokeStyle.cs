namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Stroke style for vector drawing tools (Solid, Dash, Dot).
/// </summary>
public enum DrawingStrokeStyle
{
    Solid,
    Dash,
    Dot
}

/// <summary>
/// Extension methods for converting DrawingStrokeStyle to SkiaSharp path effects.
/// Single source of truth for vector stroke styles.
/// </summary>
public static class DrawingStrokeStyleExtensions
{
    private static readonly SkiaSharp.SKPathEffect _dashEffect = SkiaSharp.SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0f);
    private static readonly SkiaSharp.SKPathEffect _dotEffect = SkiaSharp.SKPathEffect.CreateDash(new float[] { 2f, 3f }, 0f);

    public static SkiaSharp.SKPathEffect? ToSkPathEffect(this DrawingStrokeStyle style) => style switch
    {
        DrawingStrokeStyle.Dash => _dashEffect,
        DrawingStrokeStyle.Dot => _dotEffect,
        _ => null
    };
}

