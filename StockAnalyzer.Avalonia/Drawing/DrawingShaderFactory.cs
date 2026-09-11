using System;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Factory for creating SkiaSharp shaders for IFillableChartObject instances.
/// SSoT for gradient shader generation across 2D closed chart drawing objects.
/// </summary>
public static class DrawingShaderFactory
{
    private const float GeometryEpsilon = 1e-4f;

    /// <summary>
    /// Creates a deterministic SKShader based on the object's GradientType, colors, and coordinates.
    /// Caller MUST dispose the returned SKShader via using or Dispose().
    /// </summary>
    public static SKShader? CreateShader(IFillableChartObject fillable, SKRect rect, float p1X, float p1Y, float p2X, float p2Y)
    {
        if (fillable.GradientType == DrawingGradientType.None)
            return null;

        var startColor = new SKColor(fillable.Color.R, fillable.Color.G, fillable.Color.B, fillable.FillAlpha);
        var baseEndColor = fillable.GradientEndColor ?? fillable.Color;
        var endColor = new SKColor(baseEndColor.R, baseEndColor.G, baseEndColor.B, fillable.GradientEndAlpha);
        var colors = new[] { startColor, endColor };
        var pos = new[] { 0.0f, 1.0f };

        return fillable.GradientType switch
        {
            DrawingGradientType.LinearVertical =>
                SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Bottom), colors, pos, SKShaderTileMode.Clamp),
            DrawingGradientType.LinearHorizontal =>
                SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top), colors, pos, SKShaderTileMode.Clamp),
            DrawingGradientType.LinearDiagonal =>
                CreateDiagonalShader(p1X, p1Y, p2X, p2Y, colors, pos),
            DrawingGradientType.Radial =>
                CreateRadialShader(rect, colors, pos),
            _ => null
        };
    }

    private static SKShader? CreateDiagonalShader(float p1X, float p1Y, float p2X, float p2Y, SKColor[] colors, float[] pos)
    {
        float dx = p2X - p1X;
        float dy = p2Y - p1Y;
        if (MathF.Sqrt(dx * dx + dy * dy) <= GeometryEpsilon)
            return null;

        return SKShader.CreateLinearGradient(new SKPoint(p1X, p1Y), new SKPoint(p2X, p2Y), colors, pos, SKShaderTileMode.Clamp);
    }

    private static SKShader? CreateRadialShader(SKRect rect, SKColor[] colors, float[] pos)
    {
        var center = new SKPoint((rect.Left + rect.Right) / 2f, (rect.Top + rect.Bottom) / 2f);
        float radius = Math.Max(rect.Width, rect.Height) / 2f;
        if (radius <= GeometryEpsilon)
            return null;

        return SKShader.CreateRadialGradient(center, radius, colors, pos, SKShaderTileMode.Clamp);
    }
}
