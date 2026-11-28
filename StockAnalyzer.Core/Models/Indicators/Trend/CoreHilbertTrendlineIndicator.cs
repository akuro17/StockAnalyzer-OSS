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
    public HilbertDetrendMode DetrendMode { get; set; } = HilbertDetrendMode.FirDetrender;

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
            DetrendMode = p.DetrendMode;
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
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
        }

        var decompositionParams = new HilbertDecompositionParameters(
            DefaultPeriod: DefaultPeriod,
            MinPeriod: MinPeriod,
            MaxPeriod: MaxPeriod,
            SmoothBeta: SmoothBeta,
            DeltaLimit: DeltaLimit,
            WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars,
            DetrendMode: DetrendMode);

        // Compute only across contiguous non-null segments; never substitute 0m for missing bars.
        int segStart = 0;
        while (segStart < count)
        {
            if (!series[segStart].HasValue)
            {
                segStart++;
                continue;
            }

            int segEnd = segStart + 1;
            while (segEnd < count && series[segEnd].HasValue)
            {
                segEnd++;
            }

            int segLen = segEnd - segStart;
            decimal[] segPrices = ArrayPool<decimal>.Shared.Rent(segLen);
            try
            {
                for (int k = 0; k < segLen; k++)
                {
                    segPrices[k] = series[segStart + k]!.Value;
                }

                var decomp = HilbertDecompositionEngine.Decompose(segPrices.AsSpan(0, segLen), decompositionParams);

                for (int k = 0; k < segLen; k++)
                {
                    var sample = decomp[k];
                    if (!sample.IsWarmup && sample.IsValid)
                    {
                        int cycleLength = Math.Clamp((int)Math.Round(sample.DominantCycle, MidpointRounding.AwayFromZero), 2, k + 1);
                        decimal weightedSum = 0m;
                        int weightSum = 0;
                        for (int m = 0; m < cycleLength; m++)
                        {
                            int weight = cycleLength - m;
                            weightedSum += weight * segPrices[k - m];
                            weightSum += weight;
                        }
                        _values[segStart + k] = weightedSum / weightSum;
                    }
                }
            }
            finally
            {
                ArrayPool<decimal>.Shared.Return(segPrices);
            }

            segStart = segEnd;
        }

        return IndicatorResult.Success(_values);
    }
}
