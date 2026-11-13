using System.Globalization;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

/// <summary>
/// McGinley Dynamic (John R. McGinley). With N = <see cref="Period"/>, k = <see cref="Constant"/> and the price source P:
/// <list type="number">
/// <item>Initial value: at the first bar where N positive prices (not null and &gt; 0) have been seen, MD is the mean of those N prices; before that bar the output is null. A null or non-positive price is not counted and its output is null.</item>
/// <item>After initialization a null or non-positive price gives a null output and keeps the state MD unchanged (no value is ever built from a non-positive price).</item>
/// <item>For a positive price: r = P / MD_prev; if r &gt; <see cref="IndicatorDefaultConstants.McGinleyMaxPriceRatio"/> then MD = MD_prev (decimal overflow guard; the step would be below 1e-17 of MD_prev);
/// otherwise den = k x N x r^4 (r squared, then squared again), den is raised to <see cref="IndicatorDefaultConstants.McGinleyMinDivisor"/> when smaller, and MD = MD_prev + (P - MD_prev) / den.</item>
/// <item>Defensive rule (cannot occur for positive prices, because MD stays between MD_prev and P): MD_prev &lt;= 0 gives a null output and restarts at step 1 with the next bar.</item>
/// </list>
/// The overflow guard is evaluated as P / ratio-limit &gt; MD_prev, which is r &gt; limit without forming r first (r itself could overflow the decimal range for a tiny MD_prev).
/// Unit: price-native. Causal: bar t uses bars &lt;= t only. Output length equals the input length.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.McGinleyDynamic)]
public class CoreMcGinleyDynamicIndicator : CoreIndicatorBase
{
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.McGinleyDefaultPeriod;
    public decimal Constant { get; set; } = IndicatorDefaultConstants.McGinleyDefaultConstant;

    public override string Name => string.Create(CultureInfo.InvariantCulture, $"McGinley Dynamic ({Period}, {Constant})");

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreMcGinleyDynamicParameter p)
        {
            p.Validate();
            Period = p.Period;
            Constant = p.Constant;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        int count = series.Count;
        int period = Period;
        decimal constant = Constant;
        _values.Capacity = count;

        bool initialized = false;
        decimal md = 0m;
        decimal positiveSum = 0m;
        int positiveCount = 0;

        for (int i = 0; i < count; i++)
        {
            decimal? price = series[i];
            if (price is null || price.Value <= 0m)
            {
                _values.Add(null);
                continue;
            }

            decimal p = price.Value;
            if (!initialized)
            {
                positiveSum += p;
                positiveCount++;
                if (positiveCount < period)
                {
                    _values.Add(null);
                    continue;
                }

                md = positiveSum / period;
                initialized = true;
                _values.Add(md);
                continue;
            }

            if (md <= 0m)
            {
                _values.Add(null);
                initialized = false;
                positiveSum = 0m;
                positiveCount = 0;
                continue;
            }

            if (p / IndicatorDefaultConstants.McGinleyMaxPriceRatio > md)
            {
                _values.Add(md);
                continue;
            }

            decimal ratio = p / md;
            decimal ratioSquared = ratio * ratio;
            decimal divisor = constant * period * (ratioSquared * ratioSquared);
            if (divisor < IndicatorDefaultConstants.McGinleyMinDivisor) divisor = IndicatorDefaultConstants.McGinleyMinDivisor;

            md += (p - md) / divisor;
            _values.Add(md);
        }

        return IndicatorResult.Success(_values);
    }
}
