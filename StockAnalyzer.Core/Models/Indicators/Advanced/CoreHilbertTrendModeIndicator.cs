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
    public HilbertDetrendMode DetrendMode { get; set; } = HilbertDetrendMode.FirDetrender;
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
            DetrendMode = p.DetrendMode;
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
            StabilityWindow: StabilityWindow,
            StabilityThresholdDegrees: StabilityThreshold,
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
                    if (!sample.IsWarmup && sample.IsValid && !double.IsNaN(sample.CycleStability))
                    {
                        _values[segStart + k] = sample.TrendMode ? 1m : 0m;
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
