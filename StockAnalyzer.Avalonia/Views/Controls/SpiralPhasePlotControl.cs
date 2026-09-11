using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Controls;

internal enum SpiralResidualSign
{
    Unavailable,
    Negative,
    Zero,
    Positive
}

/// <summary>Zero-SKPath polar plot for a completed phase-space analysis.</summary>
public sealed class SpiralPhasePlotControl : Control
{
    private const int ModelInterpolationSegmentCount = 4;
    private const float CenterMarkerRadius = 3f;
    private const float CenterCrossHalfLength = 5f;
    private const float StartMarkerRadius = 3f;
    private const float EndMarkerRadius = 5f;
    private const float EndMarkerOutlineRadius = 6f;
    public static readonly StyledProperty<SpiralAnalysisResult?> ResultProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, SpiralAnalysisResult?>(nameof(Result));
    public static readonly StyledProperty<bool> IncludeModelRangeProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, bool>(nameof(IncludeModelRange));
    public static readonly StyledProperty<decimal?> GridPriceStepProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, decimal?>(nameof(GridPriceStep));
    public static readonly StyledProperty<uint?> GridOpacityPercentProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, uint?>(nameof(GridOpacityPercent));
    public static readonly StyledProperty<bool> GridDashedProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, bool>(nameof(GridDashed));
    public static readonly StyledProperty<string> PriceUnitProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, string>(nameof(PriceUnit), string.Empty);
    public static readonly StyledProperty<string> BarUnitProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, string>(nameof(BarUnit), string.Empty);
    public static readonly StyledProperty<bool> UseVisibleRangeOriginProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, bool>(nameof(UseVisibleRangeOrigin));
    public static readonly StyledProperty<string> DisplayBaseLabelProperty =
        AvaloniaProperty.Register<SpiralPhasePlotControl, string>(nameof(DisplayBaseLabel), string.Empty);

