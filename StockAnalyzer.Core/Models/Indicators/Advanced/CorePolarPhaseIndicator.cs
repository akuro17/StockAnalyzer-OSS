using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Polar Phase indicator.
/// Builds the analytic signal of the price series with the Ehlers Canonical Homodyne Discriminator
/// (<see cref="HilbertDecompositionEngine"/>, reused unchanged via
/// <see cref="PolarCoordinateDecompositionEngine"/>) and reports the instantaneous phase in
/// <c>[0, 360)</c> degrees plus the unit phasor components (<see cref="NormalizedInPhase"/> /
/// <see cref="NormalizedQuadrature"/>, each bounded to <c>[-1, 1]</c>) and the trailing phase
/// stability. Sub-panel; causal (never repaints).
/// Sign convention: <c>theta = atan2(-Q, I)</c> (Ehlers), passed through from the engine verbatim.
/// Warm-up / invalid bars are surfaced as <see langword="null"/> here (the engine itself only
/// flags them via <c>IsWarmup</c> / <c>IsValid</c>).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PolarPhase)]
public class CorePolarPhaseIndicator : CoreIndicatorBase
{
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    /// <summary>
    /// Friendly Screener output name for the base "Main" series (instantaneous phase, 0-360 deg).
    /// The 0-360 "Main" series has no public alias property (a second distinctly-named series would
    /// escape the renderer's Main-only chart-suppression guard and re-pollute the [-1, 1] panel
    /// axis); its friendly name is injected in <c>ScreenerCatalogProvider.GetOutputSeriesNames</c>
    /// instead, mirroring <c>CoreIfftInstantaneousPhaseIndicator</c>.
    /// </summary>
    public const string ScreenerPhaseAngleOutputName = "PolarPhaseAngle";

    public override string Name => $"Polar Phase ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => false;
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    /// <summary>I / r, the in-phase component of the unit phasor. Bounded to [-1, 1]. Null on warm-up.</summary>
    public List<decimal?> NormalizedInPhase { get; } = new();

    /// <summary>Q / r, the quadrature component of the unit phasor. Bounded to [-1, 1]. Null on warm-up.</summary>
    public List<decimal?> NormalizedQuadrature { get; } = new();

    /// <summary>
    /// Population standard deviation (degrees) of the trailing phase deltas. Lower values indicate
    /// a more stable (more likely genuinely cyclical) phase rotation. Diagnostic statistic, not a
    /// pre-thresholded validity flag. Not charted (its degree scale would dominate the [-1, 1] panel).
    /// </summary>
    public List<decimal?> PhaseStability { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePolarPhaseParameter p)
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
        NormalizedInPhase.Clear();
        NormalizedQuadrature.Clear();
        PhaseStability.Clear();

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
                if (s.IsWarmup || !s.IsValid)
                {
                    _values.Add(null);
                    NormalizedInPhase.Add(null);
                    NormalizedQuadrature.Add(null);
                    PhaseStability.Add(null);
                }
                else
                {
                    _values.Add((decimal)s.PhaseDegrees);
                    NormalizedInPhase.Add(s.NormalizedInPhase);
                    NormalizedQuadrature.Add(s.NormalizedQuadrature);
                    PhaseStability.Add(double.IsNaN(s.PhaseStability) ? (decimal?)null : (decimal)s.PhaseStability);
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
