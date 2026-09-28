using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class ClothoidCurveProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.ClothoidCurveProjection;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>
    /// Opacity (0-100%) of the light selection-range background band drawn between the start/end points.
    /// </summary>
    public int FillOpacity { get; set; } = 10;

    /// <summary>
    /// Color of the light selection-range background band drawn between the start/end points.
    /// </summary>
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    /// <summary>
    /// Number of future candles to project forward.
    /// </summary>
    public int FutureSteps { get; set; } = 20;

    /// <summary>
    /// Linear curvature variation rate (c = dkappa/ds). Positive curves upward, negative downward.
    /// </summary>
    public decimal CurvatureRate { get; set; } = 0.05m;

    /// <summary>
    /// Whether to automatically invert curvature rate polarity to match selection range trend direction.
    /// Default is true so that downward trends curve downward.
    /// </summary>
    public bool AutoPolarity { get; set; } = true;

    /// <summary>
    /// Price source selector used for trajectory calculations.
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Close;

    /// <summary>
    /// Whether to render the confidence interval band.
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Confidence interval multiplier (M in +-M*sigma*sqrt(k), e.g. 1.0 = ~68%, 2.0 = ~95%).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Projected path data: Point.X = timestamp (ticks), Point.Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Upper and Lower confidence interval band path data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.ClothoidCurveProjectionRenderer _renderer = new();

    public ClothoidCurveProjectionObject()
    {
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

        double dtTicks = timeDelta.Ticks;
        double dp = (double)priceDelta;

        if (ProjectedPath != null)
        {
            for (int i = 0; i < ProjectedPath.Count; i++)
            {
                var p = ProjectedPath[i];
                ProjectedPath[i] = new StockAnalyzer.Core.Models.Point(p.X + dtTicks, p.Y + dp);
            }
        }

        if (UpperBandPath != null)
        {
            for (int i = 0; i < UpperBandPath.Count; i++)
            {
                var p = UpperBandPath[i];
                UpperBandPath[i] = new StockAnalyzer.Core.Models.Point(p.X + dtTicks, p.Y + dp);
            }
        }

        if (LowerBandPath != null)
        {
            for (int i = 0; i < LowerBandPath.Count; i++)
            {
                var p = LowerBandPath[i];
                LowerBandPath[i] = new StockAnalyzer.Core.Models.Point(p.X + dtTicks, p.Y + dp);
            }
        }
    }

    /// <summary>
    /// Recalculates the Clothoid curve trajectory and confidence interval bounds from the selected candle region.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
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
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < 2)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            return;
        }

        decimal[]? rentedPrices = null;
        Span<decimal> prices = count <= MathBufferLimits.DecimalStackAllocThreshold
            ? stackalloc decimal[count]
            : (rentedPrices = System.Buffers.ArrayPool<decimal>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            decimal? prevClose = startIndex > 0 ? candles[startIndex - 1].Close : null;
            decimal? prevHaOpen = null;
            decimal? prevHaClose = null;

            for (int i = 0; i < count; i++)
            {
                var c = candles[startIndex + i];
                prices[i] = PriceDataHelper.ExtractPrice(c, PriceSource, prevClose, prevHaOpen, prevHaClose);

                prevClose = c.Close;
                if (prevHaOpen.HasValue && prevHaClose.HasValue)
                {
                    decimal haClose = (c.Open + c.High + c.Low + c.Close) / 4.0m;
                    decimal haOpen = (prevHaOpen.Value + prevHaClose.Value) / 2.0m;
                    prevHaOpen = haOpen;
                    prevHaClose = haClose;
                }
                else
                {
                    prevHaOpen = (c.Open + c.Close) / 2.0m;
                    prevHaClose = (c.Open + c.High + c.Low + c.Close) / 4.0m;
                }
            }

            double cRate = (double)CurvatureRate;
            if (AutoPolarity && count >= 2)
            {
                decimal deltaP = prices[count - 1] - prices[0];
                if (deltaP < 0m && cRate > 0.0)
                {
                    cRate = -cRate;
                }
                else if (deltaP > 0m && cRate < 0.0)
                {
                    cRate = -cRate;
                }
            }
            var state = ClothoidMath.EstimateInitialState(prices, cRate);

            int steps = Math.Clamp(FutureSteps, 1, 100);
            Span<double> projPrices = stackalloc double[steps];
            Span<double> upperPrices = stackalloc double[steps];
            Span<double> lowerPrices = stackalloc double[steps];

            double confMult = (double)Math.Max(0m, ConfidenceMultiplier);
            ClothoidMath.GenerateTrajectory(state, steps, confMult, projPrices, upperPrices, lowerPrices);

            if (timeframeSpan <= TimeSpan.Zero)
            {
                if (candles.Count >= 2)
                {
                    double avgMs = (candles[^1].Timestamp - candles[0].Timestamp).TotalMilliseconds / (candles.Count - 1);
                    if (avgMs > 0)
                    {
                        timeframeSpan = TimeSpan.FromMilliseconds(avgMs);
                    }
                }
                if (timeframeSpan <= TimeSpan.Zero)
                {
                    timeframeSpan = TimeSpan.FromDays(1);
                }
            }

            ProjectedPath ??= new List<StockAnalyzer.Core.Models.Point>(steps + 1);
            ProjectedPath.Clear();
            if (ProjectedPath.Capacity < steps + 1) ProjectedPath.Capacity = steps + 1;

            UpperBandPath ??= new List<StockAnalyzer.Core.Models.Point>(steps + 1);
            UpperBandPath.Clear();
            if (UpperBandPath.Capacity < steps + 1) UpperBandPath.Capacity = steps + 1;

            LowerBandPath ??= new List<StockAnalyzer.Core.Models.Point>(steps + 1);
            LowerBandPath.Clear();
            if (LowerBandPath.Capacity < steps + 1) LowerBandPath.Capacity = steps + 1;

            // Connect from the last observation candle using selected price source
            var lastCandle = candles[endIndex];
            var initialPoint = new StockAnalyzer.Core.Models.Point((double)lastCandle.Timestamp.Ticks, (double)prices[count - 1]);
            ProjectedPath.Add(initialPoint);
            UpperBandPath.Add(initialPoint);
            LowerBandPath.Add(initialPoint);

            for (int k = 1; k <= steps; k++)
            {
                int targetIndex = endIndex + k;
                DateTime targetTime;

                if (targetIndex < candles.Count)
                {
                    targetTime = candles[targetIndex].Timestamp;
                }
                else
                {
                    int extendedSteps = targetIndex - candles.Count + 1;
                    targetTime = candles[^1].Timestamp + (timeframeSpan * extendedSteps);
                }

                int idx = k - 1;
                ProjectedPath.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, projPrices[idx]));
                UpperBandPath.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, upperPrices[idx]));
                LowerBandPath.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, lowerPrices[idx]));
            }
        }
        finally
        {
            if (rentedPrices != null)
            {
                System.Buffers.ArrayPool<decimal>.Shared.Return(rentedPrices);
            }
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (ProjectedPath == null || ProjectedPath.Count <= 1) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        return new List<DrawingCalculatedValue>
        {
            new("Clothoid Steps", "Steps", FutureSteps, FutureSteps.ToString(), color),
            new("Clothoid Curvature Rate", "Curvature Rate", CurvatureRate, CurvatureRate.ToString("0.0000"), color),
            new("Clothoid Multiplier", "Confidence Multiplier", ConfidenceMultiplier, $"{ConfidenceMultiplier:0.0}σ", color)
        };
    }
}
