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

public class FrechetProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FrechetProjection;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public Color UnmatchedColor { get; set; } = global::Avalonia.Media.Color.Parse(StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultDtwUnmatchedColor);
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = -10; // Placed behind candles
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
    /// Number of future candles to project forward.
    /// </summary>
    public int FutureSteps { get; set; } = 20;

    /// <summary>
    /// Price source selector used for Fréchet distance calculation.
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Close;

    /// <summary>
    /// Backward-compatibility property for PriceField.
    /// </summary>
    public PriceField PriceField
    {
        get => PriceSource switch
        {
            PriceType.Close => PriceField.Close,
            PriceType.Open => PriceField.Open,
            PriceType.High => PriceField.High,
            PriceType.Low => PriceField.Low,
            PriceType.Median => PriceField.MedianHL,
            PriceType.Typical => PriceField.TypicalHLC,
            PriceType.Weighted => PriceField.WeightedHLCC,
            _ => PriceField.Close
        };
        set => PriceSource = value switch
        {
            PriceField.Close => PriceType.Close,
            PriceField.Open => PriceType.Open,
            PriceField.High => PriceType.High,
            PriceField.Low => PriceType.Low,
            PriceField.MedianHL => PriceType.Median,
            PriceField.TypicalHLC => PriceType.Typical,
            PriceField.WeightedHLCC => PriceType.Weighted,
            _ => PriceType.Close
        };
    }

    /// <summary>
    /// Whether to render the confidence interval band.
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Confidence interval multiplier (M in ±M*sigma, default: 2.0).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    /// <summary>
    /// Whether to highlight the best matched historical region on the chart.
    /// </summary>
    public bool ShowMatchHighlight { get; set; } = true;

    /// <summary>
    /// Maximum acceptable Discrete Fréchet Distance threshold (0.0 = unbounded/unconstrained).
    /// </summary>
    public double MaxDistance { get; set; } = 0.0;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaUnmatchedColor => new(UnmatchedColor.R, UnmatchedColor.G, UnmatchedColor.B, UnmatchedColor.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    public bool IsUnmatched { get; set; } = false;
    public bool HasMatch => ProjectedPath != null && ProjectedPath.Count > 0 && !IsUnmatched;

    // Projected path data: Point.X = timestamp (ticks), Point.Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Upper and Lower confidence interval band path data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    // Matched pattern metadata
    public DateTime? MatchedStartTime { get; set; }
    public DateTime? MatchedEndTime { get; set; }
    public double Distance { get; set; }

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.FrechetProjectionRenderer _renderer = new();

    public FrechetProjectionObject()
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
        IsUnmatched = false;
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
    /// Recalculates the Discrete Fréchet Distance historical pattern search and forward projections.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ClearProjection();
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

        if (startIndex < 0 || endIndex < 0 || startIndex >= endIndex)
        {
            ClearProjection();
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < FrechetDistanceAnalysis.MinSampleCount)
        {
            ClearProjection();
            return;
        }

        double maxDist = MaxDistance > 0.0 ? MaxDistance : double.MaxValue;

        var result = FrechetDistanceAnalysis.CalculateProjection(
            candles,
            queryStartIndex: startIndex,
            queryEndIndex: endIndex,
            horizon: FutureSteps,
            priceType: PriceSource,
            timeframeSpan: timeframeSpan,
            maxDistance: maxDist,
            confidenceMultiplier: (double)ConfidenceMultiplier);

        if (result == null || result.Projections.Count == 0)
        {
            ClearProjection();
            return;
        }

        MatchedStartTime = candles[result.MatchedStartIndex].Timestamp;
        MatchedEndTime = candles[result.MatchedEndIndex].Timestamp;
        Distance = result.Distance;

        // Base anchor point for smooth continuous trajectory line
        var currentPrice = (double)PriceDataHelper.ExtractPrice(candles[endIndex], PriceSource);
        var basePoint = new StockAnalyzer.Core.Models.Point(candles[endIndex].Timestamp.Ticks, currentPrice);

        var projected = new List<StockAnalyzer.Core.Models.Point>(result.Projections.Count + 1) { basePoint };
        var upper = new List<StockAnalyzer.Core.Models.Point>(result.Projections.Count + 1) { basePoint };
        var lower = new List<StockAnalyzer.Core.Models.Point>(result.Projections.Count + 1) { basePoint };

        for (int i = 0; i < result.Projections.Count; i++)
        {
            var pt = result.Projections[i];
            long ticks = pt.Timestamp.Ticks;
            projected.Add(new StockAnalyzer.Core.Models.Point(ticks, (double)pt.PredictedPrice));
            upper.Add(new StockAnalyzer.Core.Models.Point(ticks, (double)pt.UpperBand));
            lower.Add(new StockAnalyzer.Core.Models.Point(ticks, (double)pt.LowerBand));
        }

        ProjectedPath = projected;
        UpperBandPath = upper;
        LowerBandPath = lower;
        IsUnmatched = false;
    }

    private void ClearProjection()
    {
        ProjectedPath?.Clear();
        UpperBandPath?.Clear();
        LowerBandPath?.Clear();
        MatchedStartTime = null;
        MatchedEndTime = null;
        Distance = 0.0;
        IsUnmatched = true;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (!HasMatch) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new DrawingCalculatedValue(
                "Fréchet Distance",
                "Fréchet Distance",
                (decimal)Distance,
                $"{Distance:F3}",
                color)
        };

        if (MatchedStartTime.HasValue && MatchedEndTime.HasValue)
        {
            list.Add(new DrawingCalculatedValue(
                "Matched Range",
                "Matched Range",
                0m,
                $"{MatchedStartTime.Value:yyyy/MM/dd} ~ {MatchedEndTime.Value:yyyy/MM/dd}",
                color));
        }

        if (ProjectedPath.Count > 1)
        {
            var startPrice = (decimal)ProjectedPath[0].Y;
            var endPrice = (decimal)ProjectedPath[^1].Y;
            var pctChange = startPrice != 0m ? (endPrice - startPrice) / startPrice * 100m : 0m;

            list.Add(new DrawingCalculatedValue(
                "Projected Target",
                "Projected Target",
                endPrice,
                $"{endPrice:F2} ({pctChange:+0.00;-0.00}%)",
                color));
        }

        return list;
    }
}
