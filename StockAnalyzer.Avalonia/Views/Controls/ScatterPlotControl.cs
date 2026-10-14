using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// High-performance SkiaSharp custom control for rendering 2D scatter plots.
/// Implements hardware-accelerated multi-layer drawing via <see cref="ICustomDrawOperation"/>,
/// zero-allocation rendering hot paths, 2x2px pixel binning overdraw suppression,
/// deterministic 8px hit-testing, and dynamic confidence/prediction intervals.
/// </summary>
public sealed class ScatterPlotControl : Control, IDisposable
{
    public static readonly StyledProperty<ScatterDataset?> DatasetProperty =
        AvaloniaProperty.Register<ScatterPlotControl, ScatterDataset?>(nameof(Dataset));

    public static readonly StyledProperty<ScatterAnalysisResult?> AnalysisResultProperty =
        AvaloniaProperty.Register<ScatterPlotControl, ScatterAnalysisResult?>(nameof(AnalysisResult));

    public static readonly StyledProperty<bool> ShowRegressionLineProperty =
        AvaloniaProperty.Register<ScatterPlotControl, bool>(nameof(ShowRegressionLine), true);

    public static readonly StyledProperty<bool> ShowConfidenceBandProperty =
        AvaloniaProperty.Register<ScatterPlotControl, bool>(nameof(ShowConfidenceBand), true);

    public static readonly StyledProperty<bool> ShowPredictionBandProperty =
        AvaloniaProperty.Register<ScatterPlotControl, bool>(nameof(ShowPredictionBand), false);

    public static readonly StyledProperty<bool> SuppressOutliersProperty =
        AvaloniaProperty.Register<ScatterPlotControl, bool>(nameof(SuppressOutliers), false);

    public static readonly StyledProperty<PriceScaleType> XScaleProperty =
        AvaloniaProperty.Register<ScatterPlotControl, PriceScaleType>(nameof(XScale), PriceScaleType.Linear);

    public static readonly StyledProperty<PriceScaleType> YScaleProperty =
        AvaloniaProperty.Register<ScatterPlotControl, PriceScaleType>(nameof(YScale), PriceScaleType.Linear);

    public static readonly StyledProperty<int?> HoveredPointIndexProperty =
        AvaloniaProperty.Register<ScatterPlotControl, int?>(nameof(HoveredPointIndex));

    public static readonly StyledProperty<int?> SelectedPointIndexProperty =
        AvaloniaProperty.Register<ScatterPlotControl, int?>(nameof(SelectedPointIndex));

    public event EventHandler<ScatterPoint>? PointClicked;

    // Viewport Margins
    private const float MarginLeft = 55.0f;
    private const float MarginRight = 20.0f;
    private const float MarginTop = 20.0f;
    private const float MarginBottom = 40.0f;
    private const float HitRadius = 8.0f;
    private const float HitRadiusSq = HitRadius * HitRadius;

    // Pre-allocated Coordinate Transform
    private readonly ScatterCoordinateTransform _transform = new();

    // Zero-allocation reusable buffers
    private readonly HashSet<long> _pixelBinningSet = new();
    private readonly SKPath _confidenceBandPath = new();
    private readonly SKPath _predictionBandPath = new();
    private readonly SKPath _regressionPath = new();
    private readonly SKPathEffect _gridDashEffect = SKPathEffect.CreateDash([3.0f, 3.0f], 0.0f);
    private readonly SKPathEffect _crosshairDashEffect = SKPathEffect.CreateDash([2.0f, 2.0f], 0.0f);

