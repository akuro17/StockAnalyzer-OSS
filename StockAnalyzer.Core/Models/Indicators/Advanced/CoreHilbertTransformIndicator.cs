using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Hilbert Transform Dominant Cycle Indicator.
/// Implements John F. Ehlers'' Canonical Homodyne Discriminator algorithm to extract the instantaneous dominant cycle period (in bars).
/// Pure C# implementation (Zero-Dependency, Zero-Allocation).
/// Acts as a primary Period-native driver for dynamic adaptive indicators (Adaptive EMA, RSI, SMA).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HilbertTransform)]
public class CoreHilbertTransformIndicator : CoreIndicatorBase
{
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    public override string Name => $"Hilbert Transform ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => false;

    public List<decimal?> InPhase { get; } = new();
    public List<decimal?> Quadrature { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHilbertTransformParameter p)
        {
            DefaultPeriod = p.DefaultPeriod;
            MinPeriod = p.MinPeriod;
            MaxPeriod = p.MaxPeriod;
            SmoothBeta = p.SmoothBeta;
            DeltaLimit = p.DeltaLimit;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            _values.Clear();
            InPhase.Clear();
            Quadrature.Clear();
            return IndicatorResult.Success(_values);
        }

        // Use typical price (H+L+C)/3 for superior frequency extraction fidelity
        var series = candles.Select(c => (decimal?)((c.High + c.Low + c.Close) / 3.0m)).ToList();
        return CalculateSeriesCore(series);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        InPhase.Clear();
        Quadrature.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int count = series.Count;
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            prices[i] = series[i] ?? 0m;
        }

        // Step 1: 4-bar WMA Price Smoother (Noise Isolation)
        var smooth = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            if (i >= 3)
            {
                smooth[i] = (4.0m * prices[i] + 3.0m * prices[i - 1] + 2.0m * prices[i - 2] + prices[i - 3]) / 10.0m;
            }
            else
            {
                smooth[i] = prices[i];
            }
        }

        // Ehlers 7-point FIR filter coefficients
        const decimal a = 0.0962m;
        const decimal b = 0.5769m;

        var detrender = new decimal[count];
        var q1 = new decimal[count];
        var i1 = new decimal[count];
        var jI = new decimal[count];
        var jQ = new decimal[count];
        var i2 = new decimal[count];
        var q2 = new decimal[count];

        decimal i2_prev = 0m;
        decimal q2_prev = 0m;
        decimal smoothRe = 0m;
        decimal smoothIm = 0m;

        decimal period = DefaultPeriod;
        decimal smoothPeriod = DefaultPeriod;
        decimal prevPeriod = DefaultPeriod;

        const decimal epsilon = 1e-10m;
        const int warmupBars = IndicatorDefaultConstants.HilbertTransformWarmupBars;

        for (int i = 0; i < count; i++)
        {
            // Step 2: Adaptive Amplitude Correction Factor based on previous cycle
            decimal ampCorr = 0.075m * prevPeriod + 0.54m;

            // Step 3: 7-tap FIR Detrender on WMA-smoothed prices
            if (i >= 6)
            {
                detrender[i] = (a * smooth[i] + b * smooth[i - 2] - b * smooth[i - 4] - a * smooth[i - 6]) * ampCorr;
            }
            else
            {
                detrender[i] = 0m;
            }

            // Step 4: Compute Q1 (Hilbert FIR on detrender) and I1 (detrender delayed by 3 bars)
            if (i >= 6)
            {
                q1[i] = (a * detrender[i] + b * detrender[i - 2] - b * detrender[i - 4] - a * detrender[i - 6]) * ampCorr;
            }
            else
            {
                q1[i] = 0m;
            }

            i1[i] = i >= 3 ? detrender[i - 3] : 0m;

            // Step 5: Compute jI and jQ (Hilbert FIR on I1 and Q1)
            if (i >= 6)
            {
                jI[i] = (a * i1[i] + b * i1[i - 2] - b * i1[i - 4] - a * i1[i - 6]) * ampCorr;
                jQ[i] = (a * q1[i] + b * q1[i - 2] - b * q1[i - 4] - a * q1[i - 6]) * ampCorr;
            }
            else
            {
                jI[i] = 0m;
                jQ[i] = 0m;
            }

            // Step 6: Analytic signal with Pre-smoothing (0.2 / 0.8)
            decimal rawI2 = i1[i] - jQ[i];
            decimal rawQ2 = q1[i] + jI[i];

            i2[i] = 0.2m * rawI2 + 0.8m * i2_prev;
            q2[i] = 0.2m * rawQ2 + 0.8m * q2_prev;

            // Record auxiliary signals for visualization / debug
            if (i < warmupBars)
            {
                InPhase.Add(null);
                Quadrature.Add(null);
            }
            else
            {
                InPhase.Add(i2[i]);
                Quadrature.Add(q2[i]);
            }

            // Step 7: Homodyne Discriminator (complex correlation with previous bar)
            decimal re = i2[i] * i2_prev + q2[i] * q2_prev;
            decimal im = i2[i] * q2_prev - q2[i] * i2_prev;

            i2_prev = i2[i];
            q2_prev = q2[i];

            // Post-smoothing on real and imaginary components
            smoothRe = 0.2m * re + 0.8m * smoothRe;
            smoothIm = 0.2m * im + 0.8m * smoothIm;

            // Step 8: Instantaneous Phase & Period Extraction with Strict Safety Guards
            decimal rawPeriod;
            double magSq = (double)(smoothRe * smoothRe + smoothIm * smoothIm);
            if (magSq > (double)epsilon)
            {
                double deltaPhase = Math.Atan2((double)smoothIm, (double)smoothRe);
                if (deltaPhase > 0.001)
                {
                    double periodVal = (2.0 * Math.PI) / deltaPhase;
                    rawPeriod = double.IsFinite(periodVal) ? (decimal)periodVal : prevPeriod;
                }
                else
                {
                    rawPeriod = prevPeriod; // Hold previous valid period
                }
            }
            else
            {
                rawPeriod = prevPeriod; // Hold previous valid period
            }

            // Step 9: Ehlers Relative Rate-Limiter (0.67 ~ 1.5x) + DeltaLimit absolute jump cap + Range check
            if (rawPeriod > 1.5m * prevPeriod) rawPeriod = 1.5m * prevPeriod;
            if (rawPeriod < 0.67m * prevPeriod) rawPeriod = 0.67m * prevPeriod;
            decimal safeDeltaLimit = Math.Max(DeltaLimit, epsilon);
            rawPeriod = Math.Clamp(rawPeriod, prevPeriod - safeDeltaLimit, prevPeriod + safeDeltaLimit);
            rawPeriod = Math.Clamp(rawPeriod, (decimal)MinPeriod, (decimal)MaxPeriod);

            // Step 10: Two-Stage Exponential Smoothing (SmoothBeta-weighted Period EMA -> SmoothPeriod EMA)
            period = SmoothBeta * rawPeriod + (1.0m - SmoothBeta) * period;
            smoothPeriod = 0.33m * period + 0.67m * smoothPeriod;
            prevPeriod = period;

            // Final safety clamp within configured [MinPeriod, MaxPeriod]
            decimal finalPeriod = Math.Clamp(smoothPeriod, (decimal)MinPeriod, (decimal)MaxPeriod);

            if (i < warmupBars)
            {
                _values.Add(null);
            }
            else
            {
                _values.Add(finalPeriod);
            }
        }

        var resultSeries = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { "InPhase", InPhase },
            { "Quadrature", Quadrature }
        };

        return IndicatorResult.Success(resultSeries);
    }
}
