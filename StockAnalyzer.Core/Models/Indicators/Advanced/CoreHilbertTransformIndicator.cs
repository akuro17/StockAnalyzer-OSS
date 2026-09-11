using System;
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
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            prices[i] = series[i] ?? 0m;
        }

        // The Ehlers Canonical Homodyne Discriminator pipeline lives in exactly one place:
        // HilbertDecompositionEngine. This indicator is now a thin projection of that engine's
        // result onto its historical three-series output contract (Main = DominantCycle,
        // InPhase = i2, Quadrature = q2), with a null prefix over the warm-up window.
        //
        // Parameter sanitation (approved "Plan A"): HilbertDecompositionParameters.Validate()
        // rejects two configurations the previous inline implementation silently tolerated --
        // DeltaLimit <= 0 (previously absorbed by Math.Max(DeltaLimit, epsilon) at the use site)
        // and DefaultPeriod outside [MinPeriod, MaxPeriod] (previously only a seed value). Clamp
        // both here so behavior is preserved: the DeltaLimit floor is byte-identical to the old
        // safeDeltaLimit guard, and the DefaultPeriod clamp is a no-op for every valid config.
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
            WarmupBars: warmupBars);

        var decomposition = HilbertDecompositionEngine.Decompose(prices, parameters);

        for (int i = 0; i < count; i++)
        {
            var sample = decomposition[i];

            // Gate on the bar index (not sample.IsWarmup): the engine's single-bar fast path
            // reports IsWarmup == false, whereas the historical contract emits null for every
            // bar before warmupBars regardless of series length.
            if (i < warmupBars)
            {
                _values.Add(null);
                InPhase.Add(null);
                Quadrature.Add(null);
            }
            else
            {
                _values.Add(sample.DominantCycle);
                InPhase.Add(sample.InPhase);
                Quadrature.Add(sample.Quadrature);
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
