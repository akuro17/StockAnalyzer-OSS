using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Zero-SKPath left-to-right line overlay for a completed seasonality analysis: one polyline per
/// overlaid calendar year, the x axis being year progress (left edge = 1 January, right edge =
/// 31 December) and the y axis the year-start cumulative change (rho). Structure mirrors
/// <see cref="SeasonalityPolarPlotControl"/> — layout is cached on the result reference and drawn
/// through an <see cref="ICustomDrawOperation"/> Skia lease. The Skia draw path allocates nothing
/// per paint (cached layout arrays and palette conversion); one small draw operation is
/// constructed per paint, as ChartBaseControl does. It deliberately does not draw the mean
/// seasonal path (confirmation Q3).
/// </summary>
public sealed class SeasonalityLinearPlotControl : Control
{
    private const float PlotLeftMargin = 44f;
    private const float PlotRightMargin = 12f;
    private const float PlotTopMargin = 10f;
    private const float PlotBottomMargin = 18f;
    private const float AxisLabelGap = 4f;
    private const float RecentTraceStrokeWidth = 2f;
    private const float OlderTraceStrokeWidth = 1.2f;
    private const byte OlderTraceAlphaStep = 26;
    private const byte MinimumTraceAlpha = 120;
    private const float LegendMargin = 8f;
    private const float LegendSwatchSize = 10f;
    private const float LegendRowHeight = 16f;
    private const float LegendRowExtraGap = 4f;
    private const float LegendTextGap = 5f;

    /// <summary>Rows advanced per mouse-wheel notch when scrolling the overflowing in-chart legend (decision D7).</summary>
    private const float LegendWheelRowsPerNotch = 2f;

    /// <summary>Width (DIP) of the in-chart legend column treated as its wheel-scroll hit region (decision D7).</summary>
    private const float LegendHitWidth = 160f;

    /// <summary>Alpha for an OFF year's legend row: it stays listed but greyed rather than removed (decision D6).</summary>
    private const byte LegendDimmedAlpha = 70;

    private const uint DefaultGridOpacityPercent = 60u;

    /// <summary>Fallback label size (matches the historic hard-coded <c>_labelPaint.TextSize</c>) used
    /// whenever the bound font-size property is unset (0).</summary>
    private const float DefaultLabelTextSize = 11f;

    public static readonly StyledProperty<SeasonalityChartResult?> ResultProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, SeasonalityChartResult?>(nameof(Result));

    public static readonly StyledProperty<IReadOnlyList<Color>?> YearPaletteProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, IReadOnlyList<Color>?>(nameof(YearPalette));

    public static readonly StyledProperty<IReadOnlyList<SeasonalityYearDrawState>?> YearDrawStatesProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, IReadOnlyList<SeasonalityYearDrawState>?>(nameof(YearDrawStates));

    public static readonly StyledProperty<double> LegendFontSizeProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, double>(nameof(LegendFontSize));

    public static readonly StyledProperty<double> AxisFontSizeProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, double>(nameof(AxisFontSize));

    public static readonly StyledProperty<Color> ReturnPositiveColorProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, Color>(nameof(ReturnPositiveColor));

    public static readonly StyledProperty<Color> ReturnNegativeColorProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, Color>(nameof(ReturnNegativeColor));

    public static readonly StyledProperty<Color> ReturnNeutralColorProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, Color>(nameof(ReturnNeutralColor));

    public static readonly StyledProperty<decimal?> GridRhoStepProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, decimal?>(nameof(GridRhoStep));

    public static readonly StyledProperty<uint?> GridOpacityPercentProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, uint?>(nameof(GridOpacityPercent));

    public static readonly StyledProperty<bool> GridDashedProperty =
        AvaloniaProperty.Register<SeasonalityLinearPlotControl, bool>(nameof(GridDashed));

