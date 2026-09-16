using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Polar Cycle Angular Frequency indicator.
/// Builds the analytic signal of the price series with the Ehlers Canonical Homodyne Discriminator
/// (<see cref="HilbertDecompositionEngine"/>, reused unchanged via
/// <see cref="PolarCoordinateDecompositionEngine"/>) and plots the cycle angular frequency (a
/// cycle/period-derived rate, not the bar-to-bar phase-difference angular velocity)
/// <c>omega = 2*pi / InstantaneousPeriod</c> (radians per bar). Because it is derived from the
/// Homodyne-discriminator period rather than a bar-to-bar arctan difference, it has no +/-180 deg
/// wrap-around discontinuity. Sub-panel; causal (never repaints).
/// The dominant-cycle period in bars is already served by the Hilbert Transform indicator; this
/// indicator deliberately exposes only the angular-frequency form to keep its unit single.
/// Also surfaces two diagnostic series on the same rad/bar sub-panel: <see cref="PhaseAngularVelocity"/>
/// (the measured bar-to-bar phase-difference rate) and <see cref="PhaseFrequencyError"/>
/// (|cycle rate - phase rate|, near zero while the dominant cycle is stationary).
/// Warm-up / invalid bars are surfaced as <see langword="null"/> here (the engine itself only
/// flags them via <c>IsWarmup</c> / <c>IsValid</c>).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PolarCycleAngularFrequency)]
public class CorePolarCycleAngularFrequencyIndicator : CoreIndicatorBase
{
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    /// <summary>
    /// Friendly Screener output name for the base "Main" series (cycle angular frequency, rad/bar).
    /// Since this indicator now emits named diagnostic series, the reflective series discovery in
    /// <c>ScreenerCatalogProvider.GetOutputSeriesNames</c> no longer falls back to the enum name for
    /// "Main"; its friendly name is injected there instead, mirroring <c>CorePolarPhaseIndicator</c>.
    /// Bound to the enum member name via <c>nameof</c>, so it still equals the string a pre-existing
    /// screener reference uses and it tracks any future rename of the member.
    /// </summary>
    public const string ScreenerCycleAngularFrequencyOutputName = nameof(IndicatorType.PolarCycleAngularFrequency);

    public override string Name => $"Polar Cycle Angular Frequency ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => false;
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    /// <summary>
    /// Measured bar-to-bar phase-difference angular velocity (rad/bar): the unwrapped-phase delta
    /// converted to rad/bar, passed through from the engine. Carries arctan phase-difference noise
    /// (unlike the smooth period-derived Main series). Null on warm-up / invalid bars.
    /// </summary>
    public List<decimal?> PhaseAngularVelocity { get; } = new();

    /// <summary>
    /// |Main cycle rate - <see cref="PhaseAngularVelocity"/>| (rad/bar): deviation of the
    /// period-derived cycle rate from the measured phase progression. Near zero while the dominant
    /// cycle is stationary; grows during regime shifts / phase noise. Diagnostic statistic, not a
    /// validity flag. Null on warm-up / invalid bars or when the cycle rate is NaN.
    /// </summary>
    public List<decimal?> PhaseFrequencyError { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePolarCycleAngularFrequencyParameter p)
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
        PhaseAngularVelocity.Clear();
        PhaseFrequencyError.Clear();

        if (series == null || series.Count == 0)
        {
            return CreateAutomaticResult();
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

            var decomp = PolarCoordinateDecompositionEngine.Decompose(prices.AsSpan(0, count), decompositionParams);

            for (int i = 0; i < count; i++)
            {
                var s = decomp[i];
                if (s.IsWarmup || !s.IsValid || double.IsNaN(s.CycleAngularFrequency))
                {
                    _values.Add(null);
                    PhaseAngularVelocity.Add(null);
                    PhaseFrequencyError.Add(null);
                }
                else
                {
                    _values.Add((decimal)s.CycleAngularFrequency);
                    PhaseAngularVelocity.Add(double.IsNaN(s.PhaseAngularVelocity) ? (decimal?)null : (decimal)s.PhaseAngularVelocity);
                    PhaseFrequencyError.Add(double.IsNaN(s.PhaseFrequencyError) ? (decimal?)null : (decimal)s.PhaseFrequencyError);
                }
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
        }

        return CreateAutomaticResult();
    }
}
