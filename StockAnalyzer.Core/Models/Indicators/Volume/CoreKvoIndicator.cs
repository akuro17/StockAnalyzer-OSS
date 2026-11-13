using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volume;

/// <summary>
/// Klinger Volume Oscillator (Stephen Klinger; owner decision D-16=A for the volume force form). With hlc = H + L + C and dm = H - L:
/// trend T_i = +1 when hlc_i &gt; hlc_(i-1), otherwise -1 (undefined at bar 0); cm_1 = dm_0 + dm_1, then cm_i = cm_(i-1) + dm_i when T_i = T_(i-1), else cm_i = dm_(i-1) + dm_i;
/// volume force VF_0 = null, VF_i = 0 when cm_i = 0, else V_i x |2 x dm_i / cm_i - 1| x T_i x 100.
/// <c>Main</c> = EMA(VF, <see cref="FastPeriod"/>) - EMA(VF, <see cref="SlowPeriod"/>) (null when either is null); <c>Signal</c> = EMA(Main, <see cref="SignalPeriod"/>); <c>Histogram</c> = Main - Signal.
/// Unit: volume force (share volume x 100, not bounded). Causal: bar t uses bars &lt;= t only. Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.KVO)]
public class CoreKvoIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int FastPeriod { get; set; } = IndicatorDefaultConstants.KvoDefaultFastPeriod;
    public int SlowPeriod { get; set; } = IndicatorDefaultConstants.KvoDefaultSlowPeriod;
    public int SignalPeriod { get; set; } = IndicatorDefaultConstants.KvoDefaultSignalPeriod;

    public override string Name => $"KVO ({FastPeriod}, {SlowPeriod}, {SignalPeriod})";

    /// <summary>
    /// The Main series under its output name "KVO". Output-name discovery lists the public series properties, so without this alias the
    /// Signal and Histogram properties would hide the main output.
    /// </summary>
    public IReadOnlyList<decimal?> KVO => _values;

    /// <summary>The signal line: EMA of the KVO over <see cref="SignalPeriod"/> bars.</summary>
    public List<decimal?> Signal { get; } = new();

    /// <summary>KVO minus Signal.</summary>
    public List<decimal?> Histogram { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKvoParameter p)
        {
            p.Validate();
            FastPeriod = p.FastPeriod;
            SlowPeriod = p.SlowPeriod;
            SignalPeriod = p.SignalPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        Signal.Clear();
        Histogram.Clear();
        int count = candles.Count;

        var volumeForce = new List<decimal?>(count);
        decimal previousHlc = 0m;
        decimal previousDm = 0m;
        decimal cumulativeMeasurement = 0m;
        int previousTrend = 0;

        for (int i = 0; i < count; i++)
        {
            var candle = candles[i];
            decimal hlc = candle.High + candle.Low + candle.Close;
            decimal dm = candle.High - candle.Low;

            if (i == 0)
            {
                volumeForce.Add(null);
                previousHlc = hlc;
                previousDm = dm;
                continue;
            }

            int trend = hlc > previousHlc ? 1 : -1;
            if (i == 1) cumulativeMeasurement = previousDm + dm;
            else if (trend == previousTrend) cumulativeMeasurement += dm;
            else cumulativeMeasurement = previousDm + dm;

            volumeForce.Add(cumulativeMeasurement == 0m
                ? 0m
                : candle.Volume * Math.Abs(2m * dm / cumulativeMeasurement - 1m) * trend * IndicatorDefaultConstants.KvoVolumeForceScale);

            previousHlc = hlc;
            previousDm = dm;
            previousTrend = trend;
        }

        var fast = IndicatorCalculationHelper.CalculateEmaWithNulls(volumeForce, FastPeriod);
        var slow = IndicatorCalculationHelper.CalculateEmaWithNulls(volumeForce, SlowPeriod);

        _values.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            _values.Add(fast[i] is null || slow[i] is null ? null : fast[i]!.Value - slow[i]!.Value);
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
