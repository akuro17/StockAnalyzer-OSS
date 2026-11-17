using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class KalmanFilterProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    /// <summary>Data Window row name (key and label) of <see cref="KalmanProjectionResult.InnovationScale"/>: the band scale in price units (see DRAWING_TOOLS_SPEC.md).</summary>
    public const string BandScaleRowName = "Kalman Band Scale";

    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.KalmanFilterProjection;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties
    private Color _color = DrawingThemeContext.DefaultColor;
    public Color Color { get => _color; set { _color = value; InvalidateCalculatedValues(); } }
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
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
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get => _projectedPath; set { _projectedPath = value; InvalidateCalculatedValues(); } }
    private List<StockAnalyzer.Core.Models.Point> _projectedPath = new();

    // Upper and Lower confidence interval band path data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    /// <summary>Last Kalman state of <see cref="Recalculate"/> (terminal velocity, gains, innovation scale); null when nothing is selected.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public KalmanProjectionResult? CalculatedResult { get => _calculatedResult; private set { _calculatedResult = value; InvalidateCalculatedValues(); } }
    private KalmanProjectionResult? _calculatedResult;

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
            CalculatedResult = null;
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
            CalculatedResult = null;
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count == 0)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            CalculatedResult = null;
            return;
        }

        // The filter itself lives in KalmanProjectionAnalysis (single source of truth); this method maps it onto the chart.
        var closes = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            closes[i] = candles[startIndex + i].Close;
        }

        var result = KalmanProjectionAnalysis.CalculateProjection(closes, FutureSteps, Q, R, ConfidenceMultiplier);
        if (result.IsEmpty)
        {
            // The engine refused a band or scale value (non-finite / out of range): draw nothing, keep no start point.
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            CalculatedResult = null;
            return;
        }

        CalculatedResult = result;

        // Project forward into future coordinates
        var projectedPoints = new List<StockAnalyzer.Core.Models.Point>(result.Steps.Count + 1);
        var upperPoints = new List<StockAnalyzer.Core.Models.Point>(result.Steps.Count + 1);
        var lowerPoints = new List<StockAnalyzer.Core.Models.Point>(result.Steps.Count + 1);

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

        var steps = result.Steps;
        for (int k = 1; k <= steps.Count; k++)
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

            var step = steps[k - 1];
            projectedPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, (double)step.ProjectedPrice));
            upperPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, (double)step.UpperBand));
            lowerPoints.Add(new StockAnalyzer.Core.Models.Point((double)targetTime.Ticks, (double)step.LowerBand));
        }

        ProjectedPath = projectedPoints;
        UpperBandPath = upperPoints;
        LowerBandPath = lowerPoints;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        // ChartInteractionController clears the paths IN PLACE while a handle is dragged, which no setter sees:
        // an empty path means no rows whatever the cache holds (O(1), no allocation).
        if (ProjectedPath.Count == 0) return Array.Empty<DrawingCalculatedValue>();
        return _calculatedValuesCache ??= BuildCalculatedValues();
    }

    // Rows depend only on CalculatedResult, ProjectedPath (first and last point) and Color; their setters call InvalidateCalculatedValues.
    // ProjectedPath is replaced by Recalculate and Translate; in-place Clear() calls from outside are covered by the empty-path guard in GetCalculatedValues.
    private DrawingCalculatedValue[]? _calculatedValuesCache;

    private void InvalidateCalculatedValues() => _calculatedValuesCache = null;

    private DrawingCalculatedValue[] BuildCalculatedValues()
    {
        var result = CalculatedResult;
        if (result == null || result.IsEmpty || ProjectedPath.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        decimal anchor = (decimal)ProjectedPath[0].Y;
        decimal target = (decimal)ProjectedPath[^1].Y;
        decimal pctChange = anchor != 0m ? (target - anchor) / anchor * 100m : 0m;
        decimal velocity = result.TerminalVelocity;

        return new DrawingCalculatedValue[]
        {
            new("Kalman Target Price", "Kalman Target Price", target, string.Create(DrawingValueFormat.Culture, $"{target:F2} ({pctChange:+0.00;-0.00;0.00}%)"), color),
            new("Kalman Velocity (v)", "Kalman Velocity (v)", Math.Round(velocity, 4), string.Create(DrawingValueFormat.Culture, $"{velocity:+0.0000;-0.0000;0.0000} / bar"),
                velocity >= 0m ? IndicatorColor.Bullish : IndicatorColor.Bearish),
            new("Kalman Gain (K0, K1)", "Kalman Gain (K0, K1)", Math.Round(result.TerminalGainPosition, 4),
                string.Create(DrawingValueFormat.Culture, $"K0={result.TerminalGainPosition:F3}, K1={result.TerminalGainVelocity:F3}"), IndicatorColor.Gray),
            new(BandScaleRowName, BandScaleRowName, Math.Round(result.InnovationScale, 4), string.Create(DrawingValueFormat.Culture, $"{result.InnovationScale:F4}"), IndicatorColor.Gray)
        };
    }
}
