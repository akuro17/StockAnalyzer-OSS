using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Common;
using AvaloniaPoint = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>A straight grid line in control coordinates.</summary>
internal readonly record struct EquityGridLine(AvaloniaPoint Start, AvaloniaPoint End);

/// <summary>A measured label with its draw origin; <see cref="Content"/> is the text (a <see cref="FormattedText"/> has no text accessor).</summary>
internal readonly record struct EquityTextPlacement(FormattedText Text, AvaloniaPoint Origin, string Content);

/// <summary>How axis labels are drawn: one definition of typeface, size and brush; the culture is always <see cref="EquityLabelFormats.Culture"/>.</summary>
internal readonly record struct EquityLabelStyle(Typeface Typeface, double FontSize, IBrush Brush)
{
    public FormattedText Format(string text) =>
        new(text, EquityLabelFormats.Culture, FlowDirection.LeftToRight, Typeface, FontSize, Brush);
}

/// <summary>
/// Grid lines and axis labels of the equity chart: round Y ticks (labels in the right margin), calendar-aligned X ticks, and the two
/// edge labels used when the visible range admits no calendar tick. Pure placement; the control only draws the result.
/// </summary>
internal static class EquityAxisLabels
{
    /// <summary>Round Y ticks over the layout's Y range: a horizontal grid line across the plot and a label per tick, left-aligned in the margin right of the plot. Empty when the range has no representable tick.</summary>
    public static void AddYAxis(
        ImmutableArray<EquityGridLine>.Builder gridLines,
        ImmutableArray<EquityTextPlacement>.Builder labels,
        EquityCurveLayout layout,
        EquityPlotArea plot,
        double controlWidth,
        EquityLabelStyle style)
    {
        double labelHeight = style.Format("0").Height;
        int maxCount = Math.Max(1, (int)Math.Floor(plot.Height / (labelHeight + BacktestUiConstants.EquityAxisLabelMinGap)) + 1);
        foreach (EquityYTick tick in EquityAxisTicks.YTicks(layout.YMin, layout.YMax, maxCount))
        {
            double y = plot.ToScreen(new EquityCurveNormalizedPoint(0d, tick.Fraction)).Y;
            gridLines.Add(new EquityGridLine(new AvaloniaPoint(plot.Left, y), new AvaloniaPoint(plot.Right, y)));
            AddYLabel(labels, tick.Label, plot.Right, y, controlWidth, style);
        }
    }

    /// <summary>
    /// Calendar-aligned X ticks over the visible range: a vertical grid line and a centered label per tick (a label is kept inside the
    /// Y label column). False, adding nothing, when the range admits no tick.
    /// </summary>
    public static bool AddXAxis(
        ImmutableArray<EquityGridLine>.Builder gridLines,
        ImmutableArray<EquityTextPlacement>.Builder labels,
        EquityPlotArea plot,
        long visibleStartTicks,
        long visibleEndTicks,
        EquityLabelStyle style)
    {
        var texts = new LabelTexts(style);
        ImmutableArray<EquityXTick> ticks = EquityAxisTicks.XTicks(
            visibleStartTicks, visibleEndTicks, plot.Width, texts.Width, BacktestUiConstants.EquityAxisLabelMinGap);
        if (ticks.IsEmpty)
        {
            return false;
        }

        double span = visibleEndTicks - visibleStartTicks;
        double labelY = plot.Bottom + BacktestUiConstants.ChartLabelGap;
        foreach (EquityXTick tick in ticks)
        {
            double x = plot.ToScreen(new EquityCurveNormalizedPoint((tick.Ticks - visibleStartTicks) / span, 0d)).X;
            gridLines.Add(new EquityGridLine(new AvaloniaPoint(x, plot.Top), new AvaloniaPoint(x, plot.Bottom)));
            FormattedText text = texts.Get(tick.Label);
            // A label never reaches into the Y label column right of the plot.
            double labelX = Math.Clamp(x - (text.Width / 2d), 0d, Math.Max(0d, plot.Right - text.Width));
            labels.Add(new EquityTextPlacement(text, new AvaloniaPoint(labelX, labelY), tick.Label));
        }
        return true;
    }

    /// <summary>The visible range's first and last instant, under the plot's two ends (one centered label for a single instant).</summary>
    public static void AddEdgeLabels(
        ImmutableArray<EquityTextPlacement>.Builder labels,
        EquityCurveLayout layout,
        EquityPlotArea plot,
        EquityLabelStyle style)
    {
        FormattedText start = style.Format(layout.ViewportStartLabel);
        double y = plot.Bottom + BacktestUiConstants.ChartLabelGap;
        if (layout.ViewportStartUtc == layout.ViewportEndUtc)
        {
            labels.Add(new EquityTextPlacement(start, new AvaloniaPoint(((plot.Left + plot.Right) / 2d) - (start.Width / 2d), y), layout.ViewportStartLabel));
            return;
        }

        labels.Add(new EquityTextPlacement(start, new AvaloniaPoint(plot.Left, y), layout.ViewportStartLabel));
        FormattedText end = style.Format(layout.ViewportEndLabel);
        labels.Add(new EquityTextPlacement(end, new AvaloniaPoint(plot.Right - end.Width, y), layout.ViewportEndLabel));
    }

    private static void AddYLabel(
        ImmutableArray<EquityTextPlacement>.Builder labels,
        string text,
        double plotRight,
        double y,
        double controlWidth,
        EquityLabelStyle style)
    {
        // A label wider than the right margin is cut with an ellipsis instead of leaving the control.
        double x = plotRight + BacktestUiConstants.ChartLabelGap;
        string fitted = EquityTextFit.Truncate(text, Math.Max(0d, controlWidth - x), candidate => style.Format(candidate).Width);
        labels.Add(new EquityTextPlacement(
            style.Format(fitted),
            new AvaloniaPoint(x, y - (style.FontSize / BacktestUiConstants.ChartLabelBaselineFactor)),
            fitted));
    }

    /// <summary>One <see cref="FormattedText"/> per distinct label of a snapshot build, shared by the tick selection (measuring) and the drawing.</summary>
    private sealed class LabelTexts
    {
        private readonly Dictionary<string, FormattedText> _texts = new();
        private readonly EquityLabelStyle _style;

        public LabelTexts(EquityLabelStyle style)
        {
            _style = style;
        }

        public FormattedText Get(string text)
        {
            if (!_texts.TryGetValue(text, out FormattedText? formatted))
            {
                formatted = _style.Format(text);
                _texts[text] = formatted;
            }
            return formatted;
        }

        public double Width(string text) => Get(text).Width;
    }
}
