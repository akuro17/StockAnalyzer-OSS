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

public class HoughAutoLinesObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public virtual ChartObjectType Type => ChartObjectType.HoughAutoLines;

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

    // Line visibility toggles
    public bool ShowTrendLines { get; set; } = true;
    public bool ShowSupportLines { get; set; } = true;
    public bool ShowResistanceLines { get; set; } = true;

    // Line classification colors
    public Color TrendLineColor { get; set; } = Color.FromRgb(38, 166, 154);    // Green / Teal
    public Color SupportColor { get; set; } = Color.FromRgb(33, 150, 243);       // Blue
    public Color ResistanceColor { get; set; } = Color.FromRgb(255, 152, 0);     // Orange

    public Color TrendUpColor
    {
        get => TrendLineColor;
        set => TrendLineColor = value;
    }
    public Color TrendDownColor
    {
        get => TrendLineColor;
        set => TrendLineColor = value;
    }

    // Hough parameters
    public int PivotWindow { get; set; } = 3;
    public int VoteThreshold { get; set; } = 3;
    public int MaxLines { get; set; } = 5;
    public HoughNormalizationMode Normalization { get; set; } = HoughNormalizationMode.MinMax;
    public bool ShowChannels { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ExtendLinesToRight { get; set; } = false;

    // Calculation result cache
    public HoughTransformResult? CalculatedResult { get; set; }
    public DateTime SliceStartTime { get; set; }
    public DateTime SliceEndTime { get; set; }

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);
    public SkiaSharp.SKColor SkiaTrendLineColor => new(TrendLineColor.R, TrendLineColor.G, TrendLineColor.B, TrendLineColor.A);
    public SkiaSharp.SKColor SkiaTrendUpColor => SkiaTrendLineColor;
    public SkiaSharp.SKColor SkiaTrendDownColor => SkiaTrendLineColor;
    public SkiaSharp.SKColor SkiaSupportColor => new(SupportColor.R, SupportColor.G, SupportColor.B, SupportColor.A);
    public SkiaSharp.SKColor SkiaResistanceColor => new(ResistanceColor.R, ResistanceColor.G, ResistanceColor.B, ResistanceColor.A);

    private readonly Renderers.HoughAutoLinesRenderer _renderer = new();

    public HoughAutoLinesObject()
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

        if (ExtendLinesToRight && CalculatedResult != null && CalculatedResult.Lines.Count > 0)
        {
            maxX = transform.CanvasWidth;
        }

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

        InvalidateCache();
    }

    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            CalculatedResult = HoughTransformResult.Empty;
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
            CalculatedResult = HoughTransformResult.Empty;
            return;
        }

        int count = endIndex - startIndex + 1;
        int minRequired = Math.Max(5, PivotWindow * 2 + 1);
        if (count < minRequired)
        {
            CalculatedResult = HoughTransformResult.Empty;
            return;
        }

        SliceStartTime = candles[startIndex].Timestamp;
        SliceEndTime = candles[endIndex].Timestamp;

        var slice = new CandleData[count];
        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            slice[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
        }

        CalculatedResult = HoughTransformEngine.DetectLinesFromCandles(
            slice,
            lookback: count,
            pivotWindow: PivotWindow,
            voteThreshold: VoteThreshold,
            maxLines: MaxLines,
            normalization: Normalization);
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        var result = CalculatedResult;
        if (result == null || result.Lines.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new("Detected Lines", "Detected Lines", result.Lines.Count, result.Lines.Count.ToString(), color),
            new("Candidate Points", "Candidate Points", result.TotalCandidatePoints, result.TotalCandidatePoints.ToString(), color)
        };

        if (result.Channels.Count > 0)
        {
            list.Add(new("Detected Channels", "Detected Channels", result.Channels.Count, result.Channels.Count.ToString(), color));
        }

        for (int i = 0; i < Math.Min(5, result.Lines.Count); i++)
        {
            var line = result.Lines[i];
            bool isTrend = line.LineType == HoughLineType.TrendUp || line.LineType == HoughLineType.TrendDown;
            bool isSupport = line.LineType == HoughLineType.Support;
            bool isResistance = line.LineType == HoughLineType.Resistance;

            if (isTrend && !ShowTrendLines) continue;
            if (isSupport && !ShowSupportLines) continue;
            if (isResistance && !ShowResistanceLines) continue;

            IndicatorColor lineCol = isTrend
                ? new IndicatorColor(TrendLineColor.A, TrendLineColor.R, TrendLineColor.G, TrendLineColor.B)
                : isSupport
                    ? new IndicatorColor(SupportColor.A, SupportColor.R, SupportColor.G, SupportColor.B)
                    : isResistance
                        ? new IndicatorColor(ResistanceColor.A, ResistanceColor.R, ResistanceColor.G, ResistanceColor.B)
                        : color;

            list.Add(new(
                $"Line #{i + 1} ({line.LineType})",
                $"Line #{i + 1} ({line.LineType})",
                (decimal)line.Strength,
                $"{line.Slope:F4} (R²={line.RSquared:F2}, Touches={line.TouchCount})",
                lineCol));
        }

        return list;
    }
}
