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

public class SsaProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.SsaProjection;

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
    /// Embedding dimension (lag window length L) for trajectory matrix construction.
    /// </summary>
    public int EmbeddingDimension { get; set; } = 10;

    /// <summary>
    /// Number of principal reconstructed components (r) used for signal subspace and linear recurrence extrapolation.
    /// </summary>
    public int NumComponents { get; set; } = 2;

    /// <summary>
    /// Specifies the detrending algorithm: OLS Linear, Endpoint Linear, or None.
    /// </summary>
    public SsaDetrendMode DetrendMethod { get; set; } = SsaDetrendMode.LeastSquaresLinear;

    /// <summary>
    /// Future trajectory extrapolation mode (Recurrent LRR or Vector SSA subspace projection).
    /// </summary>
    public SsaForecastMode ForecastMode { get; set; } = SsaForecastMode.Recurrent;

    /// <summary>
    /// Overall separability score (0.0% to 100.0%) of the extracted components.
    /// </summary>
    public double SeparabilityScore { get; set; } = 100.0;

    /// <summary>
    /// Backward-compatible boolean property for Detrending.
    /// </summary>
    public bool ApplyDetrend
    {
        get => DetrendMethod != SsaDetrendMode.None;
        set => DetrendMethod = value ? SsaDetrendMode.LeastSquaresLinear : SsaDetrendMode.None;
    }

    /// <summary>
    /// Whether to draw the in-sample SSA reconstructed curve over the selection interval.
    /// </summary>
    public bool ShowReconstructedPath { get; set; } = true;

    /// <summary>
    /// Price source selector used for SSA decomposition (SSoT: PriceType).
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Median;

    /// <summary>
    /// Whether to render the uncertainty diffusion band (±M*sigma).
    /// </summary>
    public bool ShowConfidenceBand { get; set; } = true;

    /// <summary>
    /// Uncertainty interval multiplier (M in ±M*sigma, e.g. 1.0 = ~68%, 2.0 = ~95%).
    /// </summary>
    public decimal ConfidenceMultiplier { get; set; } = 2.0m;

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Projected path data: Point.X = timestamp (ticks), Point.Y = projected price
    public List<StockAnalyzer.Core.Models.Point> ProjectedPath { get; set; } = new();

    // Upper and Lower uncertainty interval band path data
    public List<StockAnalyzer.Core.Models.Point> UpperBandPath { get; set; } = new();
    public List<StockAnalyzer.Core.Models.Point> LowerBandPath { get; set; } = new();

    // In-sample reconstructed curve path data
    public List<StockAnalyzer.Core.Models.Point> ReconstructedPath { get; set; } = new();

    // SSA components extracted during last calculation
    public IReadOnlyList<SsaComponentInfo> Components { get; set; } = Array.Empty<SsaComponentInfo>();
    public double ResidualStdDev { get; set; }
    public double CumulativeVarianceRatio { get; set; }
    public double NuSquared { get; set; }
    public bool IsStable { get; set; } = true;
    public int SampleCount { get; set; }

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.SsaProjectionRenderer _renderer = new();

    public SsaProjectionObject()
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

        if (ReconstructedPath != null)
        {
            var newRecon = new List<StockAnalyzer.Core.Models.Point>();
            foreach (var p in ReconstructedPath)
            {
                newRecon.Add(new StockAnalyzer.Core.Models.Point(p.X + timeDelta.Ticks, p.Y + (double)priceDelta));
            }
            ReconstructedPath = newRecon;
        }
    }

    /// <summary>
    /// Recalculates the Singular Spectrum Analysis (SSA) trajectory decomposition and extrapolates the future path.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            ReconstructedPath?.Clear();
            Components = Array.Empty<SsaComponentInfo>();
            ResidualStdDev = 0.0;
            CumulativeVarianceRatio = 0.0;
            NuSquared = 0.0;
            IsStable = true;
            SampleCount = 0;
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
            ReconstructedPath?.Clear();
            Components = Array.Empty<SsaComponentInfo>();
            ResidualStdDev = 0.0;
            CumulativeVarianceRatio = 0.0;
            NuSquared = 0.0;
            IsStable = true;
            SampleCount = 0;
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < SsaProjectionAnalysis.MinSampleCount)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            ReconstructedPath?.Clear();
            Components = Array.Empty<SsaComponentInfo>();
            ResidualStdDev = 0.0;
            CumulativeVarianceRatio = 0.0;
            NuSquared = 0.0;
            IsStable = true;
            SampleCount = count;
            return;
        }

        Func<CoreCandleData, double> selector = c => (double)PriceDataHelper.ExtractPrice(c, PriceSource);

        var samples = new List<double>(count);
        var timestamps = new List<DateTime>(count);

        for (int i = startIndex; i <= endIndex; i++)
        {
            samples.Add(selector(candles[i]));
            timestamps.Add(candles[i].Timestamp);
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: FutureSteps,
            timeframeSpan: timeframeSpan,
            embeddingDimension: EmbeddingDimension,
            numComponents: NumComponents,
            detrendMode: DetrendMethod,
            showConfidenceBand: ShowConfidenceBand,
            confidenceMultiplier: ConfidenceMultiplier,
            forecastMode: ForecastMode);

        ProjectedPath = result.ProjectedPoints.ToList();
        UpperBandPath = result.UpperBandPoints.ToList();
        LowerBandPath = result.LowerBandPoints.ToList();
        ReconstructedPath = result.ReconstructedPoints.ToList();
        Components = result.Components;
        ResidualStdDev = result.ResidualStdDev;
        CumulativeVarianceRatio = result.CumulativeVarianceRatio;
        NuSquared = result.NuSquared;
        IsStable = result.IsStable;
        SampleCount = result.SampleCount;

        // Compute SSA Separability Score across extracted components
        int l = Math.Clamp(EmbeddingDimension, 2, Math.Max(2, count / 2));
        int k = count - l + 1;
        int r = Math.Clamp(NumComponents, 1, Math.Min(l - 1, k));

        if (r > 1 && count >= 4)
        {
            var sampleArray = samples.ToArray();
            var decomp = SsaDecompositionEngine.Decompose(sampleArray, l, DetrendMethod);
            if (decomp.SortedIndices.Length >= r)
            {
                Span<double> processed = stackalloc double[count];
                SsaDecompositionEngine.Detrend(sampleArray, processed, DetrendMethod, out _, out _);
                double[] allRecon = new double[r * count];
                for (int m = 0; m < r; m++)
                {
                    int eigIdx = decomp.SortedIndices[m];
                    Span<int> singleIdx = stackalloc int[1] { eigIdx };
                    SsaDecompositionEngine.ReconstructGroup(processed, l, k, singleIdx, decomp.Eigenvectors, allRecon.AsSpan(m * count, count));
                }
                Span<double> wCorr = stackalloc double[r * r];
                SsaDiagnostics.ComputeWCorrelationMatrix(allRecon, r, count, l, wCorr);
                SeparabilityScore = SsaDiagnostics.ComputeSeparabilityScore(wCorr, r);
            }
            else
            {
                SeparabilityScore = 100.0;
            }
        }
        else
        {
            SeparabilityScore = 100.0;
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Components == null || Components.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>();

        for (int i = 0; i < Components.Count; i++)
        {
            var comp = Components[i];
            list.Add(new DrawingCalculatedValue(
                $"SSA Comp #{comp.ComponentIndex} Var",
                $"SSA Comp #{comp.ComponentIndex} Var",
                (decimal)(comp.VarianceRatio * 100.0),
                $"{comp.VarianceRatio * 100.0:F1}%",
                color));
        }

        list.Add(new DrawingCalculatedValue(
            "SSA Cumulative Var",
            "SSA Cumulative Var",
            (decimal)(CumulativeVarianceRatio * 100.0),
            $"{CumulativeVarianceRatio * 100.0:F1}%",
            color));

        if (Components.Count > 1)
        {
            string grade = SsaDiagnostics.GetSeparabilityGrade(SeparabilityScore);
            list.Add(new DrawingCalculatedValue(
                "SSA Separability",
                "SSA Separability",
                (decimal)SeparabilityScore,
                $"{SeparabilityScore:F1}% ({grade})",
                SeparabilityScore >= 75.0 ? IndicatorColor.Bullish : IndicatorColor.Bearish));
        }

        list.Add(new DrawingCalculatedValue(
            "SSA Stability (ν²)",
            "SSA Stability (ν²)",
            (decimal)NuSquared,
            $"{NuSquared:F3} ({(IsStable ? "Stable" : "Caution")})",
            IsStable ? IndicatorColor.Bullish : IndicatorColor.Bearish));

        if (SampleCount > 0 && FutureSteps > 0)
        {
            double ratio = (double)FutureSteps / SampleCount;
            list.Add(new DrawingCalculatedValue(
                "SSA Horizon (H/N)",
                "SSA Horizon (H/N)",
                (decimal)ratio,
                $"{ratio:F2} ({FutureSteps}/{SampleCount} bars)",
                color));
        }

        if (ResidualStdDev > 0)
        {
            list.Add(new DrawingCalculatedValue(
                "SSA Residual StdDev",
                "SSA Residual StdDev",
                (decimal)ResidualStdDev,
                $"{ResidualStdDev:F2}",
                color));
        }

        if (ProjectedPath != null && ProjectedPath.Count > 1)
        {
            var targetPrice = (decimal)ProjectedPath[^1].Y;
            list.Add(new DrawingCalculatedValue(
                "SSA Target Price",
                "SSA Target Price",
                targetPrice,
                $"{targetPrice:F2}",
                color));
        }

        return list;
    }
}
