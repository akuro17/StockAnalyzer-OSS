using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.ROC)]
public class CoreRocIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 12;
    public RocDisplayStyle DisplayStyle { get; set; } = RocDisplayStyle.Line;

    public override string Name => $"ROC ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRocParameter param)
        {
            Period = param.Period;
            DisplayStyle = param.DisplayStyle;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (Period <= 0)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
        }
        else
        {
            for (int i = 0; i < series.Count; i++)
            {
                if (i < Period || !series[i].HasValue || !series[i - Period].HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                decimal prev = series[i - Period]!.Value;
                _values.Add(prev == 0m ? 0m : ((series[i]!.Value - prev) / prev) * 100m);
            }
        }

        var resultSeries = new System.Collections.Generic.Dictionary<string, IReadOnlyList<decimal?>>();
        string seriesName = DisplayStyle == RocDisplayStyle.Histogram ? "Histogram" : IndicatorResult.MainSeriesName;
        resultSeries[seriesName] = _values;

        return IndicatorResult.Success(resultSeries);
    }
}
