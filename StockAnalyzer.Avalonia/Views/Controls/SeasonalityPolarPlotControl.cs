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
/// Zero-SKPath polar plot for a completed seasonality analysis: a clock reference frame (twelve
/// month spokes with January at the 12 o'clock apex, concentric rho grid) with one polyline per
/// (series, calendar year). Rendering mirrors <see cref="SpiralPhasePlotControl"/> — layout is
/// cached on the result reference and drawn through an <see cref="ICustomDrawOperation"/> Skia
/// lease. The Skia draw path allocates nothing per paint (cached layout arrays and palette
/// conversion); one small draw operation is constructed per paint, as ChartBaseControl does.
/// </summary>
public sealed class SeasonalityPolarPlotControl : Control
{
    private const float CenterCrossHalfLength = 5f;
    internal const float MonthLabelGap = 4f;

    /// <summary>Multiplier approximating the font line height (ascent to descent) for radial month labels.</summary>
    internal const float FontLineHeightMultiplier = 1.35f;

    /// <summary>Safety padding (DIP) to guarantee month labels remain inside viewport bounds.</summary>
    internal const float MonthLabelSafetyPadding = 2f;

    /// <summary>|sin(spoke angle)| below this treats a spoke as horizontal (April at 3 o'clock, October
    /// at 9 o'clock). Their month labels are dropped below the horizontal diameter so they never
    /// collide with the rho grid labels that already sit just above that line (improvement C-1).</summary>
    private const float HorizontalSpokeSinEpsilon = 0.10f;

    private const float GridLabelGap = 4f;
    private const float RecentTraceStrokeWidth = 2f;
    private const float OlderTraceStrokeWidth = 1.2f;
    private const float MeanPathStrokeWidth = 2.6f;
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

    /// <summary>Fallback label size (matches the historic hard-coded <c>_labelPaint.TextSize</c>) used
    /// whenever the bound font-size property is unset (0).</summary>
    private const float DefaultLabelTextSize = 11f;

    private const uint DefaultGridOpacityPercent = 60u;

    public static readonly StyledProperty<SeasonalityChartResult?> ResultProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, SeasonalityChartResult?>(nameof(Result));

    public static readonly StyledProperty<IReadOnlyList<Color>?> YearPaletteProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, IReadOnlyList<Color>?>(nameof(YearPalette));

    public static readonly StyledProperty<IReadOnlyList<SeasonalityYearDrawState>?> YearDrawStatesProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, IReadOnlyList<SeasonalityYearDrawState>?>(nameof(YearDrawStates));

    public static readonly StyledProperty<double> LegendFontSizeProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, double>(nameof(LegendFontSize));

    public static readonly StyledProperty<double> AxisFontSizeProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, double>(nameof(AxisFontSize));

    public static readonly StyledProperty<Color> ReturnPositiveColorProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, Color>(nameof(ReturnPositiveColor));

    public static readonly StyledProperty<Color> ReturnNegativeColorProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, Color>(nameof(ReturnNegativeColor));

    public static readonly StyledProperty<Color> ReturnNeutralColorProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, Color>(nameof(ReturnNeutralColor));

    public static readonly StyledProperty<decimal?> GridRhoStepProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, decimal?>(nameof(GridRhoStep));

    public static readonly StyledProperty<uint?> GridOpacityPercentProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, uint?>(nameof(GridOpacityPercent));

    public static readonly StyledProperty<bool> GridDashedProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, bool>(nameof(GridDashed));

    public static readonly StyledProperty<bool> UseVisibleRangeOriginProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, bool>(nameof(UseVisibleRangeOrigin));

    public static readonly StyledProperty<float> EndpointMarkerRadiusProperty =
        AvaloniaProperty.Register<SeasonalityPolarPlotControl, float>(
            nameof(EndpointMarkerRadius),
            StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultSeasonalityEndpointMarkerRadius);

