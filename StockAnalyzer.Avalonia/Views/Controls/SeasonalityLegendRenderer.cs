using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// The in-chart year legend shared by <see cref="SeasonalityLinearPlotControl"/> and <see cref="SeasonalityPolarPlotControl"/>:
/// its geometry constants, the wheel-scroll arithmetic (decision D7) and the Skia drawing (decisions D6 and C-2). The two controls
/// differ only in the right edge the legend is clipped to, which the caller passes in. <see cref="Draw"/> runs on the render thread:
/// it reads no StyledProperty and no control field, only the snapshot values it is given.
/// </summary>
internal static class SeasonalityLegendRenderer
{
    internal const float LegendMargin = 8f;
    internal const float LegendSwatchSize = 10f;
    internal const float LegendRowHeight = 16f;
    internal const float LegendRowExtraGap = 4f;
    internal const float LegendTextGap = 5f;

    /// <summary>Rows advanced per mouse-wheel notch when scrolling the overflowing in-chart legend (decision D7).</summary>
    internal const float LegendWheelRowsPerNotch = 2f;

    /// <summary>Width (DIP) of the in-chart legend column treated as its wheel-scroll hit region (decision D7).</summary>
    internal const float LegendHitWidth = 160f;

    /// <summary>Alpha for an OFF year's legend row: it stays listed but greyed rather than removed (decision D6).</summary>
    internal const byte LegendDimmedAlpha = 70;

    /// <summary>Height of the band the legend may use: the bounds minus the margin above and below.</summary>
    private static float ResolveViewportHeight(double boundsHeight) => (float)boundsHeight - (2f * LegendMargin);

    /// <summary>
    /// Advances the legend scroll offset when the legend overflows the viewport and the pointer is over the legend column.
    /// Returns false (nothing to do, <paramref name="updatedOffset"/> = <paramref name="currentOffset"/>) when there are no rows, the
    /// legend fits, the pointer is elsewhere, or the offset is already at its end.
    /// </summary>
    internal static bool TryComputeScrollOffset(
        int rowCount,
        float legendTextSize,
        double boundsHeight,
        double pointerX,
        double pointerY,
        double wheelDelta,
        float currentOffset,
        out float updatedOffset)
    {
        updatedOffset = currentOffset;
        if (rowCount == 0)
        {
            return false;
        }

        float rowHeight = Math.Max(LegendRowHeight, legendTextSize + LegendRowExtraGap);
        float viewportHeight = ResolveViewportHeight(boundsHeight);
        float maxScroll = Math.Max(0f, (rowCount * rowHeight) - viewportHeight);
        if (viewportHeight <= 0f || maxScroll <= 0f)
        {
            return false;
        }

        if (pointerX < LegendMargin || pointerX > LegendMargin + LegendHitWidth
            || pointerY < LegendMargin || pointerY > LegendMargin + viewportHeight)
        {
            return false;
        }

        float step = rowHeight * LegendWheelRowsPerNotch;
        float updated = Math.Clamp(currentOffset - ((float)wheelDelta * step), 0f, maxScroll);
        if (Math.Abs(updated - currentOffset) < float.Epsilon)
        {
            return false;
        }

        updatedOffset = updated;
        return true;
    }

    /// <summary>
    /// Draws the legend column. <paramref name="clipRight"/> is the right edge the legend (and any annual return label) may not pass:
    /// the caller supplies <c>bounds.Right</c> or a smaller value. <paramref name="labelPaint"/> receives the legend text size and colors.
    /// </summary>
    internal static void Draw(
        SKCanvas canvas,
        ReadOnlySpan<SeasonalityLegendEntry> entries,
        Rect bounds,
        float clipRight,
        ThemeColors theme,
        IReadOnlyList<SKColor>? palette,
        IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
        float legendTextSize,
        float legendScrollOffset,
        SeasonalityLegendSignColors legendSignColors,
        SKPaint labelPaint,
        SKPaint swatchPaint)
    {
        if (entries.Length == 0)
        {
            return;
        }

        SKColor textColor = theme.AxisText.ToSkColor();
        labelPaint.TextSize = legendTextSize;
        float rowHeight = Math.Max(LegendRowHeight, legendTextSize + LegendRowExtraGap);
        float left = (float)bounds.X + LegendMargin;
        float top = (float)bounds.Y + LegendMargin;

        // Decision D7: the legend never grows past the viewport -- it is capped to a scrollable band
        // and the wheel pans it. Nothing to clip while every row already fits.
        float viewportHeight = ResolveViewportHeight(bounds.Height);
        if (viewportHeight <= 0f)
        {
            return;
        }

        float contentHeight = entries.Length * rowHeight;
        float maxScroll = Math.Max(0f, contentHeight - viewportHeight);
        float scroll = Math.Clamp(legendScrollOffset, 0f, maxScroll);
        bool clip = maxScroll > 0f || clipRight < (float)bounds.Right;
        if (clip)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(left, top, clipRight, top + viewportHeight));
        }

        for (int index = 0; index < entries.Length; index++)
        {
            float rowTop = top + (index * rowHeight) - scroll;
            if (rowTop + rowHeight < top || rowTop > top + viewportHeight)
            {
                continue; // scrolled out of the visible band
            }

            SeasonalityLegendEntry entry = entries[index];

            // Decision D6: an OFF year is not removed from the legend, only greyed -- it stays a
            // visible re-enable target.
            bool yearOff = yearStates is not null
                && yearStates.TryGetValue(entry.CalendarYear, out SeasonalityYearDrawState state)
                && !state.IsVisible;

            SKColor swatch = SeasonalitySharedAxisFormatting.ResolveYearColor(index, palette, theme);
            swatchPaint.Color = yearOff ? swatch.WithAlpha(LegendDimmedAlpha) : swatch;
            canvas.DrawRect(left, rowTop, LegendSwatchSize, LegendSwatchSize, swatchPaint);

            float textLeft = left + LegendSwatchSize + LegendTextGap;
            float baseline = rowTop + LegendSwatchSize;
            SKColor rowTextColor = yearOff ? textColor.WithAlpha(LegendDimmedAlpha) : textColor;

            if (string.IsNullOrEmpty(entry.AnnualReturnLabel) || legendSignColors.IsUnset)
            {
                // No annual value, or the design-time / preview path with no sign colours: one draw in
                // the plain text colour.
                string text = string.IsNullOrEmpty(entry.AnnualReturnLabel)
                    ? entry.Label
                    : $"{entry.Label}  {entry.AnnualReturnLabel}";
                labelPaint.Color = rowTextColor;
                canvas.DrawText(text, textLeft, baseline, labelPaint);
            }
            else
            {
                // Improvement C-2: colour the annual return by its sign with the Data-tab bucket
                // colours; the year stays in the plain text colour. An OFF year dims both alike.
                string yearPart = $"{entry.Label}  ";
                labelPaint.Color = rowTextColor;
                canvas.DrawText(yearPart, textLeft, baseline, labelPaint);

                SKColor signColor = legendSignColors.ForSign(entry.Sign);
                labelPaint.Color = yearOff ? signColor.WithAlpha(LegendDimmedAlpha) : signColor;
                canvas.DrawText(entry.AnnualReturnLabel, textLeft + labelPaint.MeasureText(yearPart), baseline, labelPaint);
            }
        }

        if (clip)
        {
            canvas.Restore();
        }
    }
}
