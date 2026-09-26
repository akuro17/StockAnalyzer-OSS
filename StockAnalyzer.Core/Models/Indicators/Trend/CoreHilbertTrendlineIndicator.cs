using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// Hilbert Transform Instantaneous Trendline indicator (HT_TRENDLINE).
/// Traces the instantaneous underlying price trend by dynamically removing the dominant cycle.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HilbertTrendline)]
public class CoreHilbertTrendlineIndicator : CoreIndicatorBase
{
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    public override string Name => $"Hilbert Instantaneous Trendline ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => true;
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHilbertTrendlineParameter p)
        {
            DefaultPeriod = p.DefaultPeriod;
            MinPeriod = p.MinPeriod;
            MaxPeriod = p.MaxPeriod;
            SmoothBeta = p.SmoothBeta;
            DeltaLimit = p.DeltaLimit;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int count = series.Count;
        decimal[] prices = ArrayPool<decimal>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                prices[i] = series[i] ?? 0m;
            }

            var decompositionParams = new HilbertDecompositionParameters(
                DefaultPeriod: DefaultPeriod,
                MinPeriod: MinPeriod,
                MaxPeriod: MaxPeriod,
                SmoothBeta: SmoothBeta,
                DeltaLimit: DeltaLimit,
                WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars);

            var decomp = HilbertDecompositionEngine.Decompose(prices.AsSpan(0, count), decompositionParams);

            for (int i = 0; i < count; i++)
            {
                var sample = decomp[i];
                if (sample.IsWarmup || !sample.IsValid)
                {
                    _values.Add(null);
                }
                else
                {
                    int cycleLength = Math.Clamp((int)Math.Round(sample.DominantCycle, MidpointRounding.AwayFromZero), 2, i + 1);
                    decimal weightedSum = 0m;
                    int weightSum = 0;
                    for (int k = 0; k < cycleLength; k++)
                    {
                        int weight = cycleLength - k;
                        weightedSum += weight * prices[i - k];
                        weightSum += weight;
                    }
                    _values.Add(weightedSum / weightSum);
                }
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
        }

        return IndicatorResult.Success(_values);
    }
}
