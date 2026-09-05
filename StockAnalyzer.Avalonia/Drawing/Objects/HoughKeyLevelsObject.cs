using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class HoughKeyLevelsObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public virtual ChartObjectType Type => ChartObjectType.HoughKeyLevels;

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

    // S/R colors
    public Color SupportColor { get; set; } = Color.FromRgb(38, 166, 154);    // Green
    public Color ResistanceColor { get; set; } = Color.FromRgb(239, 83, 80);   // Red

    // Parameters
    public int PivotWindow { get; set; } = 3;
    public int VoteThreshold { get; set; } = 3;
    public int MaxLevels { get; set; } = 5;
    public bool ExtendRight { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public decimal BandAtrMultiplier { get; set; } = 0.15m;

    // Results
    public IReadOnlyList<HoughDetectedLine> CalculatedLevels { get; set; } = Array.Empty<HoughDetectedLine>();
    public double CalculatedAtr { get; set; } = 1.0;
    public DateTime SliceStartTime { get; set; }
    public DateTime SliceEndTime { get; set; }

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);
    public SkiaSharp.SKColor SkiaSupportColor => new(SupportColor.R, SupportColor.G, SupportColor.B, SupportColor.A);
    public SkiaSharp.SKColor SkiaResistanceColor => new(ResistanceColor.R, ResistanceColor.G, ResistanceColor.B, ResistanceColor.A);

    private readonly Renderers.HoughKeyLevelsRenderer _renderer = new();

    public HoughKeyLevelsObject()
    {
    }

    public void InvalidateCache()
    {
        CalculatedLevels = Array.Empty<HoughDetectedLine>();
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

        if (ExtendRight && CalculatedLevels.Count > 0)
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
            CalculatedLevels = Array.Empty<HoughDetectedLine>();
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
            CalculatedLevels = Array.Empty<HoughDetectedLine>();
            return;
        }

        int count = endIndex - startIndex + 1;
        int minRequired = Math.Max(5, PivotWindow * 2 + 1);
        if (count < minRequired)
        {
            CalculatedLevels = Array.Empty<HoughDetectedLine>();
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

        CalculatedAtr = ComputeAtr(slice);

        var result = HoughTransformEngine.DetectLinesFromCandles(
            slice,
            lookback: count,
            pivotWindow: PivotWindow,
            voteThreshold: VoteThreshold,
            maxLines: Math.Max(MaxLevels * 2, 8));

        // Filter horizontal key levels (Support / Resistance / nearly flat)
        var levels = new List<HoughDetectedLine>();
        foreach (var l in result.Lines)
        {
            if (l.LineType == HoughLineType.Support ||
                l.LineType == HoughLineType.Resistance ||
                Math.Abs(l.Slope) <= 0.08 * CalculatedAtr)
            {
                levels.Add(l);
            }
        }

        levels.Sort((a, b) => b.TouchCount.CompareTo(a.TouchCount));
        if (levels.Count > MaxLevels)
        {
            levels.RemoveRange(MaxLevels, levels.Count - MaxLevels);
        }

        CalculatedLevels = levels;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (CalculatedLevels.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new("Key Levels Count", "Key Levels Count", CalculatedLevels.Count, CalculatedLevels.Count.ToString(), color)
        };

        for (int i = 0; i < CalculatedLevels.Count; i++)
        {
            var l = CalculatedLevels[i];
            var col = l.LineType == HoughLineType.Support
                ? new IndicatorColor(SupportColor.A, SupportColor.R, SupportColor.G, SupportColor.B)
                : new IndicatorColor(ResistanceColor.A, ResistanceColor.R, ResistanceColor.G, ResistanceColor.B);

            list.Add(new(
                $"Level #{i + 1} ({l.LineType})",
                $"Level #{i + 1} ({l.LineType})",
                l.StartPrice,
                $"{l.StartPrice:F2} (Touches={l.TouchCount}, R²={l.RSquared:F2})",
                col));
        }

        return list;
    }

    private static double ComputeAtr(IReadOnlyList<CandleData> candles)
    {
        if (candles.Count < 2) return 1.0;
        double trSum = 0;
        int count = 0;
        for (int i = 1; i < candles.Count; i++)
        {
            double high = (double)candles[i].High;
            double low = (double)candles[i].Low;
            double prevClose = (double)candles[i - 1].Close;
            double tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trSum += tr;
            count++;
        }
        return count > 0 && trSum > 1e-6 ? trSum / count : 1.0;
    }
}
