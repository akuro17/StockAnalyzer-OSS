using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Technical indicator that calculates the rolling Discrete Fréchet Distance between
/// a recent window of length <see cref="Period"/> and a comparison window shifted by <see cref="Lag"/> bars.
/// Evaluates non-linear bottleneck deviation and geometric similarity over time.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.FrechetOscillator)]
public class CoreFrechetOscillatorIndicator : CoreIndicatorBase
{
    private const int StackAllocThreshold = 512;

    public int Period { get; set; } = 20;
    public int Lag { get; set; } = 10;
    public override string Name => $"Fréchet Oscillator ({Period},{Lag})";
    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreFrechetOscillatorParameter p)
        {
            Period = p.Period;
            Lag = p.Lag;
            PriceSource = p.PriceSource;
        }
        else if (parameters is CoreStructuralDtwParameter sp)
        {
            Period = sp.Period;
            Lag = sp.Lag;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Failure("No candle data provided.");
        }

        int count = candles.Count;
        _values.Clear();
        _values.Capacity = count;

        if (count < Period + Lag)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        double[]? w1HeapBuffer = null;
        Span<double> w1Norm = Period <= StackAllocThreshold
            ? stackalloc double[Period]
            : (w1HeapBuffer = ArrayPool<double>.Shared.Rent(Period)).AsSpan(0, Period);

        double[]? w2HeapBuffer = null;
        Span<double> w2Norm = Period <= StackAllocThreshold
            ? stackalloc double[Period]
            : (w2HeapBuffer = ArrayPool<double>.Shared.Rent(Period)).AsSpan(0, Period);

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (i < Period + Lag - 1)
                {
                    _values.Add(null);
                    continue;
                }

                // Window 1: Recent window [i - Period + 1 .. i]
                double sum1 = 0.0;
                for (int j = 0; j < Period; j++)
                {
                    double p = (double)(priceSeries[i - Period + 1 + j] ?? 0m);
                    w1Norm[j] = p;
                    sum1 += p;
                }
                double mean1 = sum1 / Period;

                double sqDiffSum1 = 0.0;
                for (int j = 0; j < Period; j++)
                {
                    double d = w1Norm[j] - mean1;
                    sqDiffSum1 += d * d;
                }
                double stdDev1 = Math.Sqrt(Math.Max(0.0, sqDiffSum1 / Period));
                bool w1Flat = stdDev1 <= 1e-12;
                if (!w1Flat)
                {
                    for (int j = 0; j < Period; j++)
                    {
                        w1Norm[j] = (w1Norm[j] - mean1) / stdDev1;
                    }
                }

                // Window 2: Lagged window [i - Period - Lag + 1 .. i - Lag]
                double sum2 = 0.0;
                for (int j = 0; j < Period; j++)
                {
                    double p = (double)(priceSeries[i - Period - Lag + 1 + j] ?? 0m);
                    w2Norm[j] = p;
                    sum2 += p;
                }
                double mean2 = sum2 / Period;

                double sqDiffSum2 = 0.0;
                for (int j = 0; j < Period; j++)
                {
                    double d = w2Norm[j] - mean2;
                    sqDiffSum2 += d * d;
                }
                double stdDev2 = Math.Sqrt(Math.Max(0.0, sqDiffSum2 / Period));
                bool w2Flat = stdDev2 <= 1e-12;
                if (!w2Flat)
                {
                    for (int j = 0; j < Period; j++)
                    {
                        w2Norm[j] = (w2Norm[j] - mean2) / stdDev2;
                    }
                }

                if (w1Flat && w2Flat)
                {
                    _values.Add(0.0m);
                }
                else if (w1Flat || w2Flat)
                {
                    _values.Add(null);
                }
                else
                {
                    double dist = FrechetMath.CalculateDiscreteFrechetDistance(w1Norm, w2Norm);
                    if (double.IsNaN(dist))
                    {
                        _values.Add(null);
                    }
                    else
                    {
                        _values.Add((decimal)dist);
                    }
                }
            }

            return IndicatorResult.Success(_values);
        }
        finally
        {
            if (w1HeapBuffer != null) ArrayPool<double>.Shared.Return(w1HeapBuffer);
            if (w2HeapBuffer != null) ArrayPool<double>.Shared.Return(w2HeapBuffer);
        }
    }
}