    private readonly SKPaint _tracePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = RecentTraceStrokeWidth };
    private readonly SKPathEffect _gridDash = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0f);
    private readonly SKPaint _gridPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _axisPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _labelPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, TextSize = DefaultLabelTextSize };
    private readonly SKPaint _legendSwatchPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private SeasonalityChartResult? _layoutResult;
    private decimal? _layoutRhoStep;
    private SeasonalityLinearPlotLayout _layout;
    private IReadOnlyList<Color>? _paletteCacheKey;
    private SKColor[]? _paletteCacheValue;
    private IReadOnlyList<SeasonalityYearDrawState>? _yearStateCacheKey;
    private Dictionary<int, SeasonalityYearDrawState>? _yearStateCacheValue;

    /// <summary>
    /// UI-thread scroll offset (DIP) for the in-chart legend when it overflows the viewport (decision
    /// D7). Advanced by <see cref="OnPointerWheelChanged"/>, snapshotted into the draw operation and
    /// re-clamped on the render thread against the current viewport. Reset when the result changes.
    /// </summary>
    private float _legendScrollOffset;

    static SeasonalityLinearPlotControl()
    {
        AffectsRender<SeasonalityLinearPlotControl>(
            ResultProperty, GridRhoStepProperty, GridOpacityPercentProperty, GridDashedProperty,
            YearPaletteProperty, YearDrawStatesProperty, LegendFontSizeProperty, AxisFontSizeProperty,
            ReturnPositiveColorProperty, ReturnNegativeColorProperty, ReturnNeutralColorProperty);
    }

    public SeasonalityChartResult? Result
    {
        get => GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    /// <summary>Explicit rho gridline step; null auto-derives a 1/2/5 nice step.</summary>
    public decimal? GridRhoStep
    {
        get => GetValue(GridRhoStepProperty);
        set => SetValue(GridRhoStepProperty, value);
    }

    /// <summary>Gridline opacity, 10..100 percent. Null uses the default.</summary>
    public uint? GridOpacityPercent
    {
        get => GetValue(GridOpacityPercentProperty);
        set => SetValue(GridOpacityPercentProperty, value);
    }

    /// <summary>Draws the gridlines dashed rather than solid.</summary>
    public bool GridDashed
    {
        get => GetValue(GridDashedProperty);
        set => SetValue(GridDashedProperty, value);
    }

    /// <summary>
    /// User-configured per-year overlay colours from the Seasonality settings page (newest year =
    /// index 0). Authoritative when set; null falls back to the built-in theme palette.
    /// </summary>
    public IReadOnlyList<Color>? YearPalette
    {
        get => GetValue(YearPaletteProperty);
        set => SetValue(YearPaletteProperty, value);
    }

    /// <summary>
    /// Per-calendar-year draw state from the view model: an OFF year is skipped when drawing traces
    /// (decision D6 — nothing else changes) and each year's stroke width comes from its
    /// <see cref="SeasonalityYearDrawState.EffectiveLineThickness"/>. Null falls back to the built-in
    /// recent/older stroke widths (design-time / preview).
    /// </summary>
    public IReadOnlyList<SeasonalityYearDrawState>? YearDrawStates
    {
        get => GetValue(YearDrawStatesProperty);
        set => SetValue(YearDrawStatesProperty, value);
    }

    /// <summary>Point size for the year legend text. 0 uses the built-in default.</summary>
    public double LegendFontSize
    {
        get => GetValue(LegendFontSizeProperty);
        set => SetValue(LegendFontSizeProperty, value);
    }

    /// <summary>Point size for the month gridline labels and the rho axis labels. 0 uses the built-in default.</summary>
    public double AxisFontSize
    {
        get => GetValue(AxisFontSizeProperty);
        set => SetValue(AxisFontSizeProperty, value);
    }

    /// <summary>Colour for a positive annual-return legend value (the Data-tab "up" colour). Unset draws it in the axis text colour.</summary>
    public Color ReturnPositiveColor
    {
        get => GetValue(ReturnPositiveColorProperty);
        set => SetValue(ReturnPositiveColorProperty, value);
    }

    /// <summary>Colour for a negative annual-return legend value (the Data-tab "down" colour). Unset draws it in the axis text colour.</summary>
    public Color ReturnNegativeColor
    {
        get => GetValue(ReturnNegativeColorProperty);
        set => SetValue(ReturnNegativeColorProperty, value);
    }

    /// <summary>Colour for a flat annual-return legend value (the Data-tab "neutral" colour). Unset draws it in the axis text colour.</summary>
    public Color ReturnNeutralColor
    {
        get => GetValue(ReturnNeutralColorProperty);
        set => SetValue(ReturnNeutralColorProperty, value);
    }

    /// <summary>Avalonia colour to Skia colour, byte-for-byte. Matches the palette conversion in <see cref="ResolvePaletteSkColors"/>.</summary>
    private static SKColor ToSkColor(Color color) => new(color.R, color.G, color.B, color.A);

    private float ResolveAxisTextSize() => AxisFontSize > 0d ? (float)AxisFontSize : DefaultLabelTextSize;

    private float ResolveLegendTextSize() => LegendFontSize > 0d ? (float)LegendFontSize : DefaultLabelTextSize;

    /// <summary>
    /// The bound <see cref="YearPalette"/> converted to Skia colours, cached against the list instance
    /// so a render pass allocates nothing while the palette is unchanged. Null when no palette is set.
    /// </summary>
    private SKColor[]? ResolvePaletteSkColors()
    {
        IReadOnlyList<Color>? palette = YearPalette;
        if (palette is null || palette.Count == 0)
        {
            return null;
        }

        if (!ReferenceEquals(palette, _paletteCacheKey) || _paletteCacheValue is null || _paletteCacheValue.Length != palette.Count)
        {
            var converted = new SKColor[palette.Count];
            for (int index = 0; index < palette.Count; index++)
            {
                Color color = palette[index];
                converted[index] = new SKColor(color.R, color.G, color.B, color.A);
            }

            _paletteCacheKey = palette;
            _paletteCacheValue = converted;
        }

        return _paletteCacheValue;
    }

    /// <summary>
    /// The bound <see cref="YearDrawStates"/> as a calendar-year lookup, cached against the list
    /// instance so a render pass allocates nothing while the states are unchanged. Null when unset.
    /// </summary>
    private Dictionary<int, SeasonalityYearDrawState>? ResolveYearStateLookup()
    {
        IReadOnlyList<SeasonalityYearDrawState>? states = YearDrawStates;
        if (states is null || states.Count == 0)
        {
            return null;
        }

        if (!ReferenceEquals(states, _yearStateCacheKey) || _yearStateCacheValue is null || _yearStateCacheValue.Count != states.Count)
        {
            var map = new Dictionary<int, SeasonalityYearDrawState>(states.Count);
            for (int index = 0; index < states.Count; index++)
            {
                map[states[index].CalendarYear] = states[index];
            }

            _yearStateCacheKey = states;
            _yearStateCacheValue = map;
        }

        return _yearStateCacheValue;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        SeasonalityChartResult? result = Result;
        if (result == null || result.Traces.Count == 0)
        {
            return;
        }

        bool resultChanged = !ReferenceEquals(_layoutResult, result);
        if (resultChanged || _layoutRhoStep != GridRhoStep)
        {
            _layout = SeasonalityLinearPlotLayoutBuilder.Build(result, GridRhoStep);
            _layoutResult = result;
            _layoutRhoStep = GridRhoStep;
        }

        if (resultChanged)
        {
            // A new overlay: show the legend from the newest year again (decision D7).
            _legendScrollOffset = 0f;
        }

        // Snapshot every UI-thread-only value here: Render runs on the UI thread, but the draw
        // operation below executes on the render thread, where reading a StyledProperty
        // (YearPalette / AxisFontSize / LegendFontSize) calls VerifyAccess() and throws "Call from
        // invalid thread". That aborted every paint of a populated chart and wedged the compositor.
        // A fresh operation per paint (never equal to the previous one) mirrors ChartDrawOperation.
        var legendSignColors = new SeasonalityLegendSignColors(
            ToSkColor(ReturnPositiveColor), ToSkColor(ReturnNegativeColor), ToSkColor(ReturnNeutralColor));

        context.Custom(new SeasonalityLinearDrawOperation(
            this, new Rect(Bounds.Size), result, _layout, GridOpacityPercent ?? DefaultGridOpacityPercent, GridDashed,
            ResolvePaletteSkColors(), ResolveYearStateLookup(), ResolveAxisTextSize(), ResolveLegendTextSize(), _legendScrollOffset,
            legendSignColors));
    }

    /// <summary>
    /// Wheel over the in-chart legend column scrolls it when it overflows the viewport (decision D7);
    /// the plot itself has no other wheel gesture, so an event that does not hit an overflowing legend
    /// is left to bubble.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || !TryScrollLegend(e.GetPosition(this), e.Delta.Y))
        {
            return;
        }

        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Advances <see cref="_legendScrollOffset"/> when the legend overflows the viewport and
    /// <paramref name="pointer"/> is over the legend column. Returns false (nothing to do) when the
    /// legend fits, there is no result, the pointer is elsewhere, or the offset is already at its end.
    /// </summary>
    private bool TryScrollLegend(global::Avalonia.Point pointer, double wheelDelta)
    {
        SeasonalityChartResult? result = Result;
        int rowCount = result?.CalendarYears.Count ?? 0;
        if (rowCount == 0)
        {
            return false;
        }

        float legendTextSize = ResolveLegendTextSize();
        float rowHeight = Math.Max(LegendRowHeight, legendTextSize + LegendRowExtraGap);
        float viewportHeight = (float)Bounds.Height - (2f * LegendMargin);
        float maxScroll = Math.Max(0f, (rowCount * rowHeight) - viewportHeight);
        if (viewportHeight <= 0f || maxScroll <= 0f)
        {
            return false;
        }

        if (pointer.X < LegendMargin || pointer.X > LegendMargin + LegendHitWidth
            || pointer.Y < LegendMargin || pointer.Y > LegendMargin + viewportHeight)
        {
            return false;
        }

        float step = rowHeight * LegendWheelRowsPerNotch;
        float updated = Math.Clamp(_legendScrollOffset - ((float)wheelDelta * step), 0f, maxScroll);
        if (Math.Abs(updated - _legendScrollOffset) < float.Epsilon)
        {
            return false;
        }

        _legendScrollOffset = updated;
        return true;
    }

    // internal for the off-thread render regression test: this method and everything it calls run on
    // the render thread and must not read a StyledProperty (see the snapshot comment in Render).
    internal void RenderSkia(
        SKCanvas canvas,
        Rect bounds,
        SeasonalityChartResult result,
        SeasonalityLinearPlotLayout layout,
        uint gridOpacityPercent,
        bool gridDashed,
        SKColor[]? palette,
        IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
        float axisTextSize,
        float legendTextSize,
        float legendScrollOffset,
        SeasonalityLegendSignColors legendSignColors)
    {
        if (result.Traces.Count == 0 || bounds.Width <= 0d || bounds.Height <= 0d || !layout.HasData)
        {
            return;
        }

        float plotLeft = (float)(bounds.X + PlotLeftMargin);
        float plotRight = (float)(bounds.Right - PlotRightMargin);
        float plotTop = (float)(bounds.Y + PlotTopMargin);
        float plotBottom = (float)(bounds.Bottom - PlotBottomMargin);
        float plotWidth = plotRight - plotLeft;
        float plotHeight = plotBottom - plotTop;
        if (plotWidth <= 0f || plotHeight <= 0f)
        {
            return;
        }

        double yScale = plotHeight / layout.RhoSpan;

        global::StockAnalyzer.Avalonia.App? app = global::StockAnalyzer.Avalonia.App.Current;
        ThemeColors theme = app?.Services?.GetService<IThemeManager>()?.CurrentTheme ?? ThemeColors.Dark;

        DrawReferenceFrame(canvas, layout, plotLeft, plotRight, plotTop, plotBottom, plotWidth, yScale, theme, gridOpacityPercent, gridDashed, axisTextSize);
        DrawYearTraces(canvas, result, layout, plotLeft, plotBottom, plotWidth, yScale, theme, palette, yearStates);
        DrawLegend(canvas, layout, bounds, theme, palette, yearStates, legendTextSize, legendScrollOffset, legendSignColors);
    }

    private void DrawReferenceFrame(
        SKCanvas canvas,
        SeasonalityLinearPlotLayout layout,
        float plotLeft,
        float plotRight,
        float plotTop,
        float plotBottom,
        float plotWidth,
        double yScale,
        ThemeColors theme,
        uint gridOpacityPercent,
        bool gridDashed,
        float axisTextSize)
    {
        byte gridAlpha = (byte)(Math.Clamp(gridOpacityPercent, 10u, 100u) * 255u / 100u);
        byte axisAlpha = gridAlpha < 180 ? (byte)180 : gridAlpha;
        _gridPaint.Color = theme.AxisText.ToSkColor().WithAlpha(gridAlpha);
        _gridPaint.PathEffect = gridDashed ? _gridDash : null;
        _axisPaint.Color = theme.AxisText.ToSkColor().WithAlpha(axisAlpha);
        _labelPaint.Color = theme.AxisText.ToSkColor();
        _labelPaint.TextSize = axisTextSize;

        for (int index = 0; index < layout.GridRhoValues.Length; index++)
        {
            double rho = layout.GridRhoValues[index];
            float y = (float)(plotBottom - ((rho - layout.RhoMin) * yScale));
            if (y < plotTop || y > plotBottom)
            {
                continue;
            }

            // The year-start baseline (rho == 0) is the emphasised axis line.
            SKPaint linePaint = Math.Abs(rho) < double.Epsilon ? _axisPaint : _gridPaint;
            canvas.DrawLine(plotLeft, y, plotRight, y, linePaint);
            canvas.DrawText(layout.GridRhoLabels[index], plotLeft - AxisLabelGap - _labelPaint.MeasureText(layout.GridRhoLabels[index]), y + 4f, _labelPaint);
        }

        for (int index = 0; index < layout.MonthFractions.Length; index++)
        {
            float x = plotLeft + (float)(layout.MonthFractions[index] * plotWidth);
            canvas.DrawLine(x, plotTop, x, plotBottom, index == 0 ? _axisPaint : _gridPaint);
            canvas.DrawText(layout.MonthLabels[index], x + AxisLabelGap, plotBottom + 12f, _labelPaint);
        }
    }

    private void DrawYearTraces(
        SKCanvas canvas,
        SeasonalityChartResult result,
        SeasonalityLinearPlotLayout layout,
        float plotLeft,
        float plotBottom,
        float plotWidth,
        double yScale,
        ThemeColors theme,
        IReadOnlyList<SKColor>? palette,
        IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates)
    {
        int yearCount = result.CalendarYears.Count;
        for (int traceIndex = 0; traceIndex < result.Traces.Count; traceIndex++)
        {
            SeasonalityYearTrace trace = result.Traces[traceIndex];

            SeasonalityYearDrawState? yearState = null;
            if (yearStates is not null && yearStates.TryGetValue(trace.CalendarYear, out SeasonalityYearDrawState found))
            {
                yearState = found;
            }

            // Decision D6: an OFF year removes only its polyline; the calendar, axis and legend stay.
            if (yearState is { IsVisible: false })
            {
                continue;
            }

            int recency = SeasonalitySharedAxisFormatting.RecencyRank(result.CalendarYears, trace.CalendarYear, yearCount);
            byte alpha = (byte)Math.Max(MinimumTraceAlpha, 255 - (recency * OlderTraceAlphaStep));
            _tracePaint.Color = SeasonalitySharedAxisFormatting.ResolveYearColor(recency, palette, theme).WithAlpha(alpha);
            _tracePaint.StrokeWidth = yearState is { EffectiveLineThickness: > 0f } resolved
                ? resolved.EffectiveLineThickness
                : (recency == 0 ? RecentTraceStrokeWidth : OlderTraceStrokeWidth);

            DrawTracePolyline(canvas, trace, layout.RhoMin, plotLeft, plotBottom, plotWidth, yScale);
        }
    }

    private void DrawTracePolyline(
        SKCanvas canvas,
        SeasonalityYearTrace trace,
        double rhoMin,
        float plotLeft,
        float plotBottom,
        float plotWidth,
        double yScale)
    {
        bool hasPrevious = false;
        SKPoint previous = default;
        for (int index = 0; index < trace.Samples.Count; index++)
        {
            SeasonalitySample sample = trace.Samples[index];
            if (sample.Status != SeasonalitySampleStatus.Valid
                || sample.RateOfChange is not { } rho
                || !TryProjectLinearPoint(sample.YearFraction, (double)rho, plotLeft, plotBottom, plotWidth, rhoMin, yScale, out SKPoint point))
            {
                hasPrevious = false;
                continue;
            }

            if (hasPrevious)
            {
                canvas.DrawLine(previous.X, previous.Y, point.X, point.Y, _tracePaint);
            }

            previous = point;
            hasPrevious = true;
        }
    }

    private void DrawLegend(
        SKCanvas canvas,
        SeasonalityLinearPlotLayout layout,
        Rect bounds,
        ThemeColors theme,
        IReadOnlyList<SKColor>? palette,
        IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
        float legendTextSize,
        float legendScrollOffset,
        SeasonalityLegendSignColors legendSignColors)
    {
        if (layout.LegendEntries.Length == 0)
        {
            return;
        }

        SKColor textColor = theme.AxisText.ToSkColor();
        _labelPaint.TextSize = legendTextSize;
        float rowHeight = Math.Max(LegendRowHeight, legendTextSize + LegendRowExtraGap);
        float left = (float)bounds.X + LegendMargin;
        float top = (float)bounds.Y + LegendMargin;

        // Decision D7: the legend never grows past the viewport — it is capped to a scrollable band
        // and the wheel pans it. Nothing to clip while every row already fits.
        float viewportHeight = (float)bounds.Height - (2f * LegendMargin);
        if (viewportHeight <= 0f)
        {
            return;
        }

        float contentHeight = layout.LegendEntries.Length * rowHeight;
        float maxScroll = Math.Max(0f, contentHeight - viewportHeight);
        float scroll = Math.Clamp(legendScrollOffset, 0f, maxScroll);
        bool clip = maxScroll > 0f;
        if (clip)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(left, top, (float)bounds.Right, top + viewportHeight));
        }

        for (int index = 0; index < layout.LegendEntries.Length; index++)
        {
            float rowTop = top + (index * rowHeight) - scroll;
            if (rowTop + rowHeight < top || rowTop > top + viewportHeight)
            {
                continue; // scrolled out of the visible band
            }

            SeasonalityLegendEntry entry = layout.LegendEntries[index];

            // Decision D6: an OFF year is not removed from the legend, only greyed — it stays a
            // visible re-enable target.
            bool yearOff = yearStates is not null
                && yearStates.TryGetValue(entry.CalendarYear, out SeasonalityYearDrawState state)
                && !state.IsVisible;

            SKColor swatch = SeasonalitySharedAxisFormatting.ResolveYearColor(index, palette, theme);
            _legendSwatchPaint.Color = yearOff ? swatch.WithAlpha(LegendDimmedAlpha) : swatch;
            canvas.DrawRect(left, rowTop, LegendSwatchSize, LegendSwatchSize, _legendSwatchPaint);

            float textLeft = left + LegendSwatchSize + LegendTextGap;
            float baseline = rowTop + LegendSwatchSize;
            SKColor rowTextColor = yearOff ? textColor.WithAlpha(LegendDimmedAlpha) : textColor;

            if (string.IsNullOrEmpty(entry.AnnualReturnLabel) || legendSignColors.IsUnset)
            {
                // No annual value, or the design-time / preview path with no sign colours: one draw in
                // the plain text colour, exactly as before this feature.
                string text = string.IsNullOrEmpty(entry.AnnualReturnLabel)
                    ? entry.Label
                    : $"{entry.Label}  {entry.AnnualReturnLabel}";
                _labelPaint.Color = rowTextColor;
                canvas.DrawText(text, textLeft, baseline, _labelPaint);
            }
            else
            {
                // Improvement C-2: colour the annual return by its sign with the Data-tab bucket
                // colours; the year stays in the plain text colour. An OFF year dims both alike.
                string yearPart = $"{entry.Label}  ";
                _labelPaint.Color = rowTextColor;
                canvas.DrawText(yearPart, textLeft, baseline, _labelPaint);

                SKColor signColor = legendSignColors.ForSign(entry.Sign);
                _labelPaint.Color = yearOff ? signColor.WithAlpha(LegendDimmedAlpha) : signColor;
                canvas.DrawText(entry.AnnualReturnLabel, textLeft + _labelPaint.MeasureText(yearPart), baseline, _labelPaint);
            }
        }

        if (clip)
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// Projects a year-progress fraction (x) and a rho value (y) to a screen point. The sole
    /// decimal-to-double boundary for a trace sample is the caller's <c>(double)rho</c> cast; this
    /// method works entirely in screen (double) space.
    /// </summary>
    internal static bool TryProjectLinearPoint(
        double yearFraction,
        double rho,
        float plotLeft,
        float plotBottom,
        float plotWidth,
        double rhoMin,
        double yScale,
        out SKPoint point)
    {
        if (!double.IsFinite(yearFraction) || !double.IsFinite(rho) || !double.IsFinite(rhoMin) || !double.IsFinite(yScale))
        {
            point = default;
            return false;
        }

        double x = plotLeft + (yearFraction * plotWidth);
        double y = plotBottom - ((rho - rhoMin) * yScale);
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || x > float.MaxValue || x < float.MinValue || y > float.MaxValue || y < float.MinValue)
        {
            point = default;
            return false;
        }

        point = new SKPoint((float)x, (float)y);
        return true;
    }

    private sealed class SeasonalityLinearDrawOperation : SkiaLeaseDrawOperation
    {
        private readonly SeasonalityLinearPlotControl _owner;
        private readonly SeasonalityChartResult _result;
        private readonly SeasonalityLinearPlotLayout _layout;
        private readonly uint _gridOpacityPercent;
        private readonly bool _gridDashed;
        private readonly SKColor[]? _palette;
        private readonly IReadOnlyDictionary<int, SeasonalityYearDrawState>? _yearStates;
        private readonly float _axisTextSize;
        private readonly float _legendTextSize;
        private readonly float _legendScrollOffset;
        private readonly SeasonalityLegendSignColors _legendSignColors;

        public SeasonalityLinearDrawOperation(
            SeasonalityLinearPlotControl owner,
            Rect bounds,
            SeasonalityChartResult result,
            SeasonalityLinearPlotLayout layout,
            uint gridOpacityPercent,
            bool gridDashed,
            SKColor[]? palette,
            IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
            float axisTextSize,
            float legendTextSize,
            float legendScrollOffset,
            SeasonalityLegendSignColors legendSignColors)
            : base(bounds)
        {
            _owner = owner;
            _result = result;
            _layout = layout;
            _gridOpacityPercent = gridOpacityPercent;
            _gridDashed = gridDashed;
            _palette = palette;
            _yearStates = yearStates;
            _axisTextSize = axisTextSize;
            _legendTextSize = legendTextSize;
            _legendScrollOffset = legendScrollOffset;
            _legendSignColors = legendSignColors;
        }

        protected override void RenderCore(SKCanvas canvas, Rect bounds)
            => _owner.RenderSkia(
                canvas, bounds, _result, _layout, _gridOpacityPercent, _gridDashed, _palette, _yearStates, _axisTextSize, _legendTextSize, _legendScrollOffset,
                _legendSignColors);
    }
}
