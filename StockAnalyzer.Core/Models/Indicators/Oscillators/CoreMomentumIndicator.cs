using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.Momentum)]
public class CoreMomentumIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 10;
    public override string Name => $"Momentum ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (Period <= 0)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < series.Count; i++)
        {
            if (i < Period || !series[i].HasValue || !series[i - Period].HasValue)
            {
                _values.Add(null);
                continue;
            }

            _values.Add(series[i]!.Value - series[i - Period]!.Value);
        }

        return IndicatorResult.Success(_values);
    }
}
