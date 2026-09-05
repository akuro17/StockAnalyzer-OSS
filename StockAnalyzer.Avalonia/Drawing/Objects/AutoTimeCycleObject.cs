using System;
using System.Buffers;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Specifies the future projection alignment anchor mode for AutoTimeCycle lines.
/// </summary>
public enum AutoCycleAlignment
{
    /// <summary>Align cycle lines starting forward from the selection end bar index.</summary>
    Endpoint = 0,

    /// <summary>Align cycle lines to future crests (peaks) of the dominant harmonic waveform.</summary>
    Peak = 1,

    /// <summary>Align cycle lines to future troughs (valleys) of the dominant harmonic waveform.</summary>
    Trough = 2
}

/// <summary>
/// Drawing tool object for Auto Time Cycle Lines.
/// Analyzes price series within a selected range via FFT spectrum decomposition,
/// identifies the dominant harmonic cycle period, and projects equidistant vertical cycle lines forward into future space.
/// </summary>
public class AutoTimeCycleObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.X;
    public bool IsMoveAxisModeExplicit { get; set; } = true;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.AutoTimeCycle;

    // Points[0] = Start Point (Time/Price) of selection
    // Points[1] = End Point (Time/Price) of selection
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties (shared with SSA Projection)
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = 1.5;
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
    /// Price source selector used for Fourier decomposition (SSoT: PriceType).
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Median;

    /// <summary>
    /// Whether to subtract linear trend (OLS) prior to FFT decomposition.
    /// </summary>
    public bool ApplyDetrend { get; set; } = true;

    /// <summary>
    /// Minimum period (in bars) to search for dominant harmonics.
    /// </summary>
    public double MinPeriod { get; set; } = 5.0;

    /// <summary>
    /// Maximum period (in bars) to search for dominant harmonics.
    /// </summary>
    public double MaxPeriod { get; set; } = 200.0;

    /// <summary>
    /// Number of future periodic vertical lines to project forward (1 to 50).
    /// </summary>
    public int CycleCount { get; set; } = 10;

    /// <summary>
    /// Future projection alignment mode (Endpoint, Peak, or Trough).
    /// </summary>
    public AutoCycleAlignment Alignment { get; set; } = AutoCycleAlignment.Endpoint;

    /// <summary>
    /// Whether to apply parabolic log-interpolation for continuous sub-bin period refinement.
    /// </summary>
    public bool EnableFrequencyInterpolation { get; set; } = true;

    /// <summary>
    /// Whether to render the dominant period badge label above the end boundary line.
    /// </summary>
    public bool ShowPeriodLabel { get; set; } = true;

    public SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);

    // Calculation results (Read-only derived state)
    public double DominantPeriod { get; private set; } = 0.0;
    public double DominantPower { get; private set; } = 0.0;
    public double DominantPhase { get; private set; } = 0.0;
    public double PowerShare { get; private set; } = 0.0;
    public List<double> ProjectedBarIndices { get; } = new();
    public bool IsCalculated => DominantPeriod > 0.0 && ProjectedBarIndices.Count > 0;

    private readonly Renderers.AutoTimeCycleRenderer _renderer = new();

    public AutoTimeCycleObject()
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
    }

    /// <summary>
    /// Recalculates the dominant cycle period from in-sample price series via FFT and generates future line indices.
    /// Employs ZeroAllocation array pooling and strict defensive guardrails.
    /// </summary>
    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        DominantPeriod = 0.0;
        DominantPower = 0.0;
        DominantPhase = 0.0;
        PowerShare = 0.0;
        ProjectedBarIndices.Clear();

        if (candles == null || candles.Count < 4 || Points.Count < 2) return;

        // 1. Coordinate Normalization (t_start <= t_end)
        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var startTime = t1 <= t2 ? t1 : t2;
        var endTime = t1 <= t2 ? t2 : t1;

        int startIndex = -1;
        int endIndex = -1;

        for (int i = 0; i < candles.Count; i++)
        {
            if (startIndex == -1 && candles[i].Timestamp >= startTime) startIndex = i;
            if (candles[i].Timestamp <= endTime) endIndex = i;
        }

        if (startIndex < 0 || endIndex < 0 || startIndex > endIndex) return;
        int n = endIndex - startIndex + 1;
        if (n < 4) return; // Minimum sample guard for FFT

        // 2. ZeroAllocation scalar price extraction and data sanitation
        double[] rentedSamples = ArrayPool<double>.Shared.Rent(n);
        try
        {
            double firstVal = 0.0;
            bool allSame = true;

            for (int i = 0; i < n; i++)
            {
                double val = (double)PriceDataHelper.ExtractPrice(candles[startIndex + i], PriceSource);
                if (double.IsNaN(val) || double.IsInfinity(val)) return; // Failsafe abort on invalid values

                rentedSamples[i] = val;
                if (i == 0)
                {
                    firstVal = val;
                }
                else if (Math.Abs(val - firstVal) > 1e-12)
                {
                    allSame = false;
                }
            }

            if (allSame) return; // Failsafe abort on zero-variance constant signal

            // 3. Cycle search bounds consolidation
            double effectiveMin = Math.Max(2.0, MinPeriod);
            double effectiveMax = Math.Min(MaxPeriod, n / 2.0);
            if (effectiveMin > effectiveMax) return;

            // 4. Pure C# FFT computation using existing FftSpectrumAnalysis (SSoT, no modifications)
            var sampleSlice = new ArraySegment<double>(rentedSamples, 0, n);
            var fftResult = FftSpectrumAnalysis.CalculateSpectrum(
                sampleSlice,
                applyDetrend: ApplyDetrend,
                applyHanningWindow: true,
                minPeriod: effectiveMin,
                maxPeriod: effectiveMax);

            if (fftResult.DominantBin == null || fftResult.Bins.Count == 0) return;

            var dominant = fftResult.DominantBin;
            int kStar = dominant.BinIndex;
            double pStar = dominant.Power;
            if (pStar <= 1e-12) return;

            // 5. Parabolic log-interpolation with 4-fold defensive guards
            double delta = 0.0;
            int halfN = n / 2;
            if (EnableFrequencyInterpolation && kStar > 1 && kStar < halfN)
            {
                FftSpectrumBin? prevBin = null;
                FftSpectrumBin? nextBin = null;
                for (int i = 0; i < fftResult.Bins.Count; i++)
                {
                    if (fftResult.Bins[i].BinIndex == kStar - 1) prevBin = fftResult.Bins[i];
                    if (fftResult.Bins[i].BinIndex == kStar + 1) nextBin = fftResult.Bins[i];
                }

                if (prevBin != null && nextBin != null && prevBin.Power > 1e-12 && nextBin.Power > 1e-12)
                {
                    double lnPrev = Math.Log(prevBin.Power);
                    double lnCurr = Math.Log(pStar);
                    double lnNext = Math.Log(nextBin.Power);
                    double denom = lnPrev - 2.0 * lnCurr + lnNext;

                    if (Math.Abs(denom) > 1e-10)
                    {
                        delta = 0.5 * (lnPrev - lnNext) / denom;
                        delta = Math.Clamp(delta, -0.5, 0.5);
                    }
                }
            }

            double continuousK = Math.Clamp(kStar + delta, 1.0, halfN);
            DominantPeriod = (double)n / continuousK;
            DominantPower = pStar;
            DominantPhase = dominant.Phase;

            // Power Share calculation
            double totalPower = 0.0;
            for (int i = 0; i < fftResult.Bins.Count; i++)
            {
                totalPower += fftResult.Bins[i].Power;
            }
            PowerShare = totalPower > 1e-12 ? Math.Clamp(100.0 * pStar / totalPower, 0.0, 100.0) : 0.0;

            // 6. Future cycle line projection indices
            int count = Math.Clamp(CycleCount, 1, 50);
            if (Alignment == AutoCycleAlignment.Endpoint)
            {
                for (int m = 1; m <= count; m++)
                {
                    ProjectedBarIndices.Add(endIndex + m * DominantPeriod);
                }
            }
            else
            {
                // Extremum Alignment (Peak or Trough)
                double oPhase = -DominantPhase * DominantPeriod / (2.0 * Math.PI);
                double localOffset = (Alignment == AutoCycleAlignment.Peak) ? oPhase : (0.5 * DominantPeriod + oPhase);
                double baseOffset = startIndex + localOffset;
                int p0 = (int)Math.Floor((endIndex - baseOffset) / DominantPeriod) + 1;

                for (int m = 0; m < count; m++)
                {
                    double projIdx = baseOffset + (p0 + m) * DominantPeriod;
                    ProjectedBarIndices.Add(projIdx);
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rentedSamples);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (!IsCalculated) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        return new[]
        {
            new DrawingCalculatedValue(
                "Dominant Cycle Period",
                "Dominant Cycle Period",
                (decimal)DominantPeriod,
                $"{DominantPeriod:F1} bars",
                color),
            new DrawingCalculatedValue(
                "Cycle Power Share",
                "Cycle Power Share",
                (decimal)PowerShare,
                $"{PowerShare:F1}%",
                color)
        };
    }
}