    // Pre-allocated SKPaint instances
    private readonly SKPaint _bgPaint = new() { Color = new SKColor(0x14, 0x15, 0x17), Style = SKPaintStyle.Fill };
    private readonly SKPaint _plotBgPaint = new() { Color = new SKColor(0x1A, 0x1B, 0x1E), Style = SKPaintStyle.Fill };
    private readonly SKPaint _gridPaint;
    private readonly SKPaint _axisLinePaint = new() { Color = new SKColor(0x37, 0x3A, 0x40), Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f };
    private readonly SKPaint _axisTextPaint = new() { Color = new SKColor(0x90, 0x92, 0x96), Style = SKPaintStyle.Fill, TextSize = 11.0f, IsAntialias = true };
    private readonly SKPaint _axisTitlePaint = new() { Color = new SKColor(0xC1, 0xC2, 0xC5), Style = SKPaintStyle.Fill, TextSize = 12.0f, IsAntialias = true, FakeBoldText = true };
    private readonly SKPaint _regressionPaint = new() { Color = new SKColor(0x3B, 0x82, 0xF6), Style = SKPaintStyle.Stroke, StrokeWidth = 2.0f, IsAntialias = true };
    private readonly SKPaint _confidenceBandPaint = new() { Color = new SKColor(0x3B, 0x82, 0xF6, 0x26), Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _predictionBandPaint = new() { Color = new SKColor(0x60, 0xA5, 0xFA, 0x14), Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _pointFillPaint = new() { Color = new SKColor(0x38, 0xBD, 0xF8), Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _pointStrokePaint = new() { Color = new SKColor(0x02, 0x84, 0xC7), Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f, IsAntialias = true };
    private readonly SKPaint _outlierFillPaint = new() { Color = new SKColor(0xEF, 0x44, 0x44), Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _outlierStrokePaint = new() { Color = new SKColor(0xF8, 0x71, 0xF1), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
    private readonly SKPaint _hoverPointPaint = new() { Color = new SKColor(0xFA, 0xCC, 0x15), Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
    private readonly SKPaint _crosshairPaint;
    private readonly SKPaint _hudBgPaint = new() { Color = new SKColor(0x18, 0x18, 0x1B, 0xE0), Style = SKPaintStyle.Fill };
    private readonly SKPaint _hudBorderPaint = new() { Color = new SKColor(0x27, 0x27, 0x2A), Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f };
    private readonly SKPaint _hudTextPaint = new() { Color = new SKColor(0xF4, 0xF4, 0xF5), Style = SKPaintStyle.Fill, TextSize = 11.0f, IsAntialias = true };
    private readonly SKPaint _hudBoldTextPaint = new() { Color = new SKColor(0x38, 0xBD, 0xF8), Style = SKPaintStyle.Fill, TextSize = 11.0f, IsAntialias = true, FakeBoldText = true };
    private readonly SKPaint _tooltipBgPaint = new() { Color = new SKColor(0x09, 0x09, 0x0B, 0xF0), Style = SKPaintStyle.Fill };
    private readonly SKPaint _tooltipBorderPaint = new() { Color = new SKColor(0x52, 0x52, 0x5B), Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f };
    private readonly SKPaint _tooltipTextPaint = new() { Color = SKColors.White, Style = SKPaintStyle.Fill, TextSize = 11.0f, IsAntialias = true };
    private Point? _cursorPos;

    static ScatterPlotControl()
    {
        AffectsRender<ScatterPlotControl>(
            DatasetProperty,
            AnalysisResultProperty,
            ShowRegressionLineProperty,
            ShowConfidenceBandProperty,
            ShowPredictionBandProperty,
            SuppressOutliersProperty,
            XScaleProperty,
            YScaleProperty,
            HoveredPointIndexProperty,
            SelectedPointIndexProperty);
    }

    public ScatterPlotControl()
    {
        ClipToBounds = true;
        Focusable = true;

        _gridPaint = new SKPaint
        {
            Color = new SKColor(0x2C, 0x2E, 0x33),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            PathEffect = _gridDashEffect
        };

        _crosshairPaint = new SKPaint
        {
            Color = new SKColor(0x64, 0x74, 0x8B),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            PathEffect = _crosshairDashEffect
        };
    }

    public ScatterDataset? Dataset
    {
        get => GetValue(DatasetProperty);
        set => SetValue(DatasetProperty, value);
    }

    public ScatterAnalysisResult? AnalysisResult
    {
        get => GetValue(AnalysisResultProperty);
        set => SetValue(AnalysisResultProperty, value);
    }

    public bool ShowRegressionLine
    {
        get => GetValue(ShowRegressionLineProperty);
        set => SetValue(ShowRegressionLineProperty, value);
    }

    public bool ShowConfidenceBand
    {
        get => GetValue(ShowConfidenceBandProperty);
        set => SetValue(ShowConfidenceBandProperty, value);
    }

    public bool ShowPredictionBand
    {
        get => GetValue(ShowPredictionBandProperty);
        set => SetValue(ShowPredictionBandProperty, value);
    }

    public bool SuppressOutliers
    {
        get => GetValue(SuppressOutliersProperty);
        set => SetValue(SuppressOutliersProperty, value);
    }

    public PriceScaleType XScale
    {
        get => GetValue(XScaleProperty);
        set => SetValue(XScaleProperty, value);
    }

    public PriceScaleType YScale
    {
        get => GetValue(YScaleProperty);
        set => SetValue(YScaleProperty, value);
    }

    public int? HoveredPointIndex
    {
        get => GetValue(HoveredPointIndexProperty);
        set => SetValue(HoveredPointIndexProperty, value);
    }

    public int? SelectedPointIndex
    {
        get => GetValue(SelectedPointIndexProperty);
        set => SetValue(SelectedPointIndexProperty, value);
    }

    public ScatterCoordinateTransform Transform => _transform;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var dataset = Dataset;
        var analysis = AnalysisResult;
        bool showRegression = ShowRegressionLine;
        bool showConfidence = ShowConfidenceBand;
        bool showPrediction = ShowPredictionBand;
        bool suppressOutliers = SuppressOutliers;
        var xScale = XScale;
        var yScale = YScale;
        int? hoveredIdx = HoveredPointIndex;
        int? selectedIdx = SelectedPointIndex;
        var cursorPos = _cursorPos;

        UpdateTransform(Bounds.Width, Bounds.Height, dataset, analysis, suppressOutliers, xScale, yScale);
        var transformSnapshot = _transform.Clone();

        context.Custom(new ScatterPlotDrawOperation(
            this,
            new Rect(Bounds.Size),
            dataset,
            analysis,
            showRegression,
            showConfidence,
            showPrediction,
            suppressOutliers,
            xScale,
            yScale,
            hoveredIdx,
            selectedIdx,
            cursorPos,
            transformSnapshot));
    }

    private void UpdateTransform(
        double width,
        double height,
        ScatterDataset? dataset,
        ScatterAnalysisResult? analysis,
        bool suppressOutliers,
        PriceScaleType xScale,
        PriceScaleType yScale)
    {
        float w = (float)width;
        float h = (float)height;
        if (w <= 0.0f || h <= 0.0f) return;

        float vpW = Math.Max(0.0f, w - MarginLeft - MarginRight);
        float vpH = Math.Max(0.0f, h - MarginTop - MarginBottom);

        double minX = 0.0, maxX = 1.0, minY = 0.0, maxY = 1.0;

        if (analysis != null && analysis.ValidPointCount > 0)
        {
            if (suppressOutliers)
            {
                minX = analysis.SuppressedMinX;
                maxX = analysis.SuppressedMaxX;
                minY = analysis.SuppressedMinY;
                maxY = analysis.SuppressedMaxY;
            }
            else
            {
                minX = analysis.MinX;
                maxX = analysis.MaxX;
                minY = analysis.MinY;
                maxY = analysis.MaxY;
            }
        }
        else if (dataset != null && dataset.Points.Count > 0)
        {
            minX = double.PositiveInfinity;
            maxX = double.NegativeInfinity;
            minY = double.PositiveInfinity;
            maxY = double.NegativeInfinity;

            for (int i = 0; i < dataset.Points.Count; i++)
            {
                var pt = dataset.Points[i];
                if (!pt.IsFinite) continue;
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        if (Math.Abs(maxX - minX) <= 1e-12)
        {
            minX -= 1.0;
            maxX += 1.0;
        }
        if (Math.Abs(maxY - minY) <= 1e-12)
        {
            minY -= 1.0;
            maxY += 1.0;
        }

        _transform.XScale = xScale;
        _transform.YScale = yScale;
        _transform.SetRanges(minX, maxX, minY, maxY, MarginLeft, MarginTop, vpW, vpH, w, h);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _cursorPos = pos;

        var dataset = Dataset;
        if (dataset == null || dataset.Points.Count == 0)
        {
            if (HoveredPointIndex != null)
            {
                HoveredPointIndex = null;
                InvalidateVisual();
            }
            return;
        }

        // Fast deterministic nearest neighbor search within 8px
        int nearestIdx = -1;
        double minSqDist = HitRadiusSq;

        for (int i = 0; i < dataset.Points.Count; i++)
        {
            var p = dataset.Points[i];
            if (!p.IsFinite) continue;

            double sx = _transform.XToScreen(p.X);
            double sy = _transform.YToScreen(p.Y);

            double dx = sx - pos.X;
            double dy = sy - pos.Y;
            double distSq = (dx * dx) + (dy * dy);

            if (distSq <= minSqDist)
            {
                minSqDist = distSq;
                nearestIdx = i;
            }
        }

        int? newHover = nearestIdx >= 0 ? nearestIdx : null;
        if (HoveredPointIndex != newHover)
        {
            HoveredPointIndex = newHover;
        }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HoveredPointIndex.HasValue && Dataset != null && HoveredPointIndex.Value < Dataset.Points.Count)
        {
            SelectedPointIndex = HoveredPointIndex;
            PointClicked?.Invoke(this, Dataset.Points[HoveredPointIndex.Value]);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _cursorPos = null;
        if (HoveredPointIndex != null)
        {
            HoveredPointIndex = null;
            InvalidateVisual();
        }
    }

    internal void RenderSkia(
        SKCanvas canvas,
        Rect bounds,
        ScatterDataset? dataset,
        ScatterAnalysisResult? analysis,
        bool showRegressionLine,
        bool showConfidenceBand,
        bool showPredictionBand,
        bool suppressOutliers,
        PriceScaleType xScale,
        PriceScaleType yScale,
        int? hoveredPointIndex,
        int? selectedPointIndex,
        Point? cursorPos,
        ScatterCoordinateTransform transform)
    {
        float w = (float)bounds.Width;
        float h = (float)bounds.Height;

        if (w <= 0.0f || h <= 0.0f) return;

        canvas.Save();
        try
        {
            // Layer 1: Background & Grid Lines
            canvas.DrawRect(0.0f, 0.0f, w, h, _bgPaint);

            float vpW = Math.Max(0.0f, w - MarginLeft - MarginRight);
            float vpH = Math.Max(0.0f, h - MarginTop - MarginBottom);

            var plotRect = new SKRect(MarginLeft, MarginTop, MarginLeft + vpW, MarginTop + vpH);
            canvas.DrawRect(plotRect, _plotBgPaint);

            if (transform.ViewportWidth <= 0.0 || Math.Abs(transform.CanvasWidth - w) > 0.1 || Math.Abs(transform.CanvasHeight - h) > 0.1)
            {
                double minX = 0.0, maxX = 1.0, minY = 0.0, maxY = 1.0;
                if (analysis != null && analysis.ValidPointCount > 0)
                {
                    minX = suppressOutliers ? analysis.SuppressedMinX : analysis.MinX;
                    maxX = suppressOutliers ? analysis.SuppressedMaxX : analysis.MaxX;
                    minY = suppressOutliers ? analysis.SuppressedMinY : analysis.MinY;
                    maxY = suppressOutliers ? analysis.SuppressedMaxY : analysis.MaxY;
                }
                else if (dataset != null && dataset.Points.Count > 0)
                {
                    minX = double.PositiveInfinity;
                    maxX = double.NegativeInfinity;
                    minY = double.PositiveInfinity;
                    maxY = double.NegativeInfinity;
                    for (int i = 0; i < dataset.Points.Count; i++)
                    {
                        var pt = dataset.Points[i];
                        if (!pt.IsFinite) continue;
                        if (pt.X < minX) minX = pt.X;
                        if (pt.X > maxX) maxX = pt.X;
                        if (pt.Y < minY) minY = pt.Y;
                        if (pt.Y > maxY) maxY = pt.Y;
                    }
                }
                if (Math.Abs(maxX - minX) <= 1e-12) { minX -= 1.0; maxX += 1.0; }
                if (Math.Abs(maxY - minY) <= 1e-12) { minY -= 1.0; maxY += 1.0; }

                transform.XScale = xScale;
                transform.YScale = yScale;
                transform.SetRanges(minX, maxX, minY, maxY, MarginLeft, MarginTop, vpW, vpH, w, h);
            }

            // Draw Grid Lines & Ticks
            DrawGridAndAxes(canvas, plotRect, transform.MinX, transform.MaxX, transform.MinY, transform.MaxY, dataset, transform);

            // Save canvas state clipped to plot rectangle for data layers
            canvas.Save();
            canvas.ClipRect(plotRect);

            // Layer 2: Confidence & Prediction Interval Bands
            if (analysis != null && analysis.HasRegression && analysis.HasIntervals)
            {
                if (showPredictionBand)
                {
                    DrawBand(canvas, analysis, isPrediction: true, plotRect, transform);
                }
                if (showConfidenceBand)
                {
                    DrawBand(canvas, analysis, isPrediction: false, plotRect, transform);
                }
            }

            // Layer 3: OLS Regression Line (Nonlinear Curve Sampling in Log Space)
            if (analysis != null && analysis.HasRegression && showRegressionLine)
            {
                bool isLogMode = transform.XScale == PriceScaleType.Log || transform.YScale == PriceScaleType.Log;
                if (!isLogMode)
                {
                    float lineX1 = (float)transform.XToScreen(transform.MinX);
                    float lineY1 = (float)transform.YToScreen(analysis.PredictY(transform.MinX));
                    float lineX2 = (float)transform.XToScreen(transform.MaxX);
                    float lineY2 = (float)transform.YToScreen(analysis.PredictY(transform.MaxX));
                    if (float.IsFinite(lineX1) && float.IsFinite(lineY1) && float.IsFinite(lineX2) && float.IsFinite(lineY2))
                    {
                        canvas.DrawLine(lineX1, lineY1, lineX2, lineY2, _regressionPaint);
                    }
                }
                else
                {
                    // In log space, y = b0 + b1 x curves on screen. Sample K screen points.
                    _regressionPath.Reset();
                    int kSteps = Math.Max(40, (int)Math.Ceiling(plotRect.Width / 4.0f));
                    bool lineStarted = false;

                    for (int i = 0; i <= kSteps; i++)
                    {
                        float sx = plotRect.Left + (i * plotRect.Width / kSteps);
                        double x = transform.ScreenToX(sx);
                        if (!double.IsFinite(x)) continue;

                        double yHat = analysis.PredictY(x);
                        float sy = (float)transform.YToScreen(yHat);
                        if (!float.IsFinite(sy)) continue;

                        if (!lineStarted)
                        {
                            _regressionPath.MoveTo(sx, sy);
                            lineStarted = true;
                        }
                        else
                        {
                            _regressionPath.LineTo(sx, sy);
                        }
                    }

                    if (lineStarted)
                    {
                        canvas.DrawPath(_regressionPath, _regressionPaint);
                    }
                }
            }

            // Layer 4: Data Points (2-Pass Screen Binning Rendering)
            _pixelBinningSet.Clear();
            int? hoveredIdx = hoveredPointIndex;
            int? selectedIdx = selectedPointIndex;
            int excludedNonPositiveCount = 0;

            if (dataset != null)
            {
                var points = dataset.Points;
                var mask = analysis?.OutlierMask;
                bool logX = xScale == PriceScaleType.Log;
                bool logY = yScale == PriceScaleType.Log;

                // Pass 1: Bulk Normal Points (with 2x2px Screen Binning)
                for (int i = 0; i < points.Count; i++)
                {
                    var pt = points[i];
                    if (!pt.IsFinite) continue;

                    if ((logX && pt.X <= 0.0) || (logY && pt.Y <= 0.0))
                    {
                        excludedNonPositiveCount++;
                        continue; // Excluded from log axis
                    }

                    bool isOutlier = mask != null && i < mask.Count && mask[i];
                    bool isInteractive = i == hoveredIdx || i == selectedIdx;

                    if (isOutlier || isInteractive) continue; // Deferred to Pass 2

                    float sx = (float)transform.XToScreen(pt.X);
                    float sy = (float)transform.YToScreen(pt.Y);
                    if (!float.IsFinite(sx) || !float.IsFinite(sy)) continue;

                    int binX = (int)(sx * 0.5f);
                    int binY = (int)(sy * 0.5f);
                    long binKey = ((long)binX << 32) | (uint)binY;

                    if (!_pixelBinningSet.Add(binKey))
                    {
                        continue; // Overdraw suppressed
                    }

                    float r = pt.Radius > 0.0f ? pt.Radius : 3.5f;
                    canvas.DrawCircle(sx, sy, r, _pointFillPaint);
                    canvas.DrawCircle(sx, sy, r, _pointStrokePaint);
                }

                // Pass 2: Outliers & Interactive Points (Rendered strictly on top, zero binning suppression)
                for (int i = 0; i < points.Count; i++)
                {
                    var pt = points[i];
                    if (!pt.IsFinite) continue;

                    if ((logX && pt.X <= 0.0) || (logY && pt.Y <= 0.0))
                    {
                        continue; // Already counted in Pass 1
                    }

                    bool isOutlier = mask != null && i < mask.Count && mask[i];
                    bool isInteractive = i == hoveredIdx || i == selectedIdx;

                    if (!isOutlier && !isInteractive) continue; // Already rendered in Pass 1

                    float sx = (float)transform.XToScreen(pt.X);
                    float sy = (float)transform.YToScreen(pt.Y);
                    if (!float.IsFinite(sx) || !float.IsFinite(sy)) continue;

                    float r = pt.Radius > 0.0f ? pt.Radius : 3.5f;

                    if (isOutlier)
                    {
                        canvas.DrawCircle(sx, sy, r + 1.0f, _outlierFillPaint);
                        canvas.DrawCircle(sx, sy, r + 1.0f, _outlierStrokePaint);
                    }
                    else
                    {
                        canvas.DrawCircle(sx, sy, r, _pointFillPaint);
                        canvas.DrawCircle(sx, sy, r, _pointStrokePaint);
                    }
                }
            }

            // Layer 5: Crosshair & Hover Highlight
            if (cursorPos.HasValue && plotRect.Contains(new SKPoint((float)cursorPos.Value.X, (float)cursorPos.Value.Y)))
            {
                float cx = (float)cursorPos.Value.X;
                float cy = (float)cursorPos.Value.Y;
                canvas.DrawLine(plotRect.Left, cy, plotRect.Right, cy, _crosshairPaint);
                canvas.DrawLine(cx, plotRect.Top, cx, plotRect.Bottom, _crosshairPaint);
            }

            if (hoveredIdx.HasValue && dataset != null && hoveredIdx.Value < dataset.Points.Count)
            {
                var hPt = dataset.Points[hoveredIdx.Value];
                if (hPt.IsFinite)
                {
                    float hsx = (float)transform.XToScreen(hPt.X);
                    float hsy = (float)transform.YToScreen(hPt.Y);
                    canvas.DrawCircle(hsx, hsy, 6.0f, _hoverPointPaint);
                }
            }

            canvas.Restore(); // Restore clip

            // Layer 6: Statistics HUD Overlay
            if (analysis != null)
            {
                DrawHud(canvas, analysis, plotRect, excludedNonPositiveCount);
            }

            // Tooltip Badge for Hovered Point
            if (hoveredIdx.HasValue && dataset != null && hoveredIdx.Value < dataset.Points.Count)
            {
                var hPt = dataset.Points[hoveredIdx.Value];
                if (hPt.IsFinite)
                {
                    string? label = (dataset.Labels != null && hoveredIdx.Value < dataset.Labels.Count)
                        ? dataset.Labels[hoveredIdx.Value]
                        : null;
                    DrawTooltip(canvas, hPt, label, bounds, transform);
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private void DrawGridAndAxes(SKCanvas canvas, SKRect plotRect, double minX, double maxX, double minY, double maxY, ScatterDataset? dataset, ScatterCoordinateTransform transform)
    {
        // Axes outline
        canvas.DrawRect(plotRect, _axisLinePaint);

        // Vertical Grid & X Ticks (5 subdivisions)
        int xTicks = 5;
        for (int i = 0; i <= xTicks; i++)
        {
            double frac = i / (double)xTicks;
            float sx = plotRect.Left + (float)(frac * plotRect.Width);
            double valX = transform.ScreenToX(sx);

            canvas.DrawLine(sx, plotRect.Top, sx, plotRect.Bottom, _gridPaint);

            string tickText = FormatTickValue(valX);
            float textWidth = _axisTextPaint.MeasureText(tickText);
            canvas.DrawText(tickText, sx - (textWidth * 0.5f), plotRect.Bottom + 16.0f, _axisTextPaint);
        }

        // Horizontal Grid & Y Ticks (5 subdivisions)
        int yTicks = 5;
        for (int i = 0; i <= yTicks; i++)
        {
            double frac = i / (double)yTicks;
            float sy = plotRect.Top + (float)(frac * plotRect.Height);
            double valY = transform.ScreenToY(sy);

            canvas.DrawLine(plotRect.Left, sy, plotRect.Right, sy, _gridPaint);

            string tickText = FormatTickValue(valY);
            float textWidth = _axisTextPaint.MeasureText(tickText);
            canvas.DrawText(tickText, plotRect.Left - textWidth - 6.0f, sy + 4.0f, _axisTextPaint);
        }

        // Axis Titles
        if (dataset is { } ds)
        {
            if (!string.IsNullOrEmpty(ds.XAxisLabel))
            {
                float labelWidth = _axisTitlePaint.MeasureText(ds.XAxisLabel);
                float labelX = plotRect.Left + (0.5f * (plotRect.Width - labelWidth));
                canvas.DrawText(ds.XAxisLabel, labelX, plotRect.Bottom + 32.0f, _axisTitlePaint);
            }

            if (!string.IsNullOrEmpty(ds.YAxisLabel))
            {
                canvas.Save();
                canvas.RotateDegrees(-90, 16.0f, plotRect.Top + (0.5f * plotRect.Height));
                float labelWidth = _axisTitlePaint.MeasureText(ds.YAxisLabel);
                canvas.DrawText(ds.YAxisLabel, 16.0f - (labelWidth * 0.5f), plotRect.Top + (0.5f * plotRect.Height) - 8.0f, _axisTitlePaint);
                canvas.Restore();
            }
        }
    }

    private void DrawBand(SKCanvas canvas, ScatterAnalysisResult analysis, bool isPrediction, SKRect plotRect, ScatterCoordinateTransform transform)
    {
        var path = isPrediction ? _predictionBandPath : _confidenceBandPath;
        var paint = isPrediction ? _predictionBandPaint : _confidenceBandPaint;
        path.Reset();

        int steps = Math.Max(40, (int)Math.Ceiling(plotRect.Width / 4.0f));
        bool started = false;

        // Forward pass: Upper boundary
        for (int i = 0; i <= steps; i++)
        {
            float sx = plotRect.Left + (i * plotRect.Width / steps);
            double x = transform.ScreenToX(sx);
            if (!double.IsFinite(x)) continue;

            var (low, high) = isPrediction
                ? analysis.GetPredictionInterval(x)
                : analysis.GetConfidenceInterval(x);

            float sy = (float)transform.YToScreen(high);
            if (!float.IsFinite(sy)) continue;

            if (!started)
            {
                path.MoveTo(sx, sy);
                started = true;
            }
            else
            {
                path.LineTo(sx, sy);
            }
        }

        if (!started) return;

        // Backward pass: Lower boundary
        for (int i = steps; i >= 0; i--)
        {
            float sx = plotRect.Left + (i * plotRect.Width / steps);
            double x = transform.ScreenToX(sx);
            if (!double.IsFinite(x)) continue;

            var (low, high) = isPrediction
                ? analysis.GetPredictionInterval(x)
                : analysis.GetConfidenceInterval(x);

            float sy = (float)transform.YToScreen(low);
            if (!float.IsFinite(sy)) continue;

            path.LineTo(sx, sy);
        }

        path.Close();
        canvas.DrawPath(path, paint);
    }

    private void DrawHud(SKCanvas canvas, ScatterAnalysisResult analysis, SKRect plotRect, int excludedCount)
    {
        float hudWidth = 175.0f;
        float hudHeight = excludedCount > 0 ? 102.0f : 88.0f;
        float hudX = plotRect.Right - hudWidth - 8.0f;
        float hudY = plotRect.Top + 8.0f;

        var hudRect = new SKRoundRect(new SKRect(hudX, hudY, hudX + hudWidth, hudY + hudHeight), 6.0f, 6.0f);
        canvas.DrawRoundRect(hudRect, _hudBgPaint);
        canvas.DrawRoundRect(hudRect, _hudBorderPaint);

        float textX = hudX + 8.0f;
        float textY = hudY + 16.0f;

        canvas.DrawText("Scatter Analysis", textX, textY, _hudBoldTextPaint);

        textY += 15.0f;
        canvas.DrawText($"N = {analysis.ValidPointCount} (Outliers: {analysis.OutlierCount})", textX, textY, _hudTextPaint);

        if (excludedCount > 0)
        {
            textY += 14.0f;
            canvas.DrawText($"Excluded: {excludedCount} non-positive", textX, textY, _hudTextPaint);
        }

        textY += 14.0f;
        if (analysis.HasCorrelation)
        {
            canvas.DrawText($"r = {analysis.CorrelationR:F3}  (R² = {analysis.RSquared:F3})", textX, textY, _hudTextPaint);
        }
        else
        {
            canvas.DrawText("r = —  (R² = —)", textX, textY, _hudTextPaint);
        }

        textY += 14.0f;
        if (analysis.HasRegression)
        {
            string sign = analysis.Slope >= 0 ? "+" : "-";
            canvas.DrawText($"y = {analysis.Intercept:F2} {sign} {Math.Abs(analysis.Slope):F2}x  [Linear]", textX, textY, _hudTextPaint);
        }
        else
        {
            canvas.DrawText("Regression: None", textX, textY, _hudTextPaint);
        }

        textY += 14.0f;
        canvas.DrawText($"s = {analysis.ResidualStdError:F3}  (Conf: {analysis.ConfidenceLevel * 100:F0}%)", textX, textY, _hudTextPaint);
    }

    private void DrawTooltip(SKCanvas canvas, ScatterPoint pt, string? label, Rect bounds, ScatterCoordinateTransform transform)
    {
        float sx = (float)transform.XToScreen(pt.X);
        float sy = (float)transform.YToScreen(pt.Y);

        string text1 = string.IsNullOrEmpty(label) ? $"({pt.X:F2}, {pt.Y:F2})" : $"{label}: ({pt.X:F2}, {pt.Y:F2})";

        float tw = _tooltipTextPaint.MeasureText(text1);
        float bw = tw + 16.0f;
        float bh = 24.0f;

        float bx = sx + 8.0f;
        float by = sy - bh - 8.0f;

        if (bx + bw > bounds.Width) bx = sx - bw - 8.0f;
        if (by < 4.0f) by = sy + 8.0f;

        var badgeRect = new SKRoundRect(new SKRect(bx, by, bx + bw, by + bh), 4.0f, 4.0f);
        canvas.DrawRoundRect(badgeRect, _tooltipBgPaint);
        canvas.DrawRoundRect(badgeRect, _tooltipBorderPaint);

        canvas.DrawText(text1, bx + 8.0f, by + 16.0f, _tooltipTextPaint);
    }

    private static string FormatTickValue(double value)
    {
        double abs = Math.Abs(value);
        if (abs == 0.0) return "0";
        if (abs >= 1000.0) return value.ToString("F0");
        if (abs >= 1.0) return value.ToString("F2");
        if (abs >= 0.01) return value.ToString("F3");
        return value.ToString("G3");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HoveredPointIndex = null;
        _cursorPos = null;
        _pixelBinningSet.Clear();
        _confidenceBandPath.Reset();
        _predictionBandPath.Reset();
        _regressionPath.Reset();
    }

    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _gridDashEffect.Dispose();
        _crosshairDashEffect.Dispose();
        _confidenceBandPath.Dispose();
        _predictionBandPath.Dispose();
        _regressionPath.Dispose();

        _bgPaint.Dispose();
        _plotBgPaint.Dispose();
        _gridPaint.Dispose();
        _axisLinePaint.Dispose();
        _axisTextPaint.Dispose();
        _axisTitlePaint.Dispose();
        _regressionPaint.Dispose();
        _confidenceBandPaint.Dispose();
        _predictionBandPaint.Dispose();
        _pointFillPaint.Dispose();
        _pointStrokePaint.Dispose();
        _outlierFillPaint.Dispose();
        _outlierStrokePaint.Dispose();
        _hoverPointPaint.Dispose();
        _crosshairPaint.Dispose();
        _hudBgPaint.Dispose();
        _hudBorderPaint.Dispose();
        _hudTextPaint.Dispose();
        _hudBoldTextPaint.Dispose();
        _tooltipBgPaint.Dispose();
        _tooltipBorderPaint.Dispose();
        _tooltipTextPaint.Dispose();
    }

    private sealed class ScatterPlotDrawOperation : SkiaLeaseDrawOperation
    {
        private readonly ScatterPlotControl _owner;
        private readonly ScatterDataset? _dataset;
        private readonly ScatterAnalysisResult? _analysis;
        private readonly bool _showRegressionLine;
        private readonly bool _showConfidenceBand;
        private readonly bool _showPredictionBand;
        private readonly bool _suppressOutliers;
        private readonly PriceScaleType _xScale;
        private readonly PriceScaleType _yScale;
        private readonly int? _hoveredPointIndex;
        private readonly int? _selectedPointIndex;
        private readonly Point? _cursorPos;
        private readonly ScatterCoordinateTransform _transform;

        public ScatterPlotDrawOperation(
            ScatterPlotControl owner,
            Rect bounds,
            ScatterDataset? dataset,
            ScatterAnalysisResult? analysis,
            bool showRegressionLine,
            bool showConfidenceBand,
            bool showPredictionBand,
            bool suppressOutliers,
            PriceScaleType xScale,
            PriceScaleType yScale,
            int? hoveredPointIndex,
            int? selectedPointIndex,
            Point? cursorPos,
            ScatterCoordinateTransform transform)
            : base(bounds)
        {
            _owner = owner;
            _dataset = dataset;
            _analysis = analysis;
            _showRegressionLine = showRegressionLine;
            _showConfidenceBand = showConfidenceBand;
            _showPredictionBand = showPredictionBand;
            _suppressOutliers = suppressOutliers;
            _xScale = xScale;
            _yScale = yScale;
            _hoveredPointIndex = hoveredPointIndex;
            _selectedPointIndex = selectedPointIndex;
            _cursorPos = cursorPos;
            _transform = transform;
        }

        protected override void RenderCore(SKCanvas canvas, Rect bounds)
        {
            _owner.RenderSkia(
                canvas,
                bounds,
                _dataset,
                _analysis,
                _showRegressionLine,
                _showConfidenceBand,
                _showPredictionBand,
                _suppressOutliers,
                _xScale,
                _yScale,
                _hoveredPointIndex,
                _selectedPointIndex,
                _cursorPos,
                _transform);
        }
    }
}
