using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

/// <summary>
/// Stochastic RSI (Tushar Chande and Stanley Kroll; the original range is 0..1, this implementation scales it to 0..100).
/// RSI = <see cref="CoreRsiIndicator"/> with <see cref="RsiPeriod"/> bars on the price source; hi/lo = highest/lowest RSI over <see cref="StochPeriod"/> bars (null when any RSI in the window is null);
/// raw = 100 x (RSI - lo) / (hi - lo), or <see cref="IndicatorCalculationHelper.BoundedOscillatorNeutral"/> when hi = lo;
/// <c>Main</c> (%K) = SMA(raw, <see cref="KPeriod"/>); <c>PercentD</c> = SMA(%K, <see cref="DPeriod"/>) (series names as in the Stochastic indicator).
/// Unit: oscillator in [0, 100]. Causal: bar t uses bars &lt;= t only. A bar with an undefined RSI, window or smoothing input is null. Output length equals the candle count.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.StochRSI)]
public class CoreStochRsiIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => false;

    public int RsiPeriod { get; set; } = IndicatorDefaultConstants.StochRsiDefaultRsiPeriod;
    public int StochPeriod { get; set; } = IndicatorDefaultConstants.StochRsiDefaultStochPeriod;
    public int KPeriod { get; set; } = IndicatorDefaultConstants.StochRsiDefaultKPeriod;
    public int DPeriod { get; set; } = IndicatorDefaultConstants.StochRsiDefaultDPeriod;

    public override string Name => $"StochRSI ({RsiPeriod}, {StochPeriod}, {KPeriod}, {DPeriod})";

    /// <summary>
    /// The Main series (%K) under the output name "PercentK" (as in the Stochastic indicator). Output-name discovery lists the public series
    /// properties, so without this alias the PercentD property would hide the main output.
    /// </summary>
    public IReadOnlyList<decimal?> PercentK => _values;

    /// <summary>%D: the simple moving average of %K over <see cref="DPeriod"/> bars.</summary>
    public List<decimal?> PercentD { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreStochRsiParameter p)
        {
            p.Validate();
            RsiPeriod = p.RsiPeriod;
            StochPeriod = p.StochPeriod;
            KPeriod = p.KPeriod;
            DPeriod = p.DPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        PercentD.Clear();
        int count = candles.Count;

        var rsiIndicator = new CoreRsiIndicator { Period = RsiPeriod, PriceSource = PriceSource };
        rsiIndicator.Calculate(candles);
        var rsi = rsiIndicator.Values;

        // CoreRsiIndicator leaves its list empty for fewer than two candles.
        if (rsi.Count != count)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
                PercentD.Add(null);
            }

            return BuildResult();
        }

        var highest = RollingExtremeHelper.CalculateRollingMax(rsi, StochPeriod);
        var lowest = RollingExtremeHelper.CalculateRollingMin(rsi, StochPeriod);

        var raw = new List<decimal?>(count);
        for (int i = 0; i < count; i++)
        {
            if (rsi[i] is null || highest[i] is null || lowest[i] is null)
            {
                raw.Add(null);
            }
            else if (highest[i]!.Value == lowest[i]!.Value)
            {
                raw.Add(IndicatorCalculationHelper.BoundedOscillatorNeutral);
            }
            else
            {
                raw.Add(ChartConstants.PercentageMultiplier * (rsi[i]!.Value - lowest[i]!.Value) / (highest[i]!.Value - lowest[i]!.Value));
            }
        }

        _values.AddRange(ClampToUnitRange(IndicatorCalculationHelper.CalculateSmaWithNulls(raw, KPeriod)));
        PercentD.AddRange(ClampToUnitRange(IndicatorCalculationHelper.CalculateSmaWithNulls(_values, DPeriod)));
        return BuildResult();
    }

    /// <summary>
    /// The mean of values inside [0, 100] is inside [0, 100] mathematically, but the shared running-sum SMA can leave a decimal rounding residue of about 1e-26
    /// outside it (seen on the Scatter audit series). The declared unit range is the contract of the fixed axis, so the residue is removed here (values inside the range are not touched).
    /// </summary>
    private static List<decimal?> ClampToUnitRange(List<decimal?> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is decimal v)
            {
                if (v < IndicatorDefaultConstants.StochMinValue) values[i] = IndicatorDefaultConstants.StochMinValue;
                else if (v > IndicatorDefaultConstants.StochMaxValue) values[i] = IndicatorDefaultConstants.StochMaxValue;
            }
        }

        return values;
    }

    private IIndicatorResult BuildResult()
    {
        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { nameof(PercentD), PercentD },
        });
    }
}
