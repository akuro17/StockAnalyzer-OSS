using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.Garch11)]
public class CoreGarch11Indicator : CoreIndicatorBase
{
    public override string Name => "GARCH(1,1)";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // GARCH Implementation Placeholder
        _values.Clear();
        foreach(var c in candles) _values.Add(null);

        return IndicatorResult.Success(_values);
    }
}
