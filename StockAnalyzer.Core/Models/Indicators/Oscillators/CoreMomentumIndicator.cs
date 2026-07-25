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

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period) { _values.Add(null); continue; }
            _values.Add(candles[i].Close - candles[i - Period].Close);
        }

        return IndicatorResult.Success(_values);
    }
}
