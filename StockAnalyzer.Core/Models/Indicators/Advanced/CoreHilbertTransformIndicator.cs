using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
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
    public HilbertDetrendMode DetrendMode { get; set; } = HilbertDetrendMode.FirDetrender;

    public override string Name => $"Hilbert Transform ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => false;

    // Typical Price (H+L+C)/3 gives superior frequency extraction fidelity and remains the default,
    // but is now a configurable PriceSource like every other indicator rather than a hardcoded formula.
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

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
            DetrendMode = p.DetrendMode;
        }
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
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            InPhase.Add(null);
            Quadrature.Add(null);
        }

        // The Ehlers Canonical Homodyne Discriminator pipeline lives in exactly one place:
        // HilbertDecompositionEngine. This indicator is now a thin projection of that engine's
        // result onto its historical three-series output contract (Main = DominantCycle,
        // InPhase = i2, Quadrature = q2), with a null prefix over the warm-up window.
        const decimal deltaLimitFloor = 1e-10m;
        decimal safeDeltaLimit = Math.Max(DeltaLimit, deltaLimitFloor);
        int seedDefaultPeriod = Math.Clamp(DefaultPeriod, MinPeriod, MaxPeriod);
        const int warmupBars = IndicatorDefaultConstants.HilbertTransformWarmupBars;

        var parameters = new HilbertDecompositionParameters(
            DefaultPeriod: seedDefaultPeriod,
            MinPeriod: MinPeriod,
            MaxPeriod: MaxPeriod,
            SmoothBeta: SmoothBeta,
            DeltaLimit: safeDeltaLimit,
            WarmupBars: warmupBars,
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

                var decomposition = HilbertDecompositionEngine.Decompose(segPrices.AsSpan(0, segLen), parameters);

                for (int k = warmupBars; k < segLen; k++)
                {
                    var sample = decomposition[k];
                    _values[segStart + k] = sample.DominantCycle;
                    InPhase[segStart + k] = sample.InPhase;
                    Quadrature[segStart + k] = sample.Quadrature;
                }
            }
            finally
            {
                ArrayPool<decimal>.Shared.Return(segPrices);
            }

            segStart = segEnd;
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
