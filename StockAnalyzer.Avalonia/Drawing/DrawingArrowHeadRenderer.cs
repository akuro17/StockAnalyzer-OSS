using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Stateless helper for rendering filled triangular arrowheads using caller-owned Skia resources.
/// </summary>
public static class DrawingArrowHeadRenderer
{
    private const float ArrowWingAngleRadians = MathF.PI / 6f;

    /// <summary>
    /// Draws a filled triangular arrowhead at <paramref name="tip"/> pointing along the direction from <paramref name="tail"/> to <paramref name="tip"/>.
    /// Caller owns <paramref name="reusablePath"/> and <paramref name="fillPaint"/> so no Skia objects are allocated per frame.
    /// </summary>
    public static void DrawFilledHead(
        SKCanvas canvas,
        SKPoint tail,
        SKPoint tip,
        SKPath reusablePath,
        SKPaint fillPaint,
        float headSize = ChartConstants.ArrowHeadSize)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(reusablePath);
        ArgumentNullException.ThrowIfNull(fillPaint);

        float dx = tip.X - tail.X;
        float dy = tip.Y - tail.Y;
        if ((dx * dx) + (dy * dy) < 1e-4f || headSize <= 0f)
        {
            return;
        }

        float angle = MathF.Atan2(dy, dx);
        float p1X = tip.X - (headSize * MathF.Cos(angle - ArrowWingAngleRadians));
        float p1Y = tip.Y - (headSize * MathF.Sin(angle - ArrowWingAngleRadians));
        float p2X = tip.X - (headSize * MathF.Cos(angle + ArrowWingAngleRadians));
        float p2Y = tip.Y - (headSize * MathF.Sin(angle + ArrowWingAngleRadians));

        reusablePath.Rewind();
        reusablePath.MoveTo(tip.X, tip.Y);
        reusablePath.LineTo(p1X, p1Y);
        reusablePath.LineTo(p2X, p2Y);
        reusablePath.Close();

        canvas.DrawPath(reusablePath, fillPaint);
    }
}
