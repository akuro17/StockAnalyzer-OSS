using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Hilbert Transform Trend vs Cycle Mode indicator (HT_TRENDMODE).
/// Outputs 1 (Trend Mode) when phase variance/stability indicates non-cyclical behavior,
/// and 0 (Cycle Mode) when dominant cycles are stably rotating.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HilbertTrendMode)]
public class CoreHilbertTrendModeIndicator : CoreIndicatorBase
{
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;
    public int StabilityWindow { get; set; } = 10;
    public double StabilityThreshold { get; set; } = IndicatorDefaultConstants.HilbertTrendModeDefaultStabilityThreshold;

    public override string Name => $"Hilbert Trend vs Cycle Mode ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => false;
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHilbertTrendModeParameter p)
        {
            DefaultPeriod = p.DefaultPeriod;
            MinPeriod = p.MinPeriod;
            MaxPeriod = p.MaxPeriod;
            SmoothBeta = p.SmoothBeta;
            DeltaLimit = p.DeltaLimit;
            StabilityWindow = p.StabilityWindow;
            StabilityThreshold = p.StabilityThreshold;
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
                WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars,
                StabilityWindow: StabilityWindow,
                StabilityThresholdDegrees: StabilityThreshold);

            var decomp = HilbertDecompositionEngine.Decompose(prices.AsSpan(0, count), decompositionParams);

            for (int i = 0; i < count; i++)
            {
                var sample = decomp[i];
                if (sample.IsWarmup || !sample.IsValid || double.IsNaN(sample.CycleStability))
                {
                    _values.Add(null);
                }
                else
                {
                    _values.Add(sample.TrendMode ? 1m : 0m);
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
