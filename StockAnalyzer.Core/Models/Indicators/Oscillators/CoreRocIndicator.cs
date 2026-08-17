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

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period) { _values.Add(null); continue; }
            decimal prev = candles[i - Period].Close;
            _values.Add(prev == 0 ? 0 : ((candles[i].Close - prev) / prev) * 100);
        }

        var series = new System.Collections.Generic.Dictionary<string, IReadOnlyList<decimal?>>();
        string seriesName = DisplayStyle == RocDisplayStyle.Histogram ? "Histogram" : IndicatorResult.MainSeriesName;
        series[seriesName] = _values;

        return IndicatorResult.Success(series);
    }
}
