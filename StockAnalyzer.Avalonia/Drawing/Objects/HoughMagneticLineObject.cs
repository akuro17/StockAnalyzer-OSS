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

public class HoughMagneticLineObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public virtual ChartObjectType Type => ChartObjectType.HoughMagneticLine;

    public List<ChartPoint> Points { get; } = new(2);

    // Visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness * 1.5;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Background band
    public int FillOpacity { get; set; } = 10;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    // Parameters
    public int PivotWindow { get; set; } = 3;
    public int VoteThreshold { get; set; } = 3;
    public HoughNormalizationMode Normalization { get; set; } = HoughNormalizationMode.MinMax;
    public bool ExtendRight { get; set; } = true;
    public bool ShowLabels { get; set; } = true;

    // Results
    public HoughDetectedLine? CalculatedLine { get; set; }
    public DateTime SliceStartTime { get; set; }
    public DateTime SliceEndTime { get; set; }

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    private readonly Renderers.HoughMagneticLineRenderer _renderer = new();

    public HoughMagneticLineObject()
    {
    }

    public void InvalidateCache()
    {
        CalculatedLine = null;
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

        if (ExtendRight && CalculatedLine.HasValue)
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
            CalculatedLine = null;
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
            CalculatedLine = null;
            return;
        }

        int count = endIndex - startIndex + 1;
        int minRequired = Math.Max(5, PivotWindow * 2 + 1);
        if (count < minRequired)
        {
            CalculatedLine = null;
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

        var result = HoughTransformEngine.DetectLinesFromCandles(
            slice,
            lookback: count,
            pivotWindow: PivotWindow,
            voteThreshold: VoteThreshold,
            maxLines: 1,
            normalization: Normalization,
            useVolumeWeight: true);

        CalculatedLine = result.Lines.Count > 0 ? result.Lines[0] : null;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (!CalculatedLine.HasValue) return Array.Empty<DrawingCalculatedValue>();

        var line = CalculatedLine.Value;
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        return new List<DrawingCalculatedValue>
        {
            new("Magnetic Line Type", "Magnetic Line Type", (decimal)line.LineType, line.LineType.ToString(), color),
            new("Slope", "Slope", (decimal)line.Slope, $"{line.Slope:F4} / bar", color),
            new("Touch Count", "Touch Count", line.TouchCount, line.TouchCount.ToString(), color),
            new("RSquared", "RSquared", (decimal)line.RSquared, $"{line.RSquared:F3}", color),
            new("Strength Score", "Strength Score", (decimal)line.Strength, $"{line.Strength:F1}", color),
            new("Start Price", "Start Price", line.StartPrice, $"{line.StartPrice:F2}", color),
            new("End Price", "End Price", line.EndPrice, $"{line.EndPrice:F2}", color)
        };
    }
}
