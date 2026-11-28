using Math = System.Math;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.CMO)]
public class CoreCmoIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"CMO ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series.Count < 2) return IndicatorResult.Success(_values);
        if (Period <= 0)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        _values.Add(null);

        for (int i = 1; i < series.Count; i++)
        {
            if (i < Period)
            {
                _values.Add(null);
                continue;
            }

            decimal sumUp = 0m, sumDown = 0m;
            bool hasNull = false;
            for (int j = 0; j < Period; j++)
            {
                decimal? curr = series[i - j];
                decimal? prev = series[i - j - 1];
                if (!curr.HasValue || !prev.HasValue)
                {
                    hasNull = true;
                    break;
                }

                decimal change = curr.Value - prev.Value;
                if (change > 0m) sumUp += change;
                else sumDown += Math.Abs(change);
            }

            if (hasNull)
            {
                _values.Add(null);
                continue;
            }

            decimal denom = sumUp + sumDown;
            decimal cmo = denom == 0m ? 0m : ((sumUp - sumDown) / denom) * 100m;
            _values.Add(cmo);
        }

        return IndicatorResult.Success(_values);
    }
}
