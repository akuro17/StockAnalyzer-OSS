using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Hilbert Transform SineWave and LeadSine indicator (HT_SINE).
/// Evaluates cyclical turning points: buying on Sine crossing above LeadSine,
/// selling on Sine crossing below LeadSine in cycle mode.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.HilbertSine)]
public class CoreHilbertSineIndicator : CoreIndicatorBase
{
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    public override string Name => $"Hilbert Sine Wave ({MinPeriod}-{MaxPeriod})";
    public override bool IsOverlay => false;
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    public List<decimal?> Sine { get; } = new();
    public List<decimal?> LeadSine { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreHilbertSineParameter p)
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
        Sine.Clear();
        LeadSine.Clear();

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

            const double leadShift = IndicatorDefaultConstants.HilbertSineDefaultLeadPhaseRadians;

            for (int i = 0; i < count; i++)
            {
                var sample = decomp[i];
                if (sample.IsWarmup || !sample.IsValid)
                {
                    _values.Add(null);
                    Sine.Add(null);
                    LeadSine.Add(null);
                }
                else
                {
                    decimal sineVal = (decimal)Math.Sin(sample.PhaseRad);
                    decimal leadSineVal = (decimal)Math.Sin(sample.PhaseRad + leadShift);

                    _values.Add(sineVal);
                    Sine.Add(sineVal);
                    LeadSine.Add(leadSineVal);
                }
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
        }

        var resultSeries = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { "Sine", Sine },
            { "LeadSine", LeadSine }
        };

        return IndicatorResult.Success(resultSeries);
    }
}
