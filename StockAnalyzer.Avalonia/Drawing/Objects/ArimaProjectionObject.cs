using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class ArimaProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.ArimaProjection;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Visual appearance properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    /// <summary>
    /// Opacity (0-100%) of the selection-range background band.
    /// </summary>
    public int FillOpacity { get; set; } = 10;

    /// <summary>
    /// Color of the selection-range background band.
    /// </summary>
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    /// <summary>
    /// Number of future candles to project forward (1-100).
    /// </summary>
    public int FutureSteps { get; set; } = 20;

    /// <summary>
    /// Autoregressive order p (0-5).
    /// </summary>
    public int P { get; set; } = 1;

    /// <summary>
    /// Degree of differencing d (0-2).
    /// </summary>
    public int D { get; set; } = 1;

    /// <summary>
    /// Moving average order q (0-5).
    /// </summary>
    public int Q { get; set; } = 1;

    /// <summary>
    /// Price source selector for ARIMA model estimation.
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Close;

    /// <summary>
    /// Whether to render the uncertainty confidence interval band.
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Confidence interval multiplier (M in ±M*sigma, e.g. 1.0 = ~68%, 2.0 = ~95%).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Projected trajectory data: Point.X = timestamp ticks, Point.Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Upper and Lower confidence interval band data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    // Analytical model metadata
    public double InnovationVariance { get; private set; }
    public double ResidualStdDev { get; private set; }
    public double TargetPrice { get; private set; }
    public bool IsLowerBandClamped { get; private set; }

    private readonly Renderers.ArimaProjectionRenderer _renderer = new();

    public ArimaProjectionObject()
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
            var newPath = new List<StockAnalyzer.Core.Models.Point>(ProjectedPath.Count);
            foreach (var p in ProjectedPath)
            {
                newPath.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            ProjectedPath = newPath;
        }

        if (UpperBandPath != null)
        {
            var newUpper = new List<StockAnalyzer.Core.Models.Point>(UpperBandPath.Count);
            foreach (var p in UpperBandPath)
            {
                newUpper.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            UpperBandPath = newUpper;
        }

        if (LowerBandPath != null)
        {
            var newLower = new List<StockAnalyzer.Core.Models.Point>(LowerBandPath.Count);
            foreach (var p in LowerBandPath)
            {
                newLower.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            LowerBandPath = newLower;
        }
    }

    /// <summary>
    /// Recalculates ARIMA parameters from the user selection window and extrapolates future steps.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            InnovationVariance = 0.0;
            ResidualStdDev = 0.0;
            TargetPrice = 0.0;
            IsLowerBandClamped = false;
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
            InnovationVariance = 0.0;
            ResidualStdDev = 0.0;
            TargetPrice = 0.0;
            IsLowerBandClamped = false;
            return;
        }

        var result = ArimaProjectionAnalysis.CalculateProjection(
            candles,
            startIndex,
            endIndex,
            p: P,
            d: D,
            q: Q,
            futureSteps: FutureSteps,
            priceSource: PriceSource,
            timeframeSpan: timeframeSpan,
            showConfidenceBand: ShowConfidenceBand,
            confidenceMultiplier: ConfidenceMultiplier);

        ProjectedPath = result.ProjectedPoints.ToList();
        UpperBandPath = result.UpperBandPoints.ToList();
        LowerBandPath = result.LowerBandPoints.ToList();
        InnovationVariance = result.InnovationVariance;
        ResidualStdDev = result.ResidualStdDev;
        TargetPrice = result.TargetPrice;
        IsLowerBandClamped = result.IsLowerBandClamped;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (ProjectedPath == null || ProjectedPath.Count <= 1) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new(
                "ARIMA Order",
                "ARIMA Order",
                0m,
                $"({P}, {D}, {Q})",
                color),
            new(
                "ARIMA Innovation Variance",
                "ARIMA Innovation Variance",
                (decimal)InnovationVariance,
                $"{InnovationVariance:F4}",
                color),
            new(
                "ARIMA Residual StdDev",
                "ARIMA Residual StdDev",
                (decimal)ResidualStdDev,
                $"{ResidualStdDev:F4}",
                color),
            new(
                "ARIMA Forecast Horizon",
                "ARIMA Forecast Horizon",
                FutureSteps,
                $"{FutureSteps} bars",
                color),
            new(
                "ARIMA Target Price",
                "ARIMA Target Price",
                (decimal)TargetPrice,
                IsLowerBandClamped ? $"{TargetPrice:F2} (Clamped)" : $"{TargetPrice:F2}",
                color)
        };

        return list;
    }
}
