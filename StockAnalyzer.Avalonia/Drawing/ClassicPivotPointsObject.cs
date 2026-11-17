using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Classic Pivot Points Drawing Tool.
/// Computes 7 classic support/resistance levels (P, R1, S1, R2, S2, HBOP/R3, LBOP/S3)
/// from the preceding period's High, Low, Close, and renders them as horizontal period lines
/// alongside a solid vertical anchor reference line.
/// Follows Strict ZeroAllocation rendering principle using cached reusable Skia paints.
/// </summary>
public class ClassicPivotPointsObject : IChartObject, IDrawingCalculatedValuesProvider, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.ClassicPivotPoints;
    public string? CustomName { get; set; }
    public List<ChartPoint> Points { get; private set; }

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;
    public int PanelIndex { get; set; } = -1;
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;

    // --- Configuration Parameters for DrawingParameterViewBuilder ---

    [Category("Parameters")]
    [DisplayName("Period")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public PivotPeriod Period { get; set; } = PivotPeriod.Day;

    [Category("Parameters")]
    [DisplayName("Extend Lines")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public PivotLineExtendMode ExtendMode { get; set; } = PivotLineExtendMode.None;

    [Category("Parameters")]
    [DisplayName("Show P")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowP { get; set; } = true;

    [Category("Parameters")]
    [DisplayName("Show R1")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowR1 { get; set; } = true;

    [Category("Parameters")]
    [DisplayName("Show S1")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowS1 { get; set; } = true;

    [Category("Parameters")]
    [DisplayName("Show R2")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowR2 { get; set; } = true;

    [Category("Parameters")]
    [DisplayName("Show S2")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowS2 { get; set; } = true;

    [Category("Parameters")]
    [DisplayName("HBOP (R3)")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowR3 { get; set; } = true;

    [Category("Parameters")]
    [DisplayName("LBOP (S3)")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowS3 { get; set; } = true;

    // --- Cached Calculation Results ---

    private decimal? _pivot;
    private decimal? _r1;
    private decimal? _r2;
    private decimal? _r3;
    private decimal? _s1;
    private decimal? _s2;
    private decimal? _s3;
    private DateTime _periodStart;
    private DateTime _periodEnd;
    private bool _hasValidData;

    public decimal? Pivot => _pivot;
    public decimal? R1 => _r1;
    public decimal? R2 => _r2;
    public decimal? R3 => _r3;
    public decimal? S1 => _s1;
    public decimal? S2 => _s2;
    public decimal? S3 => _s3;
    public DateTime PeriodStart => _periodStart;
    public DateTime PeriodEnd => _periodEnd;
    public bool HasValidData => _hasValidData;

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    // --- ZeroAllocation Cached Paints ---

    private readonly SKPaint _vertPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = false
    };

    private readonly SKPaint _linePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true
    };

    private readonly SKPaint _pLinePaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true
    };

    private readonly SKPaint _textPaint = new()
    {
        IsAntialias = true
    };

    public ClassicPivotPointsObject(ChartPoint anchor)
    {
        Points = new List<ChartPoint> { anchor };
    }

    /// <summary>
    /// Recalculates the 7 Pivot Point levels based on the candles of the preceding period.
    /// Uses O(K) reverse aggregation without intermediate allocations.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData> candles)
    {
        if (Points.Count < 1 || candles == null || candles.Count == 0)
        {
            ResetLevels();
            return;
        }

        var anchor = Points[0].Time;
        ClassicPivotPointsValues.GetPeriodBoundaries(anchor, Period, out _periodStart, out _periodEnd);

        if (!ClassicPivotPointsValues.TryCompute(anchor, candles, Period, out ClassicPivotLevels levels))
        {
            ResetLevels();
            return;
        }

        _pivot = levels.Pivot;
        _r1 = levels.R1;
        _s1 = levels.S1;
        _r2 = levels.R2;
        _s2 = levels.S2;
        _r3 = levels.R3;
        _s3 = levels.S3;
        _hasValidData = true;
    }

    private void ResetLevels()
    {
        _pivot = null;
        _r1 = null;
        _r2 = null;
        _r3 = null;
        _s1 = null;
        _s2 = null;
        _s3 = null;
        _hasValidData = false;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null || Points.Count < 1 || !_hasValidData) return;

        var clip = canvas.LocalClipBounds;
        var anchorScreen = transform.ChartToScreen(Points[0]);
        float anchorX = (float)anchorScreen.X;

        var skColor = SkiaColor;
        float strokeWidth = (float)Thickness;

        // 1. Draw solid vertical reference line at the anchor timestamp using cached paint
        if (anchorX >= clip.Left && anchorX <= clip.Right)
        {
            _vertPaint.Color = skColor;
            _vertPaint.StrokeWidth = strokeWidth;
            canvas.DrawLine(anchorX, clip.Top, anchorX, clip.Bottom, _vertPaint);
        }

        // 2. Draw horizontal lines for the active period span (optionally extended)
        var pStart = transform.ChartToScreen(new ChartPoint(_periodStart, 0m));
        var pEnd = transform.ChartToScreen(new ChartPoint(_periodEnd, 0m));

        float leftX = (float)pStart.X;
        float rightX = (float)pEnd.X;

        if (rightX < leftX)
        {
            (leftX, rightX) = (rightX, leftX);
        }

        float chartLeft = 0f;
        float chartRight = (float)transform.CanvasWidth;

        switch (ExtendMode)
        {
            case PivotLineExtendMode.ExtendRight:
                rightX = Math.Max(rightX, chartRight);
                break;
            case PivotLineExtendMode.ExtendLeft:
                leftX = Math.Min(leftX, chartLeft);
                break;
            case PivotLineExtendMode.ExtendBoth:
                leftX = Math.Min(leftX, chartLeft);
                rightX = Math.Max(rightX, chartRight);
                break;
            case PivotLineExtendMode.None:
            default:
                break;
        }

        // Reuse cached line paints
        _linePaint.Color = skColor;
        _linePaint.StrokeWidth = strokeWidth;

        _pLinePaint.Color = skColor;
        _pLinePaint.StrokeWidth = (float)(Thickness * 1.5);

        _textPaint.Color = skColor;
        _textPaint.TextSize = DrawingThemeContext.DrawingFontSize;

        if (ShowR3 && _r3.HasValue) DrawLevel(canvas, transform, _r3.Value, "HBOP", leftX, rightX, chartRight, _linePaint, _textPaint);
        if (ShowR2 && _r2.HasValue) DrawLevel(canvas, transform, _r2.Value, "R2", leftX, rightX, chartRight, _linePaint, _textPaint);
        if (ShowR1 && _r1.HasValue) DrawLevel(canvas, transform, _r1.Value, "R1", leftX, rightX, chartRight, _linePaint, _textPaint);
        if (ShowP && _pivot.HasValue) DrawLevel(canvas, transform, _pivot.Value, "P", leftX, rightX, chartRight, _pLinePaint, _textPaint);
        if (ShowS1 && _s1.HasValue) DrawLevel(canvas, transform, _s1.Value, "S1", leftX, rightX, chartRight, _linePaint, _textPaint);
        if (ShowS2 && _s2.HasValue) DrawLevel(canvas, transform, _s2.Value, "S2", leftX, rightX, chartRight, _linePaint, _textPaint);
        if (ShowS3 && _s3.HasValue) DrawLevel(canvas, transform, _s3.Value, "LBOP", leftX, rightX, chartRight, _linePaint, _textPaint);

        // 3. Draw selection handle at the anchor point
        if (IsSelected)
        {
            SelectionHandleRenderer.Draw(
                canvas,
                anchorScreen,
                AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null,
                radius: ChartConstants.SelectedHandleRadius);
        }
    }

    private static void DrawLevel(
        SKCanvas canvas,
        ICoordinateTransform transform,
        decimal value,
        string label,
        float leftX,
        float rightX,
        float chartRight,
        SKPaint linePaint,
        SKPaint textPaint)
    {
        float y = (float)transform.GetYFromPrice(value);
        canvas.DrawLine(leftX, y, rightX, y, linePaint);

        // Position label near the right edge of the level
        float textX = rightX + 4;
        if (rightX >= chartRight && leftX < rightX)
        {
            textX = Math.Min(chartRight - 65, rightX - 65);
            if (textX < leftX + 4) textX = leftX + 4;
        }

        canvas.DrawText($"{label} {value:F2}", textX, y - 3, textPaint);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 1 || transform == null || !_hasValidData) return false;

        var anchorScreen = transform.ChartToScreen(Points[0]);

        // Anchor handle hit test
        double dX = screenPoint.X - anchorScreen.X;
        double dY = screenPoint.Y - anchorScreen.Y;
        if (Math.Sqrt(dX * dX + dY * dY) <= tolerance * 2) return true;

        // Vertical line hit test
        if (Math.Abs(screenPoint.X - anchorScreen.X) <= tolerance) return true;

        // Horizontal lines hit test within horizontal span (considering ExtendMode)
        var pStart = transform.ChartToScreen(new ChartPoint(_periodStart, 0m));
        var pEnd = transform.ChartToScreen(new ChartPoint(_periodEnd, 0m));
        float minX = (float)Math.Min(pStart.X, pEnd.X);
        float maxX = (float)Math.Max(pStart.X, pEnd.X);

        float chartLeft = 0f;
        float chartRight = (float)transform.CanvasWidth;

        switch (ExtendMode)
        {
            case PivotLineExtendMode.ExtendRight:
                maxX = Math.Max(maxX, chartRight);
                break;
            case PivotLineExtendMode.ExtendLeft:
                minX = Math.Min(minX, chartLeft);
                break;
            case PivotLineExtendMode.ExtendBoth:
                minX = Math.Min(minX, chartLeft);
                maxX = Math.Max(maxX, chartRight);
                break;
            case PivotLineExtendMode.None:
            default:
                break;
        }

        minX -= (float)tolerance;
        maxX += (float)tolerance;

        if (screenPoint.X >= minX && screenPoint.X <= maxX)
        {
            bool CheckLevel(decimal? level, bool isShown)
            {
                if (!isShown || !level.HasValue) return false;
                double y = transform.GetYFromPrice(level.Value);
                return Math.Abs(screenPoint.Y - y) <= tolerance;
            }

            if (CheckLevel(_r3, ShowR3)) return true;
            if (CheckLevel(_r2, ShowR2)) return true;
            if (CheckLevel(_r1, ShowR1)) return true;
            if (CheckLevel(_pivot, ShowP)) return true;
            if (CheckLevel(_s1, ShowS1)) return true;
            if (CheckLevel(_s2, ShowS2)) return true;
            if (CheckLevel(_s3, ShowS3)) return true;
        }

        return false;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (!_hasValidData) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var levels = new ClassicPivotLevels(_pivot!.Value, _r1!.Value, _r2!.Value, _r3!.Value, _s1!.Value, _s2!.Value, _s3!.Value, _periodStart, _periodEnd);
        return ClassicPivotPointsValues.Calculate(levels, Period, ExtendMode, color);
    }

    public void Dispose()
    {
        _vertPaint.Dispose();
        _linePaint.Dispose();
        _pLinePaint.Dispose();
        _textPaint.Dispose();
        GC.SuppressFinalize(this);
    }
}
