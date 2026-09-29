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

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period) { _values.Add(null); continue; }
            _values.Add((priceSeries[i] ?? 0m) - (priceSeries[i - Period] ?? 0m));
        }

        return IndicatorResult.Success(_values);
    }
}
