using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.DEMA)]
public class CoreDemaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"DEMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);
        if (Period <= 0)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        var ema1 = IndicatorCalculationHelper.CalculateEmaWithNulls(series, Period);
        var ema2 = IndicatorCalculationHelper.CalculateEmaWithNulls(ema1, Period);

        for (int i = 0; i < series.Count; i++)
        {
            if (ema1[i].HasValue && ema2[i].HasValue)
            {
                _values.Add(2m * ema1[i]!.Value - ema2[i]!.Value);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
