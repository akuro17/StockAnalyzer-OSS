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

public readonly record struct HoughFanRay(
    double AngleDegrees,
    double SlopePrice,
    int Votes,
    double Strength
);

public class HoughResonantFanObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public virtual ChartObjectType Type => ChartObjectType.HoughResonantFan;

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

    // Fan parameters
    public int PivotWindow { get; set; } = 3;
    public double AngleBinDegrees { get; set; } = 1.0;
    public int MinVotes { get; set; } = 3;
    public int MaxFanLines { get; set; } = 6;
    public bool ExtendRight { get; set; } = true;
    public bool ShowLabels { get; set; } = true;

    // Results
    public IReadOnlyList<HoughFanRay> CalculatedFanRays { get; set; } = Array.Empty<HoughFanRay>();
    public DateTime OriginTime { get; set; }
    public decimal OriginPrice { get; set; }
    public DateTime SliceEndTime { get; set; }
    public int TotalSliceBars { get; set; }

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    private readonly Renderers.HoughResonantFanRenderer _renderer = new();

    public HoughResonantFanObject()
    {
    }

    public void InvalidateCache()
    {
        CalculatedFanRays = Array.Empty<HoughFanRay>();
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

        if (ExtendRight && CalculatedFanRays.Count > 0)
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

        if (SliceEndTime != default) SliceEndTime += timeDelta;
        if (OriginTime != default) OriginTime += timeDelta;
        OriginPrice += priceDelta;

        InvalidateCache();
    }

    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            CalculatedFanRays = Array.Empty<HoughFanRay>();
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
            CalculatedFanRays = Array.Empty<HoughFanRay>();
            return;
        }

        int count = endIndex - startIndex + 1;
        int minRequired = Math.Max(5, PivotWindow * 2 + 1);
        if (count < minRequired)
        {
            CalculatedFanRays = Array.Empty<HoughFanRay>();
            return;
        }

        OriginTime = candles[startIndex].Timestamp;
        OriginPrice = Points[0].Price;
        SliceEndTime = candles[endIndex].Timestamp;
        TotalSliceBars = count;

        var slice = new CandleData[count];
        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            slice[i] = new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
        }

        double atr = ComputeAtr(slice);

        // Extract pivots
        var pivotBuffer = new List<FractalPivot>(count / 2);
        PivotDetectionEngine.ExtractPivots(slice, PivotWindow, PivotWindow, pivotBuffer);

        int maxConfirmedBar = count - 1 - PivotWindow;
        var originPriceD = (double)OriginPrice;

        // 1D Accumulator: Angular histogram
        // Bins from -85 deg to +85 deg
        double binStep = Math.Clamp(AngleBinDegrees, 0.5, 10.0);
        int binCount = (int)Math.Ceiling(180.0 / binStep);
        var votes = new int[binCount];
        var binSlopes = new List<double>[binCount];
        for (int b = 0; b < binCount; b++) binSlopes[b] = new List<double>();

        for (int i = 0; i < pivotBuffer.Count; i++)
        {
            var p = pivotBuffer[i];
            if (p.Index <= 0 || p.Index > maxConfirmedBar) continue;

            double dx = p.Index;
            double dy = (double)p.Price - originPriceD;
            double slope = dy / dx;

            // Normalized angle using ATR as price scale
            double normSlope = slope / atr;
            double angleDeg = Math.Atan(normSlope) * (180.0 / Math.PI);

            int bin = (int)Math.Floor((angleDeg + 90.0) / binStep);
            if (bin >= 0 && bin < binCount)
            {
                votes[bin]++;
                binSlopes[bin].Add(slope);
            }
        }

        // Detect peaks with vote >= MinVotes
        var peakBins = new List<(int Bin, int Votes, double MedianSlope)>();
        for (int b = 0; b < binCount; b++)
        {
            if (votes[b] >= MinVotes)
            {
                // Simple 1D NMS: local maximum
                bool isLocalMax = true;
                if (b > 0 && votes[b - 1] > votes[b]) isLocalMax = false;
                if (b < binCount - 1 && votes[b + 1] > votes[b]) isLocalMax = false;

                if (isLocalMax)
                {
                    binSlopes[b].Sort();
                    double medianSlope = binSlopes[b][binSlopes[b].Count / 2];
                    peakBins.Add((b, votes[b], medianSlope));
                }
            }
        }

        peakBins.Sort((a, b) => b.Votes.CompareTo(a.Votes));
        var rays = new List<HoughFanRay>();
        int takeCount = Math.Min(MaxFanLines, peakBins.Count);

        for (int k = 0; k < takeCount; k++)
        {
            var peak = peakBins[k];
            double angleDeg = (peak.Bin + 0.5) * binStep - 90.0;
            double strength = (double)peak.Votes / Math.Max(1, pivotBuffer.Count) * 100.0;
            rays.Add(new HoughFanRay(angleDeg, peak.MedianSlope, peak.Votes, strength));
        }

        CalculatedFanRays = rays;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (CalculatedFanRays.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new("Resonant Rays", "Resonant Rays", CalculatedFanRays.Count, CalculatedFanRays.Count.ToString(), color)
        };

        for (int i = 0; i < CalculatedFanRays.Count; i++)
        {
            var ray = CalculatedFanRays[i];
            decimal endPrice = OriginPrice + (decimal)(ray.SlopePrice * (TotalSliceBars - 1));

            list.Add(new(
                $"Ray #{i + 1} ({ray.AngleDegrees:F1}°)",
                $"Ray #{i + 1} ({ray.AngleDegrees:F1}°)",
                endPrice,
                $"Slope={ray.SlopePrice:F3}, Votes={ray.Votes}, End={endPrice:F2}",
                color));
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