    private readonly SKPaint _tracePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = RecentTraceStrokeWidth };
    private readonly SKPaint _meanPathPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = MeanPathStrokeWidth };
    private readonly SKPathEffect _meanPathDash = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0f);
    private readonly SKPathEffect _gridDash = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0f);
    // Improvement C-1: the concentric rho circles and the twelve month spokes render antialiased so
    // the clock frame reads as smooth curves/lines instead of stair-stepped pixels.
    private readonly SKPaint _gridPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _axisPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _labelPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, TextSize = DefaultLabelTextSize };
    private readonly SKPaint _legendSwatchPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _endpointMarkerFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _endpointMarkerRingPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPath _traceClipPath = new();
    private SeasonalityChartResult? _layoutResult;
    private decimal? _layoutRhoStep;
    private bool _layoutUsesVisibleRangeOrigin;
    private SeasonalityPolarPlotLayout _layout;
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

    static SeasonalityPolarPlotControl()
    {
        AffectsRender<SeasonalityPolarPlotControl>(
            ResultProperty, GridRhoStepProperty, GridOpacityPercentProperty, GridDashedProperty, UseVisibleRangeOriginProperty,
            YearPaletteProperty, YearDrawStatesProperty, LegendFontSizeProperty, AxisFontSizeProperty,
            ReturnPositiveColorProperty, ReturnNegativeColorProperty, ReturnNeutralColorProperty, EndpointMarkerRadiusProperty);
    }

    public SeasonalityChartResult? Result
    {
        get => GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    public decimal? GridRhoStep
    {
        get => GetValue(GridRhoStepProperty);
        set => SetValue(GridRhoStepProperty, value);
    }

    /// <summary>Concentric grid / spoke opacity, 10..100 percent. Null uses the default.</summary>
    public uint? GridOpacityPercent
    {
        get => GetValue(GridOpacityPercentProperty);
        set => SetValue(GridOpacityPercentProperty, value);
    }

    /// <summary>Draws the concentric grid circles and minor spokes dashed rather than solid.</summary>
    public bool GridDashed
    {
        get => GetValue(GridDashedProperty);
        set => SetValue(GridDashedProperty, value);
    }

    /// <summary>Starts the radial axis at the innermost drawn radius (zooms into the data ring) instead of the centre.</summary>
    public bool UseVisibleRangeOrigin
    {
        get => GetValue(UseVisibleRangeOriginProperty);
        set => SetValue(UseVisibleRangeOriginProperty, value);
    }

    /// <summary>Radius (DIP) of the circular endpoint marker for the newest year.</summary>
    public float EndpointMarkerRadius
    {
        get => GetValue(EndpointMarkerRadiusProperty);
        set => SetValue(EndpointMarkerRadiusProperty, value);
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

    /// <summary>Point size for the month spoke labels and the rho grid labels. 0 uses the built-in default.</summary>
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
        if (resultChanged
            || _layoutRhoStep != GridRhoStep
            || _layoutUsesVisibleRangeOrigin != UseVisibleRangeOrigin)
        {
            _layout = SeasonalityPolarPlotLayoutBuilder.Build(result, GridRhoStep, UseVisibleRangeOrigin);
            _layoutResult = result;
            _layoutRhoStep = GridRhoStep;
            _layoutUsesVisibleRangeOrigin = UseVisibleRangeOrigin;
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

        context.Custom(new SeasonalityDrawOperation(
            this, new Rect(Bounds.Size), result, _layout, GridOpacityPercent ?? DefaultGridOpacityPercent, GridDashed,
            ResolvePaletteSkColors(), ResolveYearStateLookup(), ResolveAxisTextSize(), ResolveLegendTextSize(), _legendScrollOffset,
            legendSignColors, EndpointMarkerRadius));
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
        SeasonalityPolarPlotLayout layout,
        uint gridOpacityPercent,
        bool gridDashed,
        SKColor[]? palette,
        IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
        float axisTextSize,
        float legendTextSize,
        float legendScrollOffset,
        SeasonalityLegendSignColors legendSignColors,
        float endpointMarkerRadius = StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultSeasonalityEndpointMarkerRadius)
    {
        if (result.Traces.Count == 0 || bounds.Width <= 0d || bounds.Height <= 0d || layout.RadialSpan <= 0d)
        {
            return;
        }

        float centerX = (float)(bounds.X + (bounds.Width / 2d));
        float centerY = (float)(bounds.Y + (bounds.Height / 2d));

        // The trace ring is first capped at RadialFillFraction of the shorter viewport edge (a coarse
        // sanity ceiling that only binds on very large viewports) reserving the font-independent
        // rho-tick overhang past RadialSpan (BuildRhoTicks may emit a tick above rhoMax, which would
        // otherwise push the outermost ring off the viewport). The precise, font-driven cap below then
        // tightens this further so the month labels never clip regardless of Axis Font Size.
        double maxGridDisplayRadius = 0d;
        for (int index = 0; index < layout.GridRadii.Length; index++)
        {
            maxGridDisplayRadius = Math.Max(maxGridDisplayRadius, layout.GridRadii[index] - layout.DisplayBaseRadius);
        }

        double shorterEdge = Math.Min(bounds.Width, bounds.Height);
        double ringReserve = ResolvePolarRingReserve(shorterEdge, layout.RadialSpan, maxGridDisplayRadius);
        double outerRadius = ResolvePolarOuterRadius(shorterEdge, ringReserve);
        if (outerRadius <= 0d)
        {
            // Degenerate viewport (shorter than the reserved rho-tick overhang): skip the frame
            // rather than draw it at a non-positive radius.
            return;
        }

        // Ensure that even when the grid overhangs past RadialSpan (maxGridDisplayRadius > RadialSpan),
        // or on smaller viewports, the outermost ring (spokeOuter) never pushes month labels outside
        // bounds. See ResolveFontConstrainedOuterRadius for why this is applied unconditionally.
        double gridOverhangFactor = layout.RadialSpan > 0d ? Math.Max(1d, maxGridDisplayRadius / layout.RadialSpan) : 1d;
        outerRadius = ResolveFontConstrainedOuterRadius(outerRadius, shorterEdge, gridOverhangFactor, axisTextSize);
        if (outerRadius <= 0d)
        {
            return;
        }

        double scale = outerRadius / layout.RadialSpan;
        float spokeOuter = (float)(Math.Max(layout.RadialSpan, maxGridDisplayRadius) * scale);

        global::StockAnalyzer.Avalonia.App? app = global::StockAnalyzer.Avalonia.App.Current;
        ThemeColors theme = app?.Services?.GetService<IThemeManager>()?.CurrentTheme ?? ThemeColors.Dark;

        DrawReferenceFrame(canvas, layout, centerX, centerY, scale, theme, gridOpacityPercent, gridDashed, axisTextSize);

        _traceClipPath.Reset();
        _traceClipPath.AddCircle(centerX, centerY, spokeOuter);
        canvas.Save();
        canvas.ClipPath(_traceClipPath, antialias: true);
        DrawYearTraces(canvas, result, layout, centerX, centerY, scale, theme, palette, yearStates, out SKPoint? endpointMarkerPos, out SKColor? endpointMarkerColor);
        DrawMeanPaths(canvas, result, layout, centerX, centerY, scale, theme, palette);
        canvas.Restore();

        if (endpointMarkerPos is { } markerPoint && endpointMarkerColor is { } markerColor)
        {
            DrawEndpointMarker(canvas, markerPoint, markerColor, theme, endpointMarkerRadius);
        }

        DrawLegend(canvas, layout, bounds, theme, palette, yearStates, legendTextSize, legendScrollOffset, legendSignColors);
    }

    private void DrawReferenceFrame(
        SKCanvas canvas,
        SeasonalityPolarPlotLayout layout,
        float centerX,
        float centerY,
        double scale,
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

        // The rho grid labels sit just above the horizontal (April / October) spoke. Lift the baseline
        // by the font descent plus GridLabelGap so the glyph bottom keeps a constant clear gap above
        // that line at every axis font size instead of touching it. FontMetrics is a struct (no alloc).
        float rhoLabelDescent = _labelPaint.FontMetrics.Descent;

        float outer = (float)(layout.RadialSpan * scale);
        float maxRingRadius = outer;
        for (int index = 0; index < layout.GridRadii.Length; index++)
        {
            float gridRadius = (float)((layout.GridRadii[index] - layout.DisplayBaseRadius) * scale);
            if (gridRadius <= 0f)
            {
                continue;
            }

            canvas.DrawCircle(centerX, centerY, gridRadius, _gridPaint);
            canvas.DrawText(
                layout.GridLabels[index],
                centerX + gridRadius + GridLabelGap,
                centerY - GridLabelGap - rhoLabelDescent,
                _labelPaint);
            maxRingRadius = Math.Max(maxRingRadius, gridRadius);
        }

        // The month boundary spokes stop where they meet the outermost drawn rho ring — they are not
        // drawn past it. The month labels are anchored just beyond that ring.
        float spokeOuter = maxRingRadius;
        float labelRadius = spokeOuter + MonthLabelGap;
        for (int index = 0; index < layout.MonthAnglesRadians.Length; index++)
        {
            double angle = layout.MonthAnglesRadians[index];
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);

            canvas.DrawLine(
                centerX, centerY, centerX + (spokeOuter * cos), centerY - (spokeOuter * sin),
                index == 0 ? _axisPaint : _gridPaint);
            DrawRadialMonthLabel(canvas, layout.MonthLabels[index], cos, sin, centerX, centerY, labelRadius);
        }

        canvas.DrawLine(centerX - CenterCrossHalfLength, centerY, centerX + CenterCrossHalfLength, centerY, _axisPaint);
        canvas.DrawLine(centerX, centerY - CenterCrossHalfLength, centerX, centerY + CenterCrossHalfLength, _axisPaint);
    }

    /// <summary>
    /// Draws one month label anchored on its spoke at <paramref name="radius"/> from the centre. Both
    /// the horizontal alignment and the vertical baseline shift scale with the spoke direction
    /// (<paramref name="cos"/> / <paramref name="sin"/>) so the label always sits just outside the
    /// anchor: left-aligned to the right, right-aligned to the left, centred straight up or down
    /// (decision C3). <see cref="_labelPaint"/> must already carry the axis text size and colour.
    /// </summary>
    private void DrawRadialMonthLabel(SKCanvas canvas, string label, float cos, float sin, float centerX, float centerY, float radius)
    {
        float width = _labelPaint.MeasureText(label);
        SKFontMetrics metrics = _labelPaint.FontMetrics;
        SKRect rect = MeasureRadialMonthLabelRect(cos, sin, radius, centerX, centerY, width, metrics.Ascent, metrics.Descent);
        canvas.DrawText(label, rect.Left, rect.Bottom - metrics.Descent, _labelPaint);
    }

    private void DrawYearTraces(
        SKCanvas canvas,
        SeasonalityChartResult result,
        SeasonalityPolarPlotLayout layout,
        float centerX,
        float centerY,
        double scale,
        ThemeColors theme,
        IReadOnlyList<SKColor>? palette,
        IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
        out SKPoint? currentYearEndpoint,
        out SKColor? currentYearColor)
    {
        int yearCount = result.CalendarYears.Count;
        currentYearEndpoint = null;
        currentYearColor = null;

        for (int traceIndex = 0; traceIndex < result.Traces.Count; traceIndex++)
        {
            SeasonalityYearTrace trace = result.Traces[traceIndex];

            SeasonalityYearDrawState? yearState = null;
            if (yearStates is not null && yearStates.TryGetValue(trace.CalendarYear, out SeasonalityYearDrawState found))
            {
                yearState = found;
            }

            // Decision D6: an OFF year removes only its polyline; the calendar, axis, mean path and
            // legend are untouched.
            if (yearState is { IsVisible: false })
            {
                continue;
            }

            // One series now: the hue keys off the year's recency (newest = slot 0), matching the
            // year legend; alpha still fades with age.
            int recency = SeasonalitySharedAxisFormatting.RecencyRank(result.CalendarYears, trace.CalendarYear, yearCount);
            SKColor baseHue = SeasonalitySharedAxisFormatting.ResolveYearColor(recency, palette, theme);
            byte alpha = (byte)Math.Max(MinimumTraceAlpha, 255 - (recency * OlderTraceAlphaStep));
            _tracePaint.Color = baseHue.WithAlpha(alpha);
            _tracePaint.StrokeWidth = yearState is { EffectiveLineThickness: > 0f } resolved
                ? resolved.EffectiveLineThickness
                : (recency == 0 ? RecentTraceStrokeWidth : OlderTraceStrokeWidth);

            SKPoint? endpoint = DrawTracePolyline(canvas, trace, layout.DisplayBaseRadius, centerX, centerY, scale);
            if (recency == 0 && endpoint.HasValue)
            {
                currentYearEndpoint = endpoint;
                currentYearColor = baseHue;
            }
        }
    }

    /// <summary>
    /// Draws the averaged seasonal trajectory for each series (present only when the analysis was run
    /// with <see cref="SeasonalityChartParameters.IncludeMeanPath"/>): the series hue at full opacity,
    /// thicker and dashed so it reads as an aggregate distinct from the individual year traces.
    /// </summary>
    private void DrawMeanPaths(
        SKCanvas canvas,
        SeasonalityChartResult result,
        SeasonalityPolarPlotLayout layout,
        float centerX,
        float centerY,
        double scale,
        ThemeColors theme,
        IReadOnlyList<SKColor>? palette)
    {
        if (result.MeanPaths.Count == 0)
        {
            return;
        }

        _meanPathPaint.PathEffect = _meanPathDash;
        int overlayDepth = result.CalendarYears.Count;
        for (int pathIndex = 0; pathIndex < result.MeanPaths.Count; pathIndex++)
        {
            SeasonalityMeanPath meanPath = result.MeanPaths[pathIndex];
            // The mean path is an across-years aggregate; draw it in the newest-year hue, set apart by
            // being thicker and dashed. Each segment's opacity tracks how many overlaid years back it
            // (SeasonalityMeanPathSample.SampleCount, weakest of the two ends) so thinly-supported
            // stretches read as tentative.
            SKColor baseHue = SeasonalitySharedAxisFormatting.ResolveYearColor(0, palette, theme);

            bool hasPrevious = false;
            SKPoint previous = default;
            int previousSampleCount = 0;
            for (int index = 0; index < meanPath.Samples.Count; index++)
            {
                SeasonalityMeanPathSample sample = meanPath.Samples[index];
                if (sample.Status != SeasonalitySampleStatus.Valid
                    || !TryProjectPoint(sample.Radius, sample.PlotAngleRadians, centerX, centerY, scale, layout.DisplayBaseRadius, out SKPoint point))
                {
                    hasPrevious = false;
                    continue;
                }

                if (hasPrevious)
                {
                    byte segmentAlpha = SeasonalitySharedAxisFormatting.MeanPathConfidenceAlpha(
                        Math.Min(previousSampleCount, sample.SampleCount), overlayDepth);
                    _meanPathPaint.Color = baseHue.WithAlpha(segmentAlpha);
                    canvas.DrawLine(previous.X, previous.Y, point.X, point.Y, _meanPathPaint);
                }

                previous = point;
                previousSampleCount = sample.SampleCount;
                hasPrevious = true;
            }
        }
    }

    private void DrawLegend(
        SKCanvas canvas,
        SeasonalityPolarPlotLayout layout,
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

    private SKPoint? DrawTracePolyline(
        SKCanvas canvas,
        SeasonalityYearTrace trace,
        double displayBaseRadius,
        float centerX,
        float centerY,
        double scale)
    {
        bool hasPrevious = false;
        SKPoint previous = default;
        for (int index = 0; index < trace.Samples.Count; index++)
        {
            SeasonalitySample sample = trace.Samples[index];
            if (sample.Status != SeasonalitySampleStatus.Valid
                || !TryProjectPoint(sample.Radius, sample.PlotAngleRadians, centerX, centerY, scale, displayBaseRadius, out SKPoint point))
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

        return hasPrevious ? previous : null;
    }

    /// <summary>
    /// Draws a circular endpoint marker at the latest valid sample of the newest year trace (Mode Polar Clock only).
    /// Features a background-colored halo ring so it cleanly stands out when crossing other traces.
    /// </summary>
    private void DrawEndpointMarker(SKCanvas canvas, SKPoint endpoint, SKColor fillColor, ThemeColors theme, float markerRadius)
    {
        // Background-colored halo ring to separate marker from underlying year lines
        _endpointMarkerRingPaint.Color = theme.ChartBackground.ToSkColor();
        float ringWidth = Math.Max(1.0f, markerRadius * 0.33f);
        _endpointMarkerRingPaint.StrokeWidth = ringWidth;
        canvas.DrawCircle(endpoint.X, endpoint.Y, markerRadius + (ringWidth * 0.5f), _endpointMarkerRingPaint);

        // Solid colored circle using the newest year's full-opacity hue
        _endpointMarkerFillPaint.Color = fillColor;
        canvas.DrawCircle(endpoint.X, endpoint.Y, markerRadius, _endpointMarkerFillPaint);
    }

    /// <summary>
    /// Projects a data-unit radius and an already clock-mapped angle to a screen point. The angle
    /// carries the January-apex clockwise convention from the engine
    /// (<c>theta = pi/2 - 2*pi*u</c>); this method only applies the radial offset, the pixel scale
    /// and the y-down flip. Re-implemented in this feature so the shared spiral projection stays
    /// untouched (NFR-06).
    /// </summary>
    internal static bool TryProjectPoint(
        double radius,
        double angleRadians,
        float centerX,
        float centerY,
        double scale,
        double displayBaseRadius,
        out SKPoint point)
    {
        if (!double.IsFinite(radius)
            || !double.IsFinite(angleRadians)
            || !double.IsFinite(displayBaseRadius)
            || displayBaseRadius < 0d
            || radius < displayBaseRadius)
        {
            point = default;
            return false;
        }

        double displayRadius = radius - displayBaseRadius;
        double x = centerX + (displayRadius * Math.Cos(angleRadians) * scale);
        double y = centerY - (displayRadius * Math.Sin(angleRadians) * scale);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x > float.MaxValue || x < float.MinValue || y > float.MaxValue || y < float.MinValue)
        {
            point = default;
            return false;
        }

        point = new SKPoint((float)x, (float)y);
        return true;
    }

    /// <summary>
    /// Outer trace-ring radius (DIP): the smaller of the historic fill-fraction radius and the
    /// radius that still leaves <paramref name="monthLabelReserve"/> of clearance to the viewport
    /// edge for the month labels (decision C3). Non-positive when the viewport is too small for the
    /// labels to fit without overlapping the data area, in which case the caller skips the frame
    /// instead of drawing a degenerate circle.
    /// </summary>
    internal static double ResolvePolarOuterRadius(double shorterViewportEdge, double monthLabelReserve)
    {
        double fillRadius = shorterViewportEdge * SeasonalityPolarPlotLayoutConstants.RadialFillFraction;
        double labelFitRadius = (shorterViewportEdge / 2d) - monthLabelReserve;
        return Math.Min(fillRadius, labelFitRadius);
    }

    /// <summary>
    /// Radial clearance (DIP) reserved past the trace ring before the viewport edge. This coarse,
    /// font-independent reserve only accounts for the rho-tick overhang past <paramref name="radialSpan"/>
    /// (<see cref="SeasonalitySharedAxisFormatting"/>'s BuildRhoTicks can emit a tick above rhoMax) plus
    /// one <see cref="MonthLabelGap"/> — it is not itself where month-label clipping is prevented. That
    /// is instead the job of <see cref="ResolveFontConstrainedOuterRadius"/>, applied unconditionally
    /// after this reserve in <see cref="RenderSkia"/>, which tightens the ring further by the actual
    /// font metrics so month labels are never clipped by the plot bounds at any Axis Font Size. Shared
    /// with the layout tests so the rule is not duplicated.
    /// </summary>
    internal static double ResolvePolarRingReserve(double shorterViewportEdge, double radialSpan, double maxGridDisplayRadius)
    {
        double overhangFactor = radialSpan > 0d ? Math.Max(0d, (maxGridDisplayRadius / radialSpan) - 1d) : 0d;
        return (shorterViewportEdge * SeasonalityPolarPlotLayoutConstants.RadialFillFraction * overhangFactor) + MonthLabelGap;
    }

    /// <summary>
    /// Tightens <paramref name="outerRadius"/> so the trace ring plus the font-driven month-label
    /// margin (<see cref="MonthLabelGap"/> + line-height-scaled <paramref name="axisTextSize"/> +
    /// <see cref="MonthLabelSafetyPadding"/>) never exceeds the viewport's half-edge. Applied
    /// unconditionally (not only when the resulting cap is positive): a viewport too small for the
    /// font-based margin must still drive the result non-positive so the caller's degenerate-radius
    /// check bails out, rather than silently skip this cap and let the reference frame overflow the
    /// viewport. <paramref name="gridOverhangFactor"/> is the same rho-tick-overshoot factor used by
    /// <see cref="ResolvePolarRingReserve"/>, so the two caps compose consistently. Shared with the
    /// layout tests so the rule is not duplicated.
    /// </summary>
    internal static double ResolveFontConstrainedOuterRadius(double outerRadius, double shorterViewportEdge, double gridOverhangFactor, float axisTextSize)
    {
        float requiredLabelMargin = MonthLabelGap + (axisTextSize * FontLineHeightMultiplier) + MonthLabelSafetyPadding;
        double maxAllowedSpokeOuter = (shorterViewportEdge / 2d) - requiredLabelMargin;
        return Math.Min(outerRadius, maxAllowedSpokeOuter / gridOverhangFactor);
    }

    /// <summary>
    /// Screen rectangle a month label occupies when anchored on its spoke at <paramref name="radius"/>
    /// from the centre, using the same direction-proportional horizontal alignment and vertical
    /// baseline correction as <see cref="DrawRadialMonthLabel"/> (decision C3). Exposed for the layout
    /// tests that assert all twelve rectangles stay inside the viewport. <paramref name="ascent"/> is
    /// negative (Skia convention), <paramref name="descent"/> positive.
    /// </summary>
    internal static SKRect MeasureRadialMonthLabelRect(
        float cos,
        float sin,
        float radius,
        float centerX,
        float centerY,
        float width,
        float ascent,
        float descent)
    {
        float halfHeight = (descent - ascent) * 0.5f;
        float baselineMidShift = -(ascent + descent) * 0.5f;
        float anchorX = centerX + (radius * cos);
        float anchorY = centerY - (radius * sin);
        float left = anchorX - (width * 0.5f) + (cos * width * 0.5f);

        // Improvement C-1: April / October lie on the horizontal diameter, where the rho grid labels
        // already run just above the line. Drop only those two labels fully below the line (text top
        // at anchorY + MonthLabelGap) so the two never overlap; every other spoke keeps the
        // centred/radial baseline from decision C3.
        if (Math.Abs(sin) < HorizontalSpokeSinEpsilon)
        {
            float belowBaseline = anchorY + MonthLabelGap - ascent;
            return new SKRect(left, belowBaseline + ascent, left + width, belowBaseline + descent);
        }

        float baseline = anchorY + baselineMidShift - (sin * halfHeight);
        return new SKRect(left, baseline + ascent, left + width, baseline + descent);
    }

    private sealed class SeasonalityDrawOperation : SkiaLeaseDrawOperation
    {
        private readonly SeasonalityPolarPlotControl _owner;
        private readonly SeasonalityChartResult _result;
        private readonly SeasonalityPolarPlotLayout _layout;
        private readonly uint _gridOpacityPercent;
        private readonly bool _gridDashed;
        private readonly SKColor[]? _palette;
        private readonly IReadOnlyDictionary<int, SeasonalityYearDrawState>? _yearStates;
        private readonly float _axisTextSize;
        private readonly float _legendTextSize;
        private readonly float _legendScrollOffset;
        private readonly SeasonalityLegendSignColors _legendSignColors;
        private readonly float _endpointMarkerRadius;

        public SeasonalityDrawOperation(
            SeasonalityPolarPlotControl owner,
            Rect bounds,
            SeasonalityChartResult result,
            SeasonalityPolarPlotLayout layout,
            uint gridOpacityPercent,
            bool gridDashed,
            SKColor[]? palette,
            IReadOnlyDictionary<int, SeasonalityYearDrawState>? yearStates,
            float axisTextSize,
            float legendTextSize,
            float legendScrollOffset,
            SeasonalityLegendSignColors legendSignColors,
            float endpointMarkerRadius)
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
            _endpointMarkerRadius = endpointMarkerRadius;
        }

        protected override void RenderCore(SKCanvas canvas, Rect bounds)
            => _owner.RenderSkia(
                canvas, bounds, _result, _layout, _gridOpacityPercent, _gridDashed, _palette, _yearStates, _axisTextSize, _legendTextSize, _legendScrollOffset,
                _legendSignColors, _endpointMarkerRadius);
    }
}
