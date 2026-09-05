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

public class PearsonProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.PearsonProjection;

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
    /// Minimum Pearson correlation coefficient threshold (e.g. 0.70).
    /// </summary>
    public double MinCorrelation { get; set; } = 0.70;

    /// <summary>
    /// Number of top similar historical matches to ensemble (1 = single best match).
    /// </summary>
    public int TopK { get; set; } = 1;

    /// <summary>
    /// Price source selector used for correlation calculation.
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
    /// Whether to scale historical return amplitude by the ratio of query volatility to matched window volatility.
    /// </summary>
    public bool ApplyVolatilityScaling { get; set; } = true;

    /// <summary>
    /// Whether to remove linear trend before calculating Pearson correlation (detrending).
    /// </summary>
    public bool ApplyDetrend { get; set; } = false;

    /// <summary>
    /// Whether to render the confidence interval band (±M*sigma).
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Confidence interval multiplier (M in ±M*sigma, e.g. 1.0 = ~68%, 2.0 = ~95%).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    /// <summary>
    /// Whether to highlight the best matched historical region on the chart.
    /// </summary>
    public bool ShowMatchHighlight { get; set; } = true;

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
    public double BestCorrelation { get; set; }
    public IReadOnlyList<MatchedPatternInfo> MatchedPatterns { get; set; } = Array.Empty<MatchedPatternInfo>();

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.PearsonProjectionRenderer _renderer = new();

    public PearsonProjectionObject()
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
    /// Recalculates the Pearson Correlation pattern search and extrapolates future projection points.
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
        if (count < PearsonProjectionAnalysis.MinSampleCount)
        {
            ClearProjection();
            return;
        }

        Func<CoreCandleData, double> selector = c => (double)PriceDataHelper.ExtractPrice(c, PriceSource);

        var samples = new List<double>(candles.Count);
        var timestamps = new List<DateTime>(candles.Count);

        for (int i = 0; i < candles.Count; i++)
        {
            samples.Add(selector(candles[i]));
            timestamps.Add(candles[i].Timestamp);
        }

        var result = PearsonProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            queryStartIndex: startIndex,
            queryEndIndex: endIndex,
            futureSteps: FutureSteps,
            minCorrelation: MinCorrelation,
            topK: TopK,
            applyVolatilityScaling: ApplyVolatilityScaling,
            applyDetrend: ApplyDetrend,
            showConfidenceBand: ShowConfidenceBand,
            confidenceMultiplier: ConfidenceMultiplier,
            timeframeSpan: timeframeSpan);

        if (!result.HasMatch)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            MatchedStartTime = null;
            MatchedEndTime = null;
            BestCorrelation = 0.0;
            MatchedPatterns = Array.Empty<MatchedPatternInfo>();
            IsUnmatched = true;
            return;
        }

        ProjectedPath = result.ProjectedPoints.ToList();
        UpperBandPath = result.UpperBandPoints.ToList();
        LowerBandPath = result.LowerBandPoints.ToList();
        MatchedStartTime = result.MatchedStartTime;
        MatchedEndTime = result.MatchedEndTime;
        BestCorrelation = result.BestCorrelation;
        MatchedPatterns = result.MatchedPatterns;
        IsUnmatched = false;
    }

    private void ClearProjection()
    {
        ProjectedPath?.Clear();
        UpperBandPath?.Clear();
        LowerBandPath?.Clear();
        MatchedStartTime = null;
        MatchedEndTime = null;
        BestCorrelation = 0.0;
        MatchedPatterns = Array.Empty<MatchedPatternInfo>();
        IsUnmatched = true;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (!HasMatch) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>
        {
            new DrawingCalculatedValue(
                "Pearson Correlation (r)",
                "Pearson Correlation (r)",
                (decimal)BestCorrelation,
                $"{BestCorrelation:F3} ({BestCorrelation * 100:F1}%)",
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
