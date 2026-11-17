using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Attributes;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Normalised Shannon entropy of the log returns r_k = ln(P_k / P_(k-1)) of the last <see cref="Period"/> bars. With lo and hi the smallest and largest return of the window, the
/// returns are put into <see cref="BinCount"/> equal-width bins, b = min(BinCount - 1, floor((r - lo) / (hi - lo) x BinCount)); p_b = count / Period and
/// <c>Main</c> = -sum over p_b &gt; 0 of p_b ln p_b / ln(BinCount), which is 0 when hi = lo. Unit: ratio (dimensionless) in [0, 1] (the unnormalised -sum p log p of the older
/// specification is divided by ln(BinCount), the maximum for BinCount bins). Null for bars before bar <see cref="Period"/> and when the window has an unusable price.
/// Also computes companion series <c>PermutationEntropy</c> (normalized ordinal entropy in [0, 1]) and <c>SampleEntropy</c> (Richman-Moorman nats).
/// Causal: bar t uses bars &lt;= t only. Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Entropy)]
public class CoreEntropyIndicator : CoreIndicatorBase
{
    public const string PermutationEntropySeriesName = nameof(PermutationEntropy);
    public const string SampleEntropySeriesName = nameof(SampleEntropy);

    public override bool IsOverlay => false;

    public int Period { get; set; } = IndicatorDefaultConstants.EntropyDefaultPeriod;

    public int BinCount { get; set; } = IndicatorDefaultConstants.EntropyDefaultBinCount;

    public int EmbeddingDimension { get; set; } = IndicatorDefaultConstants.EntropyDefaultEmbeddingDimension;

    public int SampleEntropyDimension { get; set; } = IndicatorDefaultConstants.EntropyDefaultSampleDimension;

    public double ToleranceMultiplier { get; set; } = IndicatorDefaultConstants.EntropyDefaultToleranceMultiplier;

    [IndicatorResultIgnore]
    public List<decimal?> PermutationEntropy { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> SampleEntropy { get; } = new();

    public override string Name =>
        EmbeddingDimension == IndicatorDefaultConstants.EntropyDefaultEmbeddingDimension &&
        SampleEntropyDimension == IndicatorDefaultConstants.EntropyDefaultSampleDimension &&
        ToleranceMultiplier == IndicatorDefaultConstants.EntropyDefaultToleranceMultiplier
            ? $"Entropy ({Period}, {BinCount})"
            : $"Entropy ({Period}, {BinCount}, d:{EmbeddingDimension}, m:{SampleEntropyDimension})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreEntropyParameter p)
        {
            p.Validate();
            Period = p.Period;
            BinCount = p.BinCount;
            EmbeddingDimension = p.EmbeddingDimension;
            SampleEntropyDimension = p.SampleEntropyDimension;
            ToleranceMultiplier = p.ToleranceMultiplier;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        PermutationEntropy.Clear();
        SampleEntropy.Clear();

        var parameters = new CoreEntropyParameter
        {
            Period = Period,
            BinCount = BinCount,
            EmbeddingDimension = EmbeddingDimension,
            SampleEntropyDimension = SampleEntropyDimension,
            ToleranceMultiplier = ToleranceMultiplier
        };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        var returns = new LogReturnWindow(PriceDataHelper.ExtractPriceSeries(candles, PriceSource));
        double[] values = returns.Returns;
        double logBinCount = Math.Log(BinCount);

        // One counts array for the whole calculation, reset for every bar.
        var counts = new int[BinCount];

        for (int i = 0; i < returns.Count; i++)
        {
            int first = i - Period + 1;
            if (i < Period || !returns.IsValid(first, i))
            {
                _values.Add(null);
                PermutationEntropy.Add(null);
                SampleEntropy.Add(null);
                continue;
            }

            ReadOnlySpan<double> window = values.AsSpan(first, Period);
            double permEntropy = ShannonEntropyMath.PermutationEntropy(window, EmbeddingDimension);
            double sampEntropy = ShannonEntropyMath.SampleEntropy(window, SampleEntropyDimension, ToleranceMultiplier);
            PermutationEntropy.Add(IndicatorCalculationHelper.ToDecimalOrNull(permEntropy));
            SampleEntropy.Add(IndicatorCalculationHelper.ToDecimalOrNull(sampEntropy));

            double lo = double.PositiveInfinity;
            double hi = double.NegativeInfinity;
            for (int k = first; k <= i; k++)
            {
                if (values[k] < lo) lo = values[k];
                if (values[k] > hi) hi = values[k];
            }

            if (hi == lo)
            {
                _values.Add(0m);
                continue;
            }

            Array.Clear(counts);
            double range = hi - lo;
            for (int k = first; k <= i; k++)
            {
                int bin = (int)Math.Floor((values[k] - lo) / range * BinCount);
                counts[Math.Min(BinCount - 1, bin)]++;
            }

            double entropy = ShannonEntropyMath.Nats(counts, Period);

            _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(entropy / logBinCount));
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { PermutationEntropySeriesName, PermutationEntropy },
            { SampleEntropySeriesName, SampleEntropy }
        });
    }
}
