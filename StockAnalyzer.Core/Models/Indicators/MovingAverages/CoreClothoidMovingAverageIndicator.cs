using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.ClothoidMovingAverage)]
public class CoreClothoidMovingAverageIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = IndicatorDefaultConstants.ClothoidMovingAverageDefaultPeriod;
    public double Offset { get; set; } = IndicatorDefaultConstants.ClothoidMovingAverageDefaultOffset;
    public double Sigma { get; set; } = IndicatorDefaultConstants.ClothoidMovingAverageDefaultSigma;

    public override string Name => $"ClothoidMA ({Period}, {Offset:F2}, {Sigma:F1})";
    public override bool IsOverlay => true;

    /// <summary>
    /// Theoretical effective lag (average bar delay) of the Fresnel filter weights: Lag = sum(q_j * (N - 1 - j)).
    /// Diagnostic series for filter lag evaluation.
    /// Excluded from chart rendering and auto-range via IndicatorChartSuppression.
    /// </summary>
    public List<decimal?> EffectiveLag { get; } = new();

    /// <summary>
    /// Kish's effective sample size (degrees of freedom) of the kernel: N_eff = 1 / sum(q_j^2).
    /// Diagnostic series for smoothing strength evaluation.
    /// Excluded from chart rendering and auto-range via IndicatorChartSuppression.
    /// </summary>
    public List<decimal?> EffectiveSampleSize { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreClothoidMovingAverageParameter p)
        {
            Period = p.Period;
            Offset = p.Offset;
            Sigma = p.Sigma;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        EffectiveLag.Clear();
        EffectiveSampleSize.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = series.Count;
        int period = Math.Max(2, Period);

        if (_values.Capacity < count) _values.Capacity = count;
        if (EffectiveLag.Capacity < count) EffectiveLag.Capacity = count;
        if (EffectiveSampleSize.Capacity < count) EffectiveSampleSize.Capacity = count;

        double[]? rentedRaw = null;
        decimal[]? rentedNorm = null;

        Span<double> rawWeights = period <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[period]
            : (rentedRaw = ArrayPool<double>.Shared.Rent(period)).AsSpan(0, period);

        Span<decimal> normWeights = period <= MathBufferLimits.StackAllocThreshold
            ? stackalloc decimal[period]
            : (rentedNorm = ArrayPool<decimal>.Shared.Rent(period)).AsSpan(0, period);

        try
        {
            // Pre-calculate Fresnel cosine weights
            ClothoidMath.GenerateFresnelWeights(rawWeights, period, Offset, Sigma);

            double sumRaw = 0.0;
            for (int j = 0; j < period; j++)
            {
                sumRaw += rawWeights[j];
            }

            if (sumRaw > 1e-12)
            {
                decimal sumDec = 0.0m;
                int lastPositiveIndex = -1;
                for (int j = 0; j < period; j++)
                {
                    decimal wDec = (decimal)(rawWeights[j] / sumRaw);
                    normWeights[j] = wDec;
                    sumDec += wDec;
                    if (wDec > 0.0m)
                    {
                        lastPositiveIndex = j;
                    }
                }

                // Minor residual correction to guarantee sum equals exactly 1.0m
                decimal diff = 1.0m - sumDec;
                if (lastPositiveIndex >= 0)
                {
                    normWeights[lastPositiveIndex] += diff;
                }
            }
            else
            {
                // Degenerate kernel: all weights are near-zero or zero.
                // Do NOT silently fall back to SMA. Return null values across all bars.
                for (int i = 0; i < count; i++)
                {
                    _values.Add(null);
                    EffectiveLag.Add(null);
                    EffectiveSampleSize.Add(null);
                }
                return CreateAutomaticResult();
            }

            // Calculate filter diagnostics (effective lag and Kish effective sample size)
            decimal lag = 0.0m;
            decimal sumSq = 0.0m;
            for (int j = 0; j < period; j++)
            {
                lag += normWeights[j] * (decimal)(period - 1 - j);
                sumSq += normWeights[j] * normWeights[j];
            }
            decimal sampleSize = sumSq > 0.0m ? 1.0m / sumSq : 0.0m;

            // Perform pure decimal weighted convolution
            for (int i = 0; i < count; i++)
            {
                if (i < period - 1)
                {
                    _values.Add(null);
                    EffectiveLag.Add(null);
                    EffectiveSampleSize.Add(null);
                    continue;
                }

                bool hasNull = false;
                decimal weightedSum = 0.0m;

                for (int j = 0; j < period; j++)
                {
                    var price = series[i - period + 1 + j];
                    if (!price.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    weightedSum += normWeights[j] * price.Value;
                }

                if (hasNull)
                {
                    _values.Add(null);
                    EffectiveLag.Add(null);
                    EffectiveSampleSize.Add(null);
                }
                else
                {
                    _values.Add(weightedSum);
                    EffectiveLag.Add(lag);
                    EffectiveSampleSize.Add(sampleSize);
                }
            }

            return CreateAutomaticResult();
        }
        finally
        {
            if (rentedRaw != null) ArrayPool<double>.Shared.Return(rentedRaw);
            if (rentedNorm != null) ArrayPool<decimal>.Shared.Return(rentedNorm);
        }
    }
}
