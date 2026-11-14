using System;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Stateless helper for measuring and rendering rounded multiline label boxes using caller-owned Skia paints.
/// </summary>
public static class DrawingLabelBoxRenderer
{
    /// <summary>
    /// Measures the outer box size (including padding) for the specified text lines.
    /// </summary>
    public static SKSize MeasureBox(
        SKPaint textPaint,
        string[]? lines,
        float paddingX = ChartConstants.DefaultPriceLabelPadding,
        float paddingY = ChartConstants.DefaultPriceLabelPadding)
    {
        ArgumentNullException.ThrowIfNull(textPaint);

        if (lines == null || lines.Length == 0)
        {
            return SKSize.Empty;
        }

        SKSize textSize = MultilineTextRenderer.MeasureBlock(textPaint, lines);
        return new SKSize(
            textSize.Width + (paddingX * 2f),
            textSize.Height + (paddingY * 2f));
    }

    /// <summary>
    /// Draws a rounded label box centered horizontally at <paramref name="centerX"/> and anchored vertically by <paramref name="anchorY"/>.
    /// When <paramref name="placeAbove"/> is true, <paramref name="anchorY"/> is the box bottom; otherwise it is the box top.
    /// Caller owns <paramref name="backgroundPaint"/>, <paramref name="borderPaint"/>, and <paramref name="textPaint"/>.
    /// </summary>
    public static SKRect DrawCenteredBox(
        SKCanvas canvas,
        float centerX,
        float anchorY,
        bool placeAbove,
        string[]? lines,
        SKPaint backgroundPaint,
        SKPaint borderPaint,
        SKPaint textPaint,
        float cornerRadius = ChartConstants.DefaultDrawingCornerRadius,
        float paddingX = ChartConstants.DefaultPriceLabelPadding,
        float paddingY = ChartConstants.DefaultPriceLabelPadding)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(backgroundPaint);
        ArgumentNullException.ThrowIfNull(borderPaint);
        ArgumentNullException.ThrowIfNull(textPaint);

        if (lines == null || lines.Length == 0)
        {
            return SKRect.Empty;
        }

        SKSize textSize = MultilineTextRenderer.MeasureBlock(textPaint, lines);
        float boxWidth = textSize.Width + (paddingX * 2f);
        float boxHeight = textSize.Height + (paddingY * 2f);

        float left = centerX - (boxWidth * 0.5f);
        float top = placeAbove ? (anchorY - boxHeight) : anchorY;
        var boxRect = new SKRect(left, top, left + boxWidth, top + boxHeight);

        canvas.DrawRoundRect(boxRect, cornerRadius, cornerRadius, backgroundPaint);
        canvas.DrawRoundRect(boxRect, cornerRadius, cornerRadius, borderPaint);

        MultilineTextRenderer.DrawBlock(
            canvas,
            lines,
            boxRect.Left + paddingX,
            boxRect.MidY,
            textPaint,
            TextHorizontalAlignment.Center,
            textSize.Width);

        return boxRect;
    }
}
