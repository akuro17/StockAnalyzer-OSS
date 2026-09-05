using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class HoughParabolicCurveObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public virtual ChartObjectType Type => ChartObjectType.HoughParabolicCurve;

    public List<ChartPoint> Points { get; } = new(2);

    // Visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Background band
    public int FillOpacity { get; set; } = 10;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    // Parabolic Hough parameters
    public int PivotWindow { get; set; } = 3;
    public int VoteThreshold { get; set; } = 3;
    public int MaxCurves { get; set; } = 1;
    public ParabolicHoughCurvatureSign CurvatureSign { get; set; } = ParabolicHoughCurvatureSign.Both;
    public bool ShowLabels { get; set; } = true;

    // Results
    public ParabolicHoughResult? CalculatedResult { get; set; }
    public DateTime SliceStartTime { get; set; }
    public DateTime SliceMidTime { get; set; }
    public DateTime SliceEndTime { get; set; }
    public int TotalSliceBars { get; set; }

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    private readonly Renderers.HoughParabolicCurveRenderer _renderer = new();

    public HoughParabolicCurveObject()
    {
    }

    public void InvalidateCache()
    {
        CalculatedResult = null;
        _renderer.InvalidateCache();
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        _renderer.Render(canvas, this, transform, IsSelected);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double minX = Math.Min(p1.X, p2.X) - tolerance;
        double maxX = Math.Max(p1.X, p2.X) + tolerance;

        return screenPoint.X >= minX && screenPoint.X <= maxX;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(
                Points[i].Time + timeDelta,
                Points[i].Price + priceDelta
            );
        }

        if (SliceStartTime != default) SliceStartTime += timeDelta;
        if (SliceEndTime != default) SliceEndTime += timeDelta;
        if (SliceMidTime != default) SliceMidTime += timeDelta;

        InvalidateCache();
    }

    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            CalculatedResult = ParabolicHoughResult.Empty;
            return;
        }

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var startTime = t1 < t2 ? t1 : t2;
        var endTime = t1 > t2 ? t1 : t2;

        int startIndex = -1;
        int endIndex = -1;

        for (int i = 0; i < candles.Count; i++)
        {
            if (startIndex == -1 && candles[i].Timestamp >= startTime)
            {
                startIndex = i;
            }
            if (candles[i].Timestamp <= endTime)
            {
                endIndex = i;
            }
        }

        if (startIndex < 0 || endIndex < 0 || startIndex > endIndex)
        {
            CalculatedResult = ParabolicHoughResult.Empty;
            return;
        }

        int count = endIndex - startIndex + 1;
        int minRequired = Math.Max(5, PivotWindow * 2 + 1);
        if (count < minRequired)
        {
            CalculatedResult = ParabolicHoughResult.Empty;
            return;
        }

        SliceStartTime = candles[startIndex].Timestamp;
        SliceMidTime = candles[startIndex + count / 2].Timestamp;
        SliceEndTime = candles[endIndex].Timestamp;
        TotalSliceBars = count;

        var slice = new CandleData[count];
        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            slice[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
        }

        CalculatedResult = ParabolicHoughTransformEngine.DetectParabolasFromCandles(
            slice,
            lookback: count,
            pivotWindow: PivotWindow,
            voteThreshold: VoteThreshold,
            maxCurves: MaxCurves,
            curvatureSign: CurvatureSign);
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        var result = CalculatedResult;
        if (result == null || result.IsEmpty) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new("Parabolas Detected", "Parabolas Detected", result.Parabolas.Count, result.Parabolas.Count.ToString(), color)
        };

        for (int i = 0; i < result.Parabolas.Count; i++)
        {
            var p = result.Parabolas[i];
            list.Add(new(
                $"Parabola #{i + 1} ({p.CurvatureSign})",
                $"Parabola #{i + 1} ({p.CurvatureSign})",
                p.VertexPrice,
                $"Curvature={p.CurvaturePrice:E3}, Vertex={p.VertexPrice:F2} (R²={p.RSquared:F2}, Touches={p.Votes})",
                color));
        }

        return list;
    }
}
