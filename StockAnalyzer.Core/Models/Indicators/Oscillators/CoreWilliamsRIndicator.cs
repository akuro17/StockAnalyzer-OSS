using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.WilliamsR)]
public class CoreWilliamsRIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"Williams %R ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1) { _values.Add(null); continue; }

            decimal highest = decimal.MinValue;
            decimal lowest = decimal.MaxValue;
            for (int j = 0; j < Period; j++)
            {
                highest = Math.Max(highest, candles[i - j].High);
                lowest = Math.Min(lowest, candles[i - j].Low);
            }

            decimal wr = highest == lowest ? -50 : ((highest - candles[i].Close) / (highest - lowest)) * -100;
            _values.Add(wr);
        }

        return IndicatorResult.Success(_values);
    }
}
