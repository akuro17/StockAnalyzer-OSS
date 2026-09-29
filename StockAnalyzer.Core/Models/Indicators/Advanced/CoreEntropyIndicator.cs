using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

// [StockAnalyzerIndicator(IndicatorType.Entropy)] // Unimplemented stub
public class CoreEntropyIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"Entropy ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p) Period = p.Period;
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // Shannon Entropy Implementation Placeholder
        _values.Clear();
         foreach(var c in candles) _values.Add(null);

        return IndicatorResult.Success(_values);
    }
}
