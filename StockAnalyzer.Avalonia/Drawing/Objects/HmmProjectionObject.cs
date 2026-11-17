using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class HmmProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.HmmProjection;

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
    /// Number of hidden regime states (2 or 3).
    /// </summary>
    public int States { get => _states; set { _states = value; InvalidateCalculatedValues(); } }
    private int _states = 2;

    /// <summary>
    /// Maximum iterations for Baum-Welch EM convergence.
    /// </summary>
    public int MaxIterations { get; set; } = 30;

    /// <summary>
    /// Convergence log-likelihood threshold for EM termination.
    /// </summary>
    public double Tolerance { get; set; } = 1e-4;

    /// <summary>
    /// Price source selector used for HMM return calculation (SSoT: PriceType).
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Median;

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
            _ => PriceField.MedianHL
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
            _ => PriceType.Median
        };
    }

    /// <summary>
    /// Whether to render the confidence interval band (+-M*sigma).
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Confidence interval multiplier (M in +-M*sigma, e.g. 1.0 = ~68%, 2.0 = ~95%).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Projected path data: Point.X = timestamp (ticks), Point.Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Upper and Lower confidence interval band path data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    // HMM estimation results
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<double> FilteredStateProbabilities { get => _filteredStateProbabilities; set { _filteredStateProbabilities = value; InvalidateCalculatedValues(); } }
    private IReadOnlyList<double> _filteredStateProbabilities = Array.Empty<double>();
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<double> StateMeans { get => _stateMeans; set { _stateMeans = value; InvalidateCalculatedValues(); } }
    private IReadOnlyList<double> _stateMeans = Array.Empty<double>();
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<double> StateStdDevs { get => _stateStdDevs; set { _stateStdDevs = value; InvalidateCalculatedValues(); } }
    private IReadOnlyList<double> _stateStdDevs = Array.Empty<double>();
    [System.Text.Json.Serialization.JsonIgnore]
    public double[,] TransitionMatrix { get => _transitionMatrix; set { _transitionMatrix = value; InvalidateCalculatedValues(); } }
    private double[,] _transitionMatrix = new double[0, 0];
    [System.Text.Json.Serialization.JsonIgnore]
    public int CurrentRegimeIndex { get => _currentRegimeIndex; set { _currentRegimeIndex = value; InvalidateCalculatedValues(); } }
    private int _currentRegimeIndex;
    [System.Text.Json.Serialization.JsonIgnore]
    public double BullStateProbability { get => _bullStateProbability; set { _bullStateProbability = value; InvalidateCalculatedValues(); } }
    private double _bullStateProbability;

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.HmmProjectionRenderer _renderer = new();

    public HmmProjectionObject()
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
    /// Recalculates the Gaussian HMM parameters from the selection and extrapolates future steps.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ClearResults();
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
            ClearResults();
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < HmmProjectionAnalysis.MinSampleCount)
        {
            ClearResults();
            return;
        }

        // Heikin-Ashi sources are recursive over the whole series: fill from candles[0] (PriceDataHelper.FillPrices);
        // True High / True Low use the previous bar's close.
        var prices = new decimal[count];
        PriceDataHelper.FillPrices(candles, startIndex, prices, PriceSource);

        var samples = new List<double>(count);
        var timestamps = new List<DateTime>(count);

        for (int i = startIndex; i <= endIndex; i++)
        {
            samples.Add((double)prices[i - startIndex]);
            timestamps.Add(candles[i].Timestamp);
        }

        var result = HmmProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: FutureSteps,
            timeframeSpan: timeframeSpan,
            states: States,
            maxIterations: MaxIterations,
            tolerance: Tolerance,
            showConfidenceBand: ShowConfidenceBand,
            confidenceMultiplier: ConfidenceMultiplier);

        ProjectedPath = result.ProjectedPoints.ToList();
        UpperBandPath = result.UpperBandPoints.ToList();
        LowerBandPath = result.LowerBandPoints.ToList();
        FilteredStateProbabilities = result.FilteredStateProbabilities;
        StateMeans = result.StateMeans;
        StateStdDevs = result.StateStdDevs;
        TransitionMatrix = result.TransitionMatrix;
        CurrentRegimeIndex = result.CurrentRegimeIndex;
        BullStateProbability = result.BullStateProbability;
    }

    private void ClearResults()
    {
        ProjectedPath?.Clear();
        UpperBandPath?.Clear();
        LowerBandPath?.Clear();
        FilteredStateProbabilities = Array.Empty<double>();
        StateMeans = Array.Empty<double>();
        StateStdDevs = Array.Empty<double>();
        TransitionMatrix = new double[0, 0];
        CurrentRegimeIndex = 0;
        BullStateProbability = 0.0;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
        => _calculatedValuesCache ??= BuildCalculatedValues();

    // Rows depend only on Color, States and the estimation results (FilteredStateProbabilities, StateMeans, StateStdDevs, TransitionMatrix,
    // CurrentRegimeIndex, BullStateProbability), which are always assigned as a whole; every setter calls InvalidateCalculatedValues.
    private DrawingCalculatedValue[]? _calculatedValuesCache;

    private void InvalidateCalculatedValues() => _calculatedValuesCache = null;

    private DrawingCalculatedValue[] BuildCalculatedValues()
    {
        if (FilteredStateProbabilities == null || FilteredStateProbabilities.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>();

        string regimeLabel = States switch
        {
            2 => CurrentRegimeIndex == 1 ? "Bullish" : "Bearish",
            3 => CurrentRegimeIndex switch
            {
                2 => "Bullish",
                1 => "Neutral",
                _ => "Bearish"
            },
            _ => $"State #{CurrentRegimeIndex + 1}"
        };

        list.Add(new DrawingCalculatedValue(
            "HMM Current Regime",
            "HMM Current Regime",
            (decimal)CurrentRegimeIndex,
            regimeLabel,
            color));

        list.Add(new DrawingCalculatedValue(
            "HMM Bull Probability",
            "HMM Bull Probability",
            (decimal)(BullStateProbability * 100.0),
            $"{BullStateProbability * 100.0:N2}%",
            color));

        for (int i = 0; i < StateMeans.Count; i++)
        {
            list.Add(new DrawingCalculatedValue(
                $"HMM State #{i + 1} Mean Return",
                $"HMM State #{i + 1} Mean Return",
                (decimal)StateMeans[i],
                $"{StateMeans[i]:F6} ({StateMeans[i] * 100.0:N3}%)",
                color));
        }

        for (int i = 0; i < StateStdDevs.Count; i++)
        {
            double stdPct = StateStdDevs[i] * 100.0;
            list.Add(new DrawingCalculatedValue(
                $"HMM State #{i + 1} Volatility",
                $"HMM State #{i + 1} Volatility",
                IndicatorCalculationHelper.ToDecimalOrNull(stdPct),
                string.Create(DrawingValueFormat.Culture, $"{stdPct:F2}%"),
                color));
        }

        for (int i = 0; i < StateMeans.Count && i < TransitionMatrix.GetLength(0) && i < TransitionMatrix.GetLength(1); i++)
        {
            double persistPct = TransitionMatrix[i, i] * 100.0;
            list.Add(new DrawingCalculatedValue(
                $"HMM State #{i + 1} Persistence",
                $"HMM State #{i + 1} Persistence",
                IndicatorCalculationHelper.ToDecimalOrNull(Math.Round(persistPct, 1)),
                string.Create(DrawingValueFormat.Culture, $"{persistPct:F1}%"),
                color));
        }

        return list.ToArray();
    }
}
