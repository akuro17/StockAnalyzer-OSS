using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.SchaffTrendCycle)]
public class CoreSchaffTrendCycleIndicator : CoreIndicatorBase
{
    public int CyclePeriod { get; set; } = 10;
    public int ShortPeriod { get; set; } = 23;
    public int LongPeriod { get; set; } = 50;
    public int SmoothPeriod { get; set; } = 3;

    public override string Name => $"STC ({CyclePeriod},{ShortPeriod},{LongPeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSchaffTrendCycleParameter p)
        {
            CyclePeriod = p.CyclePeriod;
            ShortPeriod = p.ShortPeriod;
            LongPeriod = p.LongPeriod;
            SmoothPeriod = p.SmoothPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

        int count = candles.Count;

        // 1. MACD line: EMA(Close, ShortPeriod) - EMA(Close, LongPeriod).
        var closes = new List<decimal>(count);
        for (int i = 0; i < count; i++)
        {
            closes.Add(candles[i].Close);
        }

        var shortEma = IndicatorCalculationHelper.CalculateEma(closes, ShortPeriod);
        var longEma = IndicatorCalculationHelper.CalculateEma(closes, LongPeriod);

        var macd = new List<decimal?>(count);
        for (int i = 0; i < count; i++)
        {
            macd.Add(shortEma[i].HasValue && longEma[i].HasValue
                ? shortEma[i]!.Value - longEma[i]!.Value
                : (decimal?)null);
        }

        decimal smoothFactor = 2m / (SmoothPeriod + 1m);

        // 2. First stochastic of MACD over CyclePeriod (scaled 0..100), then EMA-smoothed.
        var stoch1 = StochasticOverCycle(macd);
        var pf1 = EmaSmooth(stoch1, smoothFactor);

        // 3. Second stochastic of the smoothed series, then EMA-smoothed => STC.
        var stoch2 = StochasticOverCycle(pf1);
        var stc = EmaSmooth(stoch2, smoothFactor);

        // 4. Clamp to the oscillator's 0..100 range.
        for (int i = 0; i < count; i++)
        {
            _values.Add(stc[i].HasValue
                ? Math.Max(0m, Math.Min(100m, stc[i]!.Value))
                : (decimal?)null);
        }

        return IndicatorResult.Success(_values);
    }

    /// <summary>
    /// Rolling stochastic %K of <paramref name="source"/> over <see cref="CyclePeriod"/> bars,
    /// scaled to 0..100. Nulls in the window are skipped; a flat window (highest == lowest) yields 0.
    /// </summary>
    private List<decimal?> StochasticOverCycle(IReadOnlyList<decimal?> source)
    {
        var result = new List<decimal?>(source.Count);

        for (int i = 0; i < source.Count; i++)
        {
            if (i < CyclePeriod - 1 || !source[i].HasValue)
            {
                result.Add(null);
                continue;
            }

            decimal highest = decimal.MinValue;
            decimal lowest = decimal.MaxValue;
            for (int j = i - CyclePeriod + 1; j <= i; j++)
            {
                if (!source[j].HasValue)
                {
                    continue;
                }

                if (source[j]!.Value > highest) highest = source[j]!.Value;
                if (source[j]!.Value < lowest) lowest = source[j]!.Value;
            }

            result.Add(highest == lowest
                ? 0m
                : (source[i]!.Value - lowest) / (highest - lowest) * 100m);
        }

        return result;
    }

    /// <summary>
    /// EMA smoothing that skips leading/interior nulls and seeds from the first valid value,
    /// matching the canonical Schaff Trend Cycle reference.
    /// </summary>
    private static List<decimal?> EmaSmooth(IReadOnlyList<decimal?> source, decimal k)
    {
        var result = new List<decimal?>(source.Count);
        decimal? prev = null;

        for (int i = 0; i < source.Count; i++)
        {
            if (!source[i].HasValue)
            {
                result.Add(null);
                continue;
            }

            prev = prev.HasValue
                ? (source[i]!.Value - prev.Value) * k + prev.Value
                : source[i]!.Value;
            result.Add(prev);
        }

        return result;
    }
}
