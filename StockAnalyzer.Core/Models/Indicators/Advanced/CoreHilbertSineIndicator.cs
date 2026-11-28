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
    public HilbertDetrendMode DetrendMode { get; set; } = HilbertDetrendMode.FirDetrender;

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
            DetrendMode = p.DetrendMode;
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
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            Sine.Add(null);
            LeadSine.Add(null);
        }

        var decompositionParams = new HilbertDecompositionParameters(
            DefaultPeriod: DefaultPeriod,
            MinPeriod: MinPeriod,
            MaxPeriod: MaxPeriod,
            SmoothBeta: SmoothBeta,
            DeltaLimit: DeltaLimit,
            WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars,
            DetrendMode: DetrendMode);

        const double leadShift = IndicatorDefaultConstants.HilbertSineDefaultLeadPhaseRadians;

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
                        decimal? sineVal = IndicatorCalculationHelper.ToDecimalOrNull(Math.Sin(sample.PhaseRad));
                        decimal? leadSineVal = IndicatorCalculationHelper.ToDecimalOrNull(Math.Sin(sample.PhaseRad + leadShift));
                        if (sineVal.HasValue && leadSineVal.HasValue)
                        {
                            _values[segStart + k] = sineVal.Value;
                            Sine[segStart + k] = sineVal.Value;
                            LeadSine[segStart + k] = leadSineVal.Value;
                        }
                    }
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
            { "Sine", Sine },
            { "LeadSine", LeadSine }
        };

        return IndicatorResult.Success(resultSeries);
    }
}
