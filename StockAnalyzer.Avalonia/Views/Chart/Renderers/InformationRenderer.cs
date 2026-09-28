using System;
using Avalonia;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Layout and formatting helper for the Information drawing tool.
/// Provides text truncation with ellipsis and viewport-clamped card positioning.
/// </summary>
public static class InformationRenderer
{
    /// <summary>
    /// Truncates text so that it fits within maxWidth when measured with paint, appending "..." if truncated.
    /// </summary>
    public static string TruncateWithEllipsis(string? text, float maxWidth, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return string.Empty;
        if (paint.MeasureText(text) <= maxWidth) return text;

        const string ellipsis = "...";
        float ellipsisWidth = paint.MeasureText(ellipsis);
        float targetWidth = maxWidth - ellipsisWidth;
        if (targetWidth <= 0f) return ellipsis;

        int low = 0;
        int high = text.Length;
        int bestLen = 0;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            float w = paint.MeasureText(text.Substring(0, mid));
            if (w <= targetWidth)
            {
                bestLen = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        string result = text.Substring(0, bestLen) + ellipsis;
        while (bestLen > 0 && paint.MeasureText(result) > maxWidth)
        {
            bestLen--;
            result = text.Substring(0, bestLen) + ellipsis;
        }

        return result;
    }

    /// <summary>
    /// Calculates the top-left pinned position for the information card, clamped safely within the chart area.
    /// </summary>
    public static SKPoint CalculateCardPosition(global::Avalonia.Rect chartArea, float cardWidth, float cardHeight)
    {
        float boxX = (float)chartArea.Left + 10f;
        float boxY = (float)chartArea.Top + 10f;

        if (boxX + cardWidth > chartArea.Right)
        {
            boxX = Math.Max((float)chartArea.Left + 5f, (float)chartArea.Right - cardWidth - 5f);
        }
        if (boxY + cardHeight > chartArea.Bottom)
        {
            boxY = Math.Max((float)chartArea.Top + 5f, (float)chartArea.Bottom - cardHeight - 5f);
        }

        return new SKPoint(boxX, boxY);
    }
}
