using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

/// <summary>
/// True Strength Index (William Blau). With P the price source: m_0 = null, m_i = P_i - P_(i-1) (null when either price is null), a_i = |m_i|;
/// PC = EMA(EMA(m, LongPeriod), ShortPeriod), APC = EMA(EMA(a, LongPeriod), ShortPeriod);
/// <c>Main</c> = 100 x PC / APC (0 when APC = 0; null when PC or APC is null); <c>Signal</c> = EMA(Main, SignalPeriod); <c>Histogram</c> = Main - Signal.
/// Unit: oscillator in [-100, 100] (the EMA is the shared SMA-seeded EMA with nulls). Causal: bar t uses bars &lt;= t only.
/// For an input without gaps the first Main value is at bar index 1 + (LongPeriod - 1) + (ShortPeriod - 1). Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TSI)]
public class CoreTsiIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int LongPeriod { get; set; } = IndicatorDefaultConstants.TsiDefaultLongPeriod;
    public int ShortPeriod { get; set; } = IndicatorDefaultConstants.TsiDefaultShortPeriod;
    public int SignalPeriod { get; set; } = IndicatorDefaultConstants.TsiDefaultSignalPeriod;

    public override string Name => $"TSI ({LongPeriod}, {ShortPeriod}, {SignalPeriod})";

    /// <summary>
    /// The Main series under its output name "TSI". Output-name discovery (IndicatorOutputSeriesResolver) lists the public series properties,
    /// so without this alias the Signal and Histogram properties would hide the main output.
    /// </summary>
    public IReadOnlyList<decimal?> TSI => _values;

    /// <summary>The signal line: EMA of the TSI over <see cref="SignalPeriod"/> bars.</summary>
    public List<decimal?> Signal { get; } = new();

    /// <summary>TSI minus Signal.</summary>
    public List<decimal?> Histogram { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreTsiParameter p)
        {
            p.Validate();
            LongPeriod = p.LongPeriod;
            ShortPeriod = p.ShortPeriod;
            SignalPeriod = p.SignalPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        Signal.Clear();
        Histogram.Clear();

        int count = candles.Count;
        var prices = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

        var change = new List<decimal?>(count);
        var absoluteChange = new List<decimal?>(count);
        for (int i = 0; i < count; i++)
        {
            if (i == 0 || prices[i] is null || prices[i - 1] is null)
            {
                change.Add(null);
                absoluteChange.Add(null);
                continue;
            }

            decimal m = prices[i]!.Value - prices[i - 1]!.Value;
            change.Add(m);
            absoluteChange.Add(Math.Abs(m));
        }

        var pc = IndicatorCalculationHelper.CalculateEmaWithNulls(IndicatorCalculationHelper.CalculateEmaWithNulls(change, LongPeriod), ShortPeriod);
        var apc = IndicatorCalculationHelper.CalculateEmaWithNulls(IndicatorCalculationHelper.CalculateEmaWithNulls(absoluteChange, LongPeriod), ShortPeriod);

        _values.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            if (pc[i] is null || apc[i] is null) _values.Add(null);
            else if (apc[i]!.Value == 0m) _values.Add(0m);
            else _values.Add(ChartConstants.PercentageMultiplier * pc[i]!.Value / apc[i]!.Value);
        }

        var signal = IndicatorCalculationHelper.CalculateEmaWithNulls(_values, SignalPeriod);
        Signal.Capacity = count;
        Histogram.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            Signal.Add(signal[i]);
            Histogram.Add(_values[i] is null || signal[i] is null ? null : _values[i]!.Value - signal[i]!.Value);
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { nameof(Signal), Signal },
            { nameof(Histogram), Histogram },
        });
    }
}
