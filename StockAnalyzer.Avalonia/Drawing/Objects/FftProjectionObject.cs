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

public class FftProjectionObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FftProjection;

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
    /// Number of dominant Fourier harmonic frequencies to synthesize into the future projection.
    /// </summary>
    public int HarmonicCount { get; set; } = 3;

    /// <summary>
    /// Whether to subtract and extrapolate the linear trend component.
    /// </summary>
    public bool ApplyDetrend { get; set; } = true;

    /// <summary>
    /// Minimum period (in bars) to filter harmonic components.
    /// </summary>
    public double MinPeriod { get; set; } = 3.0;

    /// <summary>
    /// Maximum period (in bars) to filter harmonic components.
    /// </summary>
    public double MaxPeriod { get; set; } = 200.0;

    /// <summary>
    /// Price source selector used for Fourier decomposition (SSoT: PriceType).
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

    // Harmonic components extracted during last calculation
    public IReadOnlyList<FftHarmonicComponent> DominantHarmonics { get; set; } = Array.Empty<FftHarmonicComponent>();
    public double ResidualStdDev { get; set; }

    private readonly StockAnalyzer.Avalonia.Drawing.Renderers.FftProjectionRenderer _renderer = new();

    public FftProjectionObject()
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
    /// Recalculates the Fast Fourier Transform dominant harmonics and extrapolates the trajectory forward.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            DominantHarmonics = Array.Empty<FftHarmonicComponent>();
            ResidualStdDev = 0.0;
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
            DominantHarmonics = Array.Empty<FftHarmonicComponent>();
            ResidualStdDev = 0.0;
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < FftProjectionAnalysis.MinSampleCount)
        {
            ProjectedPath?.Clear();
            UpperBandPath?.Clear();
            LowerBandPath?.Clear();
            DominantHarmonics = Array.Empty<FftHarmonicComponent>();
            ResidualStdDev = 0.0;
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

        var result = FftProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: FutureSteps,
            timeframeSpan: timeframeSpan,
            harmonicCount: HarmonicCount,
            applyDetrend: ApplyDetrend,
            minPeriod: MinPeriod,
            maxPeriod: MaxPeriod,
            showConfidenceBand: ShowConfidenceBand,
            confidenceMultiplier: ConfidenceMultiplier);

        ProjectedPath = result.ProjectedPoints.ToList();
        UpperBandPath = result.UpperBandPoints.ToList();
        LowerBandPath = result.LowerBandPoints.ToList();
        DominantHarmonics = result.DominantHarmonics;
        ResidualStdDev = result.ResidualStdDev;
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (DominantHarmonics == null || DominantHarmonics.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var list = new List<DrawingCalculatedValue>();

        for (int i = 0; i < DominantHarmonics.Count; i++)
        {
            var h = DominantHarmonics[i];
            list.Add(new DrawingCalculatedValue(
                $"FFT Harmonic #{i + 1} Period",
                $"FFT Harmonic #{i + 1} Period",
                (decimal)h.Period,
                $"{h.Period:F1} bars",
                color));
        }

        if (ResidualStdDev > 0)
        {
            list.Add(new DrawingCalculatedValue(
                "FFT Residual StdDev",
                "FFT Residual StdDev",
                (decimal)ResidualStdDev,
                $"{ResidualStdDev:F2}",
                color));
        }

        return list;
    }
}