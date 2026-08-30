using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class KalmanFilterProjectionObject : IChartObject
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.KalmanFilterProjection;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
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
    /// Process noise covariance (Q).
    /// </summary>
    public decimal Q { get; set; } = 0.01m;

    /// <summary>
    /// Measurement noise covariance (R).
    /// </summary>
    public decimal R { get; set; } = 0.1m;

    /// <summary>
    /// Whether to render the confidence interval band (±M*sigma).
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Confidence interval multiplier (M in ±M*sigma, e.g. 1.0 = ~68%, 2.0 = ~95%).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Projected path data: Point.X = timestamp (ticks), Point.Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Upper and Lower confidence interval band path data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.KalmanFilterProjectionRenderer _renderer = new();

    public KalmanFilterProjectionObject()
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

        if (ProjectedPath != null)
        {
            var newPath = new List<StockAnalyzer.Core.Models.Point>();
            foreach (var p in ProjectedPath)
            {
                newPath.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            ProjectedPath = newPath;
        }

        if (UpperBandPath != null)
        {
            var newUpper = new List<StockAnalyzer.Core.Models.Point>();
            foreach (var p in UpperBandPath)
            {
                newUpper.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            UpperBandPath = newUpper;
        }

        if (LowerBandPath != null)
        {
            var newLower = new List<StockAnalyzer.Core.Models.Point>();
            foreach (var p in LowerBandPath)
            {
                newLower.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            LowerBandPath = newLower;
        }
    }

    /// <summary>
    /// Recalculates the 2nd-order Kinematic Kalman Filter state from the selection and extrapolates future steps.
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
        if (count == 0)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            return;
        }

        // 2nd-order Kinematic Kalman Filter: State = [price, velocity]^T
        decimal p = candles[startIndex].Close;
        decimal v = count > 1 ? (candles[startIndex + 1].Close - candles[startIndex].Close) : 0m;

        decimal p00 = 1m;
        decimal p01 = 0m;
        decimal p10 = 0m;
        decimal p11 = 1m;

        decimal qVal = Math.Max(0.00001m, Q);
        decimal rVal = Math.Max(0.00001m, R);

        // Track innovation residuals to estimate empirical price scale (sigma_hat)
        double sumNormalizedResidualSq = 0.0;
        int validResidualCount = 0;

        for (int i = startIndex + 1; i <= endIndex; i++)
        {
            // Predict step:
            // x_pred = [p + v, v]
            decimal pPred = p + v;
            decimal vPred = v;

            // P_pred = F * P * F^T + Q
            decimal predP00 = p00 + p01 + p10 + p11 + qVal;
            decimal predP01 = p01 + p11;
            decimal predP10 = p10 + p11;
            decimal predP11 = p11 + qVal;

            // Update step:
            decimal measurement = candles[i].Close;
            decimal y = measurement - pPred;
            decimal s = predP00 + rVal;

            if (s != 0m)
            {
                decimal k0 = predP00 / s;
                decimal k1 = predP10 / s;

                p = pPred + k0 * y;
                v = vPred + k1 * y;

                p00 = (1m - k0) * predP00;
                p01 = (1m - k0) * predP01;
                p10 = predP10 - k1 * predP00;
                p11 = predP11 - k1 * predP01;

                sumNormalizedResidualSq += (double)(y * y / s);
                validResidualCount++;
            }
            else
            {
                p = pPred;
                v = vPred;
                p00 = predP00;
                p01 = predP01;
                p10 = predP10;
                p11 = predP11;
            }
        }

        // Project forward into future coordinates
        int steps = Math.Clamp(FutureSteps, 1, 100);
        var projectedPoints = new List<StockAnalyzer.Core.Models.Point>(steps + 1);
        var upperPoints = new List<StockAnalyzer.Core.Models.Point>(steps + 1);
        var lowerPoints = new List<StockAnalyzer.Core.Models.Point>(steps + 1);

        // First point connects from the last selection candle
        var lastCandle = candles[endIndex];
        var initialPoint = new StockAnalyzer.Core.Models.Point((double)lastCandle.Timestamp.Ticks, (double)lastCandle.Close);
        projectedPoints.Add(initialPoint);
        upperPoints.Add(initialPoint);
        lowerPoints.Add(initialPoint);

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

        // Compute empirical price innovation scale (sigma_hat) to bring normalized covariance into physical price scale
        double minScale = Math.Max(0.01, (double)Math.Abs(lastCandle.Close) * 0.005);
        double sigmaScale = validResidualCount > 0
            ? Math.Max(minScale, Math.Sqrt(sumNormalizedResidualSq / validResidualCount))
            : minScale;

        decimal pCov00 = p00;
        decimal pCov01 = p01;
        decimal pCov10 = p10;
        decimal pCov11 = p11;

        decimal multiplier = Math.Max(0m, ConfidenceMultiplier);

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

            // Propagate error covariance into future (without measurement updates)
            // P_k = F * P_{k-1} * F^T + Q
            decimal nextCov00 = pCov00 + pCov01 + pCov10 + pCov11 + qVal;
            decimal nextCov01 = pCov01 + pCov11;
            decimal nextCov10 = pCov10 + pCov11;
            decimal nextCov11 = pCov11 + qVal;

            pCov00 = nextCov00;
            pCov01 = nextCov01;
            pCov10 = nextCov10;
            pCov11 = nextCov11;

            decimal projectedPrice = p + (decimal)k * v;
            double stdDev = sigmaScale * Math.Sqrt((double)Math.Max(0m, pCov00));
            decimal margin = (decimal)stdDev * multiplier;

            decimal upperPrice = projectedPrice + margin;
            decimal lowerPrice = projectedPrice - margin;

            projectedPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, (double)projectedPrice));
            upperPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, (double)upperPrice));
            lowerPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, (double)lowerPrice));
        }

        ProjectedPath = projectedPoints;
        UpperBandPath = upperPoints;
        LowerBandPath = lowerPoints;
    }
}