    private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPaint _gridPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _axisPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _markerPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _labelPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, TextSize = 11f };
    private readonly SKPathEffect _dashEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0f);
    private readonly SpiralPhaseDrawOperation _operation;
    private SpiralAnalysisResult? _layoutResult;
    private bool _layoutIncludesModelRange;
    private decimal? _layoutPriceStep;
    private string _layoutPriceUnit = string.Empty;
    private string _layoutBarUnit = string.Empty;
    private bool _layoutUsesVisibleRangeOrigin;
    private string _layoutDisplayBaseLabel = string.Empty;
    private SpiralPhasePlotLayout _layout;

    static SpiralPhasePlotControl()
    {
        AffectsRender<SpiralPhasePlotControl>(ResultProperty);
        AffectsRender<SpiralPhasePlotControl>(IncludeModelRangeProperty);
        AffectsRender<SpiralPhasePlotControl>(GridPriceStepProperty, GridOpacityPercentProperty, GridDashedProperty, PriceUnitProperty, BarUnitProperty, UseVisibleRangeOriginProperty, DisplayBaseLabelProperty);
    }

    public SpiralPhasePlotControl()
    {
        _operation = new SpiralPhaseDrawOperation(this);
    }

    public SpiralAnalysisResult? Result
    {
        get => GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    public bool IncludeModelRange
    {
        get => GetValue(IncludeModelRangeProperty);
        set => SetValue(IncludeModelRangeProperty, value);
    }
    public decimal? GridPriceStep { get => GetValue(GridPriceStepProperty); set => SetValue(GridPriceStepProperty, value); }
    public uint? GridOpacityPercent { get => GetValue(GridOpacityPercentProperty); set => SetValue(GridOpacityPercentProperty, value); }
    public bool GridDashed { get => GetValue(GridDashedProperty); set => SetValue(GridDashedProperty, value); }
    public string PriceUnit { get => GetValue(PriceUnitProperty); set => SetValue(PriceUnitProperty, value); }
    public string BarUnit { get => GetValue(BarUnitProperty); set => SetValue(BarUnitProperty, value); }
    public bool UseVisibleRangeOrigin { get => GetValue(UseVisibleRangeOriginProperty); set => SetValue(UseVisibleRangeOriginProperty, value); }
    public string DisplayBaseLabel { get => GetValue(DisplayBaseLabelProperty); set => SetValue(DisplayBaseLabelProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        SpiralAnalysisResult? result = Result;
        if (result == null || result.Samples.Count == 0) return;

        string priceUnit = PriceUnit ?? string.Empty;
        string barUnit = BarUnit ?? string.Empty;
        string displayBaseLabel = DisplayBaseLabel ?? string.Empty;
        if (!ReferenceEquals(_layoutResult, result)
            || _layoutIncludesModelRange != IncludeModelRange
            || _layoutPriceStep != GridPriceStep
            || !string.Equals(_layoutPriceUnit, priceUnit, StringComparison.Ordinal)
            || !string.Equals(_layoutBarUnit, barUnit, StringComparison.Ordinal)
            || _layoutUsesVisibleRangeOrigin != UseVisibleRangeOrigin
            || !string.Equals(_layoutDisplayBaseLabel, displayBaseLabel, StringComparison.Ordinal))
        {
            _layout = SpiralPhasePlotLayoutBuilder.Build(result, IncludeModelRange, GridPriceStep, priceUnit, barUnit, UseVisibleRangeOrigin, displayBaseLabel);
            _layoutResult = result;
            _layoutIncludesModelRange = IncludeModelRange;
            _layoutPriceStep = GridPriceStep;
            _layoutPriceUnit = priceUnit;
            _layoutBarUnit = barUnit;
            _layoutUsesVisibleRangeOrigin = UseVisibleRangeOrigin;
            _layoutDisplayBaseLabel = displayBaseLabel;
        }

        _operation.Update(new Rect(Bounds.Size), result, _layout, GridOpacityPercent ?? 60u, GridDashed);
        context.Custom(_operation);
    }

    private void RenderSkia(SKCanvas canvas, Rect bounds, SpiralAnalysisResult result, SpiralPhasePlotLayout layout, uint opacityPercent, bool dashed)
    {
        if (result.Samples.Count == 0 || bounds.Width <= 0d || bounds.Height <= 0d) return;

        if (layout.RadialSpan <= 0d) return;

        float centerX = (float)(bounds.X + (bounds.Width / 2d));
        float centerY = (float)(bounds.Y + (bounds.Height / 2d));
        double scale = (Math.Min(bounds.Width, bounds.Height) * 0.45d) / layout.RadialSpan;
        global::StockAnalyzer.Avalonia.App? app = global::StockAnalyzer.Avalonia.App.Current;
        ThemeColors theme = app?.Services?.GetService<IThemeManager>()?.CurrentTheme ?? ThemeColors.Dark;
        DrawReferenceFrame(canvas, layout, centerX, centerY, scale, theme, opacityPercent, dashed);
        DrawActualSeries(canvas, result, centerX, centerY, scale, layout.DisplayBaseRadius, theme);
        DrawSeries(canvas, result, centerX, centerY, scale, layout.DisplayBaseRadius, useModel: true, SKColors.Orange);
        DrawMarker(canvas, result, layout.StartSampleIndex, centerX, centerY, scale, layout.DisplayBaseRadius, StartMarkerRadius, SKPaintStyle.Stroke, theme, false);
        DrawMarker(canvas, result, layout.EndSampleIndex, centerX, centerY, scale, layout.DisplayBaseRadius, EndMarkerRadius, SKPaintStyle.Fill, theme, true);
    }

    private void DrawActualSeries(
        SKCanvas canvas,
        SpiralAnalysisResult result,
        float centerX,
        float centerY,
        double scale,
        double displayBaseRadius,
        ThemeColors theme)
    {
        _paint.StrokeWidth = 1.5f;
        bool hasPrevious = false;
        SKPoint previousPoint = default;
        double? previousResidual = null;
        for (int index = 0; index < result.Samples.Count; index++)
        {
            SpiralAnalysisSample sample = result.Samples[index];
            if (sample.Status != SpiralAnalysisSampleStatus.Valid
                || !TryProjectPoint(sample.Radius, sample.AngleRadians, centerX, centerY, scale, displayBaseRadius, out SKPoint point))
            {
                hasPrevious = false;
                previousResidual = null;
                continue;
            }

            if (hasPrevious)
                DrawResidualSegment(canvas, previousPoint, point, previousResidual, sample.Residual, theme);

            previousPoint = point;
            previousResidual = sample.Residual;
            hasPrevious = true;
        }
    }

    private void DrawResidualSegment(
        SKCanvas canvas,
        SKPoint start,
        SKPoint end,
        double? startResidual,
        double? endResidual,
        ThemeColors theme)
    {
        SpiralResidualSign startSign = ClassifyResidual(startResidual);
        SpiralResidualSign endSign = ClassifyResidual(endResidual);
        if (startResidual.HasValue
            && endResidual.HasValue
            && TryGetResidualCrossingFraction(startResidual.Value, endResidual.Value, out double fraction))
        {
            float crossingX = start.X + ((end.X - start.X) * (float)fraction);
            float crossingY = start.Y + ((end.Y - start.Y) * (float)fraction);
            DrawActualLine(canvas, start.X, start.Y, crossingX, crossingY, startSign, theme);
            DrawActualLine(canvas, crossingX, crossingY, end.X, end.Y, endSign, theme);
            return;
        }

        SpiralResidualSign segmentSign = startSign == SpiralResidualSign.Zero ? endSign
            : endSign == SpiralResidualSign.Zero ? startSign
            : startSign == endSign ? startSign
            : SpiralResidualSign.Unavailable;
        DrawActualLine(canvas, start.X, start.Y, end.X, end.Y, segmentSign, theme);
    }

    private void DrawActualLine(
        SKCanvas canvas,
        float startX,
        float startY,
        float endX,
        float endY,
        SpiralResidualSign sign,
        ThemeColors theme)
    {
        _paint.Color = sign switch
        {
            SpiralResidualSign.Positive => theme.SemanticPlus.ToSkColor(),
            SpiralResidualSign.Negative => theme.SemanticMinus.ToSkColor(),
            SpiralResidualSign.Zero => theme.SemanticNeutral.ToSkColor(),
            _ => SKColors.DodgerBlue
        };
        canvas.DrawLine(startX, startY, endX, endY, _paint);
    }

    private void DrawReferenceFrame(SKCanvas canvas, SpiralPhasePlotLayout layout, float centerX, float centerY, double scale, ThemeColors theme, uint opacityPercent, bool dashed)
    {
        byte alpha = (byte)(Math.Clamp(opacityPercent, 10u, 100u) * 255u / 100u);
        _gridPaint.Color = theme.AxisText.ToSkColor().WithAlpha(alpha);
        _gridPaint.PathEffect = dashed ? _dashEffect : null;
        _axisPaint.Color = theme.AxisText.ToSkColor().WithAlpha(alpha < 180 ? (byte)180 : alpha);
        _labelPaint.Color = theme.AxisText.ToSkColor();
        for (int index = 0; index < layout.GridRadii.Length; index++)
        {
            float gridRadius = (float)((layout.GridRadii[index] - layout.DisplayBaseRadius) * scale);
            canvas.DrawCircle(centerX, centerY, gridRadius, _gridPaint);
            canvas.DrawText(layout.GridLabels[index], centerX + gridRadius + 4f, centerY - 4f, _labelPaint);
        }
        float outer = (float)(layout.RadialSpan * scale);
        for (int index = 0; index < SpiralPhasePlotLayoutBuilder.RadialLineCount; index++)
        {
            double angle = Math.PI * 2d * index / SpiralPhasePlotLayoutBuilder.RadialLineCount;
            float x = centerX + (float)(outer * Math.Cos(angle));
            float y = centerY - (float)(outer * Math.Sin(angle));
            canvas.DrawLine(centerX, centerY, x, y, index % 2 == 0 ? _axisPaint : _gridPaint);
            canvas.DrawText(layout.AngleLabels[index], x, y, _labelPaint);
        }
        canvas.DrawLine(centerX - CenterCrossHalfLength, centerY, centerX + CenterCrossHalfLength, centerY, _axisPaint);
        canvas.DrawLine(centerX, centerY - CenterCrossHalfLength, centerX, centerY + CenterCrossHalfLength, _axisPaint);
        canvas.DrawCircle(centerX, centerY, CenterMarkerRadius, _axisPaint);
        canvas.DrawText(layout.CenterLabel, centerX + CenterCrossHalfLength + 2f, centerY - CenterCrossHalfLength - 2f, _labelPaint);
    }

    private void DrawMarker(
        SKCanvas canvas,
        SpiralAnalysisResult result,
        int index,
        float centerX,
        float centerY,
        double scale,
        double displayBaseRadius,
        float radius,
        SKPaintStyle style,
        ThemeColors theme,
        bool drawOutline)
    {
        if (index < 0) return;
        SpiralAnalysisSample sample = result.Samples[index];
        if (!TryProjectPoint(sample.Radius, sample.AngleRadians, centerX, centerY, scale, displayBaseRadius, out SKPoint point)) return;

        if (drawOutline)
            canvas.DrawCircle(point, EndMarkerOutlineRadius, _axisPaint);

        _markerPaint.Color = theme.Bullish.ToSkColor();
        _markerPaint.Style = style;
        canvas.DrawCircle(point, radius, _markerPaint);
    }

    private void DrawSeries(SKCanvas canvas, SpiralAnalysisResult result, float centerX, float centerY, double scale, double displayBaseRadius, bool useModel, SKColor color)
    {
        _paint.Color = color;
        _paint.StrokeWidth = useModel ? 1.0f : 1.5f;
        bool hasPrevious = false;
        float previousX = 0f;
        float previousY = 0f;
        double previousRadius = 0d;
        double previousAngleRadians = 0d;
        for (int i = 0; i < result.Samples.Count; i++)
        {
            SpiralAnalysisSample sample = result.Samples[i];
            double? radius = useModel ? sample.ModelRadius : sample.Status == SpiralAnalysisSampleStatus.Valid ? sample.Radius : null;
            if (!radius.HasValue)
            {
                hasPrevious = false;
                continue;
            }

            if (!hasPrevious)
            {
                if (!TryProjectPoint(radius.Value, sample.AngleRadians, centerX, centerY, scale, displayBaseRadius, out SKPoint point))
                    continue;

                previousX = point.X;
                previousY = point.Y;
                previousRadius = radius.Value;
                previousAngleRadians = sample.AngleRadians;
                hasPrevious = true;
                continue;
            }

            int segmentCount = useModel ? ModelInterpolationSegmentCount : 1;
            for (int segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
            {
                double fraction = (double)segmentIndex / segmentCount;
                double interpolatedRadius = InterpolateRadius(previousRadius, radius.Value, fraction, result.Parameters.Model, useModel);
                double interpolatedAngle = previousAngleRadians + ((sample.AngleRadians - previousAngleRadians) * fraction);
                if (!TryProjectPoint(interpolatedRadius, interpolatedAngle, centerX, centerY, scale, displayBaseRadius, out SKPoint point))
                {
                    hasPrevious = false;
                    break;
                }

                canvas.DrawLine(previousX, previousY, point.X, point.Y, _paint);
                previousX = point.X;
                previousY = point.Y;
            }

            if (!hasPrevious) continue;
            previousRadius = radius.Value;
            previousAngleRadians = sample.AngleRadians;
        }
    }

    internal static double InterpolateRadius(double startRadius, double endRadius, double fraction, SpiralPriceModelKind model, bool isModelSeries)
    {
        if (isModelSeries && startRadius > 0d && endRadius > 0d && model is SpiralPriceModelKind.Logarithmic or SpiralPriceModelKind.Golden)
            return startRadius * Math.Pow(endRadius / startRadius, fraction);

        return startRadius + ((endRadius - startRadius) * fraction);
    }

    internal static SpiralResidualSign ClassifyResidual(double? residual)
    {
        if (!residual.HasValue || !double.IsFinite(residual.Value)) return SpiralResidualSign.Unavailable;
        if (residual.Value > 0d) return SpiralResidualSign.Positive;
        if (residual.Value < 0d) return SpiralResidualSign.Negative;
        return SpiralResidualSign.Zero;
    }

    internal static bool TryGetResidualCrossingFraction(double startResidual, double endResidual, out double fraction)
    {
        fraction = 0d;
        if (!double.IsFinite(startResidual)
            || !double.IsFinite(endResidual)
            || !((startResidual < 0d && endResidual > 0d) || (startResidual > 0d && endResidual < 0d)))
            return false;

        double startMagnitude = Math.Abs(startResidual);
        double endMagnitude = Math.Abs(endResidual);
        double scale = Math.Max(startMagnitude, endMagnitude);
        double normalizedStart = startMagnitude / scale;
        double normalizedEnd = endMagnitude / scale;
        fraction = normalizedStart / (normalizedStart + normalizedEnd);
        return double.IsFinite(fraction) && fraction is > 0d and < 1d;
    }

    internal static bool TryProjectPoint(double radius, double angleRadians, float centerX, float centerY, double scale, out SKPoint point)
        => TryProjectPoint(radius, angleRadians, centerX, centerY, scale, 0d, out point);

    internal static bool TryProjectPoint(double radius, double angleRadians, float centerX, float centerY, double scale, double displayBaseRadius, out SKPoint point)
    {
        if (!double.IsFinite(radius) || !double.IsFinite(displayBaseRadius) || displayBaseRadius < 0d || radius < displayBaseRadius)
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

    private sealed class SpiralPhaseDrawOperation : ICustomDrawOperation
    {
        private readonly SpiralPhasePlotControl _owner;
        public SpiralPhaseDrawOperation(SpiralPhasePlotControl owner) => _owner = owner;
        public Rect Bounds { get; set; }
        private SpiralAnalysisResult? Result { get; set; }
        private SpiralPhasePlotLayout Layout { get; set; }
        private uint OpacityPercent { get; set; }
        private bool Dashed { get; set; }
        public void Update(Rect bounds, SpiralAnalysisResult result, SpiralPhasePlotLayout layout, uint opacityPercent, bool dashed)
        {
            Bounds = bounds;
            Result = result;
            Layout = layout;
            OpacityPercent = opacityPercent;
            Dashed = dashed;
        }
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        public bool HitTest(global::Avalonia.Point p) => Bounds.Contains(p);
        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            SpiralAnalysisResult? result = Result;
            if (result == null) return;
            using var lease = feature.Lease();
            _owner.RenderSkia(lease.SkCanvas, Bounds, result, Layout, OpacityPercent, Dashed);
        }
    }
}
